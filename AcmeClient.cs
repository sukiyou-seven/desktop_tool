using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>ACME 协议异常。</summary>
public class AcmeException : Exception
{
    public AcmeException(string message) : base(message) { }
}

/// <summary>ACME 目录信息（目录 URL + 账户密钥路径）。</summary>
public class AcmeDirectoryInfo
{
    public string DirectoryUrl { get; }
    public string AccountKeyPath { get; }
    public AcmeDirectoryInfo(string directoryUrl, string accountKeyPath)
    {
        DirectoryUrl = directoryUrl;
        AccountKeyPath = accountKeyPath;
    }
}

/// <summary>一个 DNS-01 验证挑战（域名、授权/挑战 URL、TXT 记录值）。可序列化用于跨会话续作。</summary>
public class CertChallenge
{
    public string Domain { get; set; } = "";
    public string AuthorizationUrl { get; set; } = "";
    public string ChallengeUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string TxtValue { get; set; } = "";

    /// <summary>需要添加的 TXT 主机记录名。</summary>
    public string RecordName => "_acme-challenge." + Domain;
}

/// <summary>ACME 订单结果。</summary>
public class AcmeOrderResult
{
    public string OrderUrl { get; set; } = "";
    public string FinalizeUrl { get; set; } = "";
    public List<CertChallenge> Challenges { get; set; } = new();
}

