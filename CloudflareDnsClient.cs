using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>
/// Cloudflare DNS API v4 客户端。
/// 认证使用 API Token（推荐，最小权限：Zone.Zone Read + Zone.DNS Edit）。
/// 覆盖：域名（zone）列表、解析记录增删改查，以及证书申请所需的 TXT 验证记录。
/// 文档：https://developers.cloudflare.com/api/
/// </summary>
public class CloudflareDnsClient
{
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _apiToken;

    public CloudflareDnsClient(string apiToken)
    {
        _apiToken = apiToken;
    }

    // ---------------- 域名（zone） ----------------

    /// <summary>列出账号下的全部域名（zone）。</summary>
    public async Task<List<AlidnsDomain>> ListZonesAsync()
    {
        var root = await GetAsync("/zones?per_page=50");
        var result = new List<AlidnsDomain>();
        foreach (var el in root.EnumerateArray())
        {
            result.Add(new AlidnsDomain
            {
                DomainName = GetStr(el, "name"),
                DomainId = GetStr(el, "id"),
                RecordCount = GetStr(el, "status"), // Cloudflare 无记录数，这里放 zone 状态（active/pending）
                PunyCode = "",
            });
        }
        return result;
    }

    /// <summary>按根域名精确查找 zone（证书验证用）。找不到返回 null。</summary>
    public async Task<AlidnsDomain?> FindZoneAsync(string domainName)
    {
        var root = await GetAsync("/zones?name=" + Uri.EscapeDataString(domainName) + "&per_page=1");
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return null;
        var el = root[0];
        return new AlidnsDomain
        {
            DomainName = GetStr(el, "name"),
            DomainId = GetStr(el, "id"),
            RecordCount = GetStr(el, "status"),
        };
    }

    // ---------------- 解析记录 CRUD ----------------