/// <summary>
/// 原生 ACME v2 客户端（DNS-01 手动验证），直接对接 Let's Encrypt，
/// 不依赖 certbot / Python 环境。
/// </summary>
public class AcmeClient
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SeraphineAITool", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private readonly string _directoryUrl;
    private readonly ECDsa _accountKey;
    private readonly JsonElement _accountJwk;
    private readonly string _thumbprint;
    private string? _kid;
    private JsonElement _directory;

    public string Kid => _kid ?? "";
    public string Thumbprint => _thumbprint;

    public AcmeClient(string directoryUrl, ECDsa accountKey)
    {
        _directoryUrl = directoryUrl;
        _accountKey = accountKey;

        var p = accountKey.ExportParameters(false);
        var x = Base64Url(p.Q.X!);
        var y = Base64Url(p.Q.Y!);
        _accountJwk = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
        {
            kty = "EC",
            crv = "P-256",
            x,
            y,
        }));
        // JWK Thumbprint：RFC 7638，canonical JSON 按字典序排列字段
        var canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
        _thumbprint = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(canonical)));
    }

    /// <summary>恢复已注册账户的 kid（第二步续作时使用）。</summary>
    public void SetKid(string kid) => _kid = kid;

    /// <summary>注册/复用 ACME 账户，成功后 Kid 可用。</summary>
    public async Task RegisterAsync(string email)
    {
        var newAccountUrl = await DirUrlAsync("newAccount");
        var payload = JsonSerializer.Serialize(new
        {
            termsOfServiceAgreed = true,
            contact = new[] { "mailto:" + email },
        });
        var resp = await PostJwsAsync(newAccountUrl, payload, useKid: false);
        _kid = resp.Headers.Location?.ToString() ?? "";
        if (string.IsNullOrEmpty(_kid))
            throw new AcmeException("注册账户失败：未返回账户 URL");
    }

    /// <summary>创建 DNS-01 订单，返回订单/终态 URL 及各域名的验证挑战。</summary>
    public async Task<AcmeOrderResult> CreateOrderAsync(string domain)
    {
        var newOrderUrl = await DirUrlAsync("newOrder");
        var payload = JsonSerializer.Serialize(new
        {
            identifiers = new[] { new { type = "dns", value = domain } },
        });
        var resp = await PostJwsAsync(newOrderUrl, payload, useKid: true);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var result = new AcmeOrderResult
        {
            OrderUrl = resp.Headers.Location?.ToString() ?? "",
            FinalizeUrl = root.TryGetProperty("finalize", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "",
        };
        if (string.IsNullOrEmpty(result.OrderUrl))
            throw new AcmeException("创建订单失败：未返回订单 URL");

        if (root.TryGetProperty("authorizations", out var auths) && auths.ValueKind == JsonValueKind.Array)
        {
            foreach (var authUrlEl in auths.EnumerateArray())
            {
                var authUrl = authUrlEl.GetString() ?? "";
                using var authDoc = await GetJsonAsync(authUrl);
                var authRoot = authDoc.RootElement;

                var authDomain = authRoot.TryGetProperty("identifier", out var idEl) && idEl.ValueKind == JsonValueKind.Object
                    && idEl.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "";

                if (!authRoot.TryGetProperty("challenges", out var chs) || chs.ValueKind != JsonValueKind.Array) continue;
                foreach (var chEl in chs.EnumerateArray())
                {
                    var type = chEl.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                    if (type != "dns-01") continue;
                    var token = chEl.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String ? tok.GetString() ?? "" : "";
                    // key authorization = token + "." + thumbprint；TXT 值 = base64url(sha256(key authorization))
                    var keyAuth = token + "." + _thumbprint;
                    var txt = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuth)));
                    result.Challenges.Add(new CertChallenge
                    {
                        Domain = authDomain,
                        AuthorizationUrl = authUrl,
                        ChallengeUrl = chEl.TryGetProperty("url", out var cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString() ?? "" : "",
                        Token = token,
                        TxtValue = txt,
                    });
                }
            }
        }
        if (result.Challenges.Count == 0)
            throw new AcmeException("订单中未找到 DNS-01 验证挑战");
        return result;
    }

    /// <summary>提交挑战（让 CA 开始验证 DNS 记录）。</summary>
    public async Task TriggerChallengeAsync(CertChallenge ch)
        => await PostJwsAsync(ch.ChallengeUrl, "{}", useKid: true);

    /// <summary>查询授权当前状态（POST-as-GET），返回 status：pending / processing / valid / invalid / deactivated / revoked / expired。</summary>
    public async Task<string> GetAuthorizationStatusAsync(string authorizationUrl)
    {
        using var doc = await GetJsonAsync(authorizationUrl);
        var root = doc.RootElement;
        return root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
    }

    /// <summary>查询订单当前状态（POST-as-GET），返回 status：pending / ready / processing / valid / invalid。</summary>
    public async Task<string> GetOrderStatusAsync(string orderUrl)
    {
        using var doc = await GetJsonAsync(orderUrl);
        var root = doc.RootElement;
        return root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
    }

    /// <summary>轮询授权直到 valid / invalid / 超时。</summary>
    public async Task PollAuthorizationAsync(CertChallenge ch, int timeoutSeconds = 300)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            using var doc = await GetJsonAsync(ch.AuthorizationUrl);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            if (status == "valid") return;
            if (status == "invalid")
                throw new AcmeException($"域名 {ch.Domain} 验证失败：{DescribeError(root)}");
            if (status is "deactivated" or "revoked" or "expired")
                throw new AcmeException($"域名 {ch.Domain} 授权状态异常：{status}");
            await Task.Delay(3000);
        }
        throw new AcmeException($"等待域名 {ch.Domain} 验证超时");
    }

    /// <summary>提交 CSR，触发证书签发。</summary>
    public async Task FinalizeOrderAsync(string finalizeUrl, byte[] csrDer)
    {
        var payload = JsonSerializer.Serialize(new { csr = Base64Url(csrDer) });
        await PostJwsAsync(finalizeUrl, payload, useKid: true);
    }

    /// <summary>轮询订单直到 valid，返回证书 URL。</summary>
    public async Task<string> PollOrderAsync(string orderUrl, int timeoutSeconds = 300)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            using var doc = await GetJsonAsync(orderUrl);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            if (status == "valid")
            {
                if (root.TryGetProperty("certificate", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString() ?? "";
                throw new AcmeException("订单已有效但缺少证书 URL");
            }
            if (status == "invalid")
                throw new AcmeException($"订单处理失败：{DescribeError(root)}");
            await Task.Delay(3000);
        }
        throw new AcmeException("等待证书签发超时");
    }

    /// <summary>下载 PEM 证书链（含中间证书）。</summary>
    public async Task<string> DownloadCertificateAsync(string certificateUrl)
    {
        var resp = await PostJwsAsync(certificateUrl, "", useKid: true);
        return await resp.Content.ReadAsStringAsync();
    }

    // ---------------- 内部实现 ----------------

    private async Task<string> DirUrlAsync(string key)
    {
        if (_directory.ValueKind != JsonValueKind.Object)
        {
            var resp = await Http.GetAsync(_directoryUrl);
            resp.EnsureSuccessStatusCode();
            _directory = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        if (!_directory.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new AcmeException($"ACME 目录缺少 {key} 端点");
        return el.GetString() ?? "";
    }

    private async Task<string> GetNonceAsync()
    {
        var nonceUrl = await DirUrlAsync("newNonce");
        // 优先 HEAD，个别实现不支持时回退 GET
        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            using var req = new HttpRequestMessage(method, nonceUrl);
            var resp = await Http.SendAsync(req);
            if (resp.Headers.TryGetValues("Replay-Nonce", out var vals))
            {
                var n = vals.FirstOrDefault();
                if (!string.IsNullOrEmpty(n)) return n!;
            }
        }
        throw new AcmeException("无法获取 ACME 服务器 nonce");
    }

    private async Task<HttpResponseMessage> PostJwsAsync(string url, string payloadJson, bool useKid)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var headers = new Dictionary<string, object>
            {
                ["alg"] = "ES256",
                ["nonce"] = await GetNonceAsync(),
                ["url"] = url,
            };
            if (useKid)
            {
                if (string.IsNullOrEmpty(_kid))
                    throw new AcmeException("账户尚未注册，缺少 kid");
                headers["kid"] = _kid!;
            }
            else
            {
                headers["jwk"] = _accountJwk;
            }

            var protectedB64 = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headers)));
            var payloadB64 = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
            var input = Encoding.ASCII.GetBytes(protectedB64 + "." + payloadB64);
            var sig = _accountKey.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            var body = "{\"protected\":\"" + protectedB64 + "\",\"payload\":\"" + payloadB64 + "\",\"signature\":\"" + Base64Url(sig) + "\"}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            // 必须精确等于 application/jose+json，不能带 charset 参数，否则 CA 拒绝
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return resp;

            var err = await ReadErrorAsync(resp);
            if (attempt < 2 && err.Contains("badNonce", StringComparison.OrdinalIgnoreCase))
                continue; // nonce 过期，用新 nonce 重试
            throw new AcmeException(err);
        }
        throw new AcmeException("ACME 请求失败");
    }

    /// <summary>POST-as-GET：对资源 URL 执行签名 GET。</summary>
    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        var resp = await PostJwsAsync(url, "", useKid: true);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static string DescribeError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var detail = err.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var type = err.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (!string.IsNullOrEmpty(detail)) return detail + (string.IsNullOrEmpty(type) ? "" : $"（{type}）");
            return type ?? "";
        }
        return root.ToString();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var detail = doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (!string.IsNullOrEmpty(detail)) return detail + (string.IsNullOrEmpty(type) ? "" : $"（{type}）");
            return body;
        }
        catch
        {
            return $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
        }
    }

    internal static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