    /// <summary>查询 zone 下全部解析记录（映射为通用记录模型，Rr 显示完整记录名）。</summary>
    public async Task<List<AlidnsRecord>> ListRecordsAsync(string zoneId)
    {
        var root = await GetAsync($"/zones/{zoneId}/dns_records?per_page=100");
        var result = new List<AlidnsRecord>();
        foreach (var el in root.EnumerateArray())
        {
            var ttl = el.TryGetProperty("ttl", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 1;
            var proxied = el.TryGetProperty("proxied", out var p) && p.ValueKind == JsonValueKind.True;
            var priority = el.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : 0;
            result.Add(new AlidnsRecord
            {
                RecordId = GetStr(el, "id"),
                Rr = GetStr(el, "name"),
                Type = GetStr(el, "type"),
                Value = GetStr(el, "content"),
                Ttl = ttl == 1 ? "自动" : ttl.ToString(),
                Priority = priority > 0 ? priority.ToString() : "",
                Line = proxied ? "已代理" : "仅DNS",
                Status = "",
            });
        }
        return result;
    }

    /// <summary>添加解析记录，返回记录 id。</summary>
    public async Task<string> AddRecordAsync(string zoneId, string type, string name, string content, int ttl, int? priority, bool proxied)
    {
        var (body, pttl) = BuildBody(type, name, content, ttl, priority, proxied);
        var root = await PostAsync($"/zones/{zoneId}/dns_records", body);
        return root.TryGetProperty("id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
    }

    /// <summary>更新解析记录。</summary>
    public async Task UpdateRecordAsync(string zoneId, string recordId, string type, string name, string content, int ttl, int? priority, bool proxied)
    {
        var (body, _) = BuildBody(type, name, content, ttl, priority, proxied);
        await PutAsync($"/zones/{zoneId}/dns_records/{recordId}", body);
    }

    /// <summary>删除解析记录。</summary>
    public async Task DeleteRecordAsync(string zoneId, string recordId)
    {
        await DeleteAsync($"/zones/{zoneId}/dns_records/{recordId}");
    }

    // ---------------- TXT 验证记录（证书申请用） ----------------

    /// <summary>查询 zone 下指定记录名的全部 TXT 记录。</summary>
    public async Task<List<AlidnsRecord>> ListTxtRecordsAsync(string zoneId, string name)
    {
        var root = await GetAsync($"/zones/{zoneId}/dns_records?type=TXT&name={Uri.EscapeDataString(name)}&per_page=100");
        var result = new List<AlidnsRecord>();
        foreach (var el in root.EnumerateArray())
        {
            result.Add(new AlidnsRecord
            {
                RecordId = GetStr(el, "id"),
                Rr = GetStr(el, "name"),
                Type = "TXT",
                Value = GetStr(el, "content"),
            });
        }
        return result;
    }

    /// <summary>添加 TXT 验证记录（TTL 自动、不代理，DNS-01 专用）。</summary>
    public async Task<string> AddTxtRecordAsync(string zoneId, string name, string value)
    {
        var root = await PostAsync($"/zones/{zoneId}/dns_records", new Dictionary<string, object>
        {
            ["type"] = "TXT",
            ["name"] = name,
            ["content"] = value,
            ["ttl"] = 1,
            ["proxied"] = false,
        });
        return root.TryGetProperty("id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
    }

    /// <summary>按记录 id 删除（TXT 验证记录清理）。</summary>
    public Task DeleteTxtRecordAsync(string zoneId, string recordId)
        => DeleteRecordAsync(zoneId, recordId);

    // ---------------- 内部实现 ----------------

    /// <summary>构造记录请求体。代理（橙云）只支持 A/AAAA/CNAME 且 TTL 强制为 1（自动）。</summary>
    private static (Dictionary<string, object> Body, int Ttl) BuildBody(string type, string name, string content, int ttl, int? priority, bool proxied)
    {
        var canProxy = type is "A" or "AAAA" or "CNAME";
        var useProxy = proxied && canProxy;
        var pttl = useProxy ? 1 : (ttl > 0 ? ttl : 1);
        var body = new Dictionary<string, object>
        {
            ["type"] = type,
            ["name"] = name,
            ["content"] = content,
            ["ttl"] = pttl,
            ["proxied"] = useProxy,
        };
        if (priority is > 0)
            body["priority"] = priority.Value;
        return (body, pttl);
    }

    private async Task<JsonElement> GetAsync(string path)
        => await SendAsync(HttpMethod.Get, path, null);

    private async Task<JsonElement> PostAsync(string path, object body)
        => await SendAsync(HttpMethod.Post, path, body);

    private async Task<JsonElement> PutAsync(string path, object body)
        => await SendAsync(HttpMethod.Put, path, body);

    private async Task<JsonElement> DeleteAsync(string path)
        => await SendAsync(HttpMethod.Delete, path, null);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body)
    {
        using var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiToken);
        if (body is not null)
            req.Content = JsonContent.Create(body);

        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new CloudflareException($"HTTP {(int)resp.StatusCode}：响应不是合法 JSON");
        }

        if (!resp.IsSuccessStatusCode)
            throw new CloudflareException($"HTTP {(int)resp.StatusCode}：{text}");

        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var messages = new List<string>();
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in errs.EnumerateArray())
                {
                    var msg = GetStr(el, "message");
                    if (msg.Length > 0) messages.Add(msg);
                }
            }
            throw new CloudflareException(messages.Count > 0 ? string.Join("；", messages) : "Cloudflare API 错误");
        }

        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            return result.Clone();
        if (root.TryGetProperty("result", out var resultArr) && resultArr.ValueKind == JsonValueKind.Array)
            return resultArr.Clone();
        return root.Clone();
    }

    private static string GetStr(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Cloudflare API 返回的业务错误。</summary>
public class CloudflareException : Exception
{
    public CloudflareException(string message) : base("Cloudflare API 错误：" + message) { }
}
