using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>
/// 阿里云云解析 DNS（Alidns）OpenAPI 客户端（RPC 风格 + HMAC-SHA1 签名）。
/// 仅实现本工具所需的 TXT 记录操作：添加、查询、删除。
/// 文档：https://help.aliyun.com/zh/alidns/（接口版本 2015-01-09）
/// </summary>
public class AlidnsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _accessKeyId;
    private readonly string _accessKeySecret;

    public AlidnsClient(string accessKeyId, string accessKeySecret)
    {
        _accessKeyId = accessKeyId;
        _accessKeySecret = accessKeySecret;
    }

    /// <summary>添加 TXT 记录，返回 RecordId。</summary>
    public async Task<string> AddTxtRecordAsync(string domainName, string rr, string value, int ttl = 60)
    {
        var root = await CallAsync(new Dictionary<string, string>
        {
            ["Action"] = "AddDomainRecord",
            ["DomainName"] = domainName,
            ["RR"] = rr,
            ["Type"] = "TXT",
            ["Value"] = value,
            ["TTL"] = ttl.ToString(),
        });
        return root.TryGetProperty("RecordId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
    }

    /// <summary>查询域名下所有 TXT 记录。</summary>
    public async Task<List<AlidnsRecord>> DescribeTxtRecordsAsync(string domainName)
    {
        var root = await CallAsync(new Dictionary<string, string>
        {
            ["Action"] = "DescribeDomainRecords",
            ["DomainName"] = domainName,
            ["TypeKeyWord"] = "TXT",
            ["PageSize"] = "500",
        });
        var result = new List<AlidnsRecord>();
        if (root.TryGetProperty("DomainRecords", out var dr) && dr.ValueKind == JsonValueKind.Object
            && dr.TryGetProperty("Record", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in recs.EnumerateArray())
            {
                result.Add(new AlidnsRecord
                {
                    RecordId = GetStr(el, "RecordId"),
                    Rr = GetStr(el, "RR"),
                    Type = GetStr(el, "Type"),
                    Value = GetStr(el, "Value"),
                });
            }
        }
        return result;
    }

    /// <summary>列出账号下的全部域名。</summary>
    public async Task<List<AlidnsDomain>> DescribeDomainsAsync()
    {
        var root = await CallAsync(new Dictionary<string, string>
        {
            ["Action"] = "DescribeDomains",
            ["PageSize"] = "100",
        });
        var result = new List<AlidnsDomain>();
        if (root.TryGetProperty("Domains", out var ds) && ds.ValueKind == JsonValueKind.Object
            && ds.TryGetProperty("Domain", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                result.Add(new AlidnsDomain
                {
                    DomainName = GetStr(el, "DomainName"),
                    DomainId = GetStr(el, "DomainId"),
                    RecordCount = GetStr(el, "RecordCount"),
                    PunyCode = GetStr(el, "PunyCode"),
                });
            }
        }
        return result;
    }

    /// <summary>查询域名下的全部解析记录（可按类型过滤，如 A / TXT / CNAME）。</summary>
    public async Task<List<AlidnsRecord>> DescribeRecordsAsync(string domainName, string? typeKeyword = null)
    {
        var ps = new Dictionary<string, string>
        {
            ["Action"] = "DescribeDomainRecords",
            ["DomainName"] = domainName,
            ["PageSize"] = "500",
        };
        if (!string.IsNullOrWhiteSpace(typeKeyword))
            ps["TypeKeyWord"] = typeKeyword!;
        var root = await CallAsync(ps);
        var result = new List<AlidnsRecord>();
        if (root.TryGetProperty("DomainRecords", out var dr) && dr.ValueKind == JsonValueKind.Object
            && dr.TryGetProperty("Record", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in recs.EnumerateArray())
            {
                result.Add(new AlidnsRecord
                {
                    RecordId = GetStr(el, "RecordId"),
                    Rr = GetStr(el, "RR"),
                    Type = GetStr(el, "Type"),
                    Value = GetStr(el, "Value"),
                    Ttl = GetStr(el, "TTL"),
                    Priority = GetStr(el, "Priority"),
                    Line = GetStr(el, "Line"),
                    Status = GetStr(el, "Status"),
                });
            }
        }
        return result;
    }

    /// <summary>添加解析记录，返回 RecordId。</summary>
    public async Task<string> AddRecordAsync(string domainName, string rr, string type, string value, int ttl, int? priority, string line)
    {
        var ps = new Dictionary<string, string>
        {
            ["Action"] = "AddDomainRecord",
            ["DomainName"] = domainName,
            ["RR"] = rr,
            ["Type"] = type,
            ["Value"] = value,
            ["TTL"] = ttl.ToString(),
            ["Line"] = string.IsNullOrWhiteSpace(line) ? "default" : line,
        };
        if (priority is > 0)
            ps["Priority"] = priority.Value.ToString();
        var root = await CallAsync(ps);
        return root.TryGetProperty("RecordId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
    }

    /// <summary>修改解析记录（RR/Type/Value 等）。</summary>
    public async Task UpdateRecordAsync(string recordId, string rr, string type, string value, int ttl, int? priority, string line)
    {
        var ps = new Dictionary<string, string>
        {
            ["Action"] = "UpdateDomainRecord",
            ["RecordId"] = recordId,
            ["RR"] = rr,
            ["Type"] = type,
            ["Value"] = value,
            ["TTL"] = ttl.ToString(),
            ["Line"] = string.IsNullOrWhiteSpace(line) ? "default" : line,
        };
        if (priority is > 0)
            ps["Priority"] = priority.Value.ToString();
        await CallAsync(ps);
    }

    /// <summary>删除指定解析记录。</summary>
    public async Task DeleteRecordAsync(string recordId)
    {
        await CallAsync(new Dictionary<string, string>
        {
            ["Action"] = "DeleteDomainRecord",
            ["RecordId"] = recordId,
        });
    }

    // ---------------- 内部实现 ----------------

    private async Task<JsonElement> CallAsync(Dictionary<string, string> biz)
    {
        var ps = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in biz) ps[kv.Key] = kv.Value;
        ps["AccessKeyId"] = _accessKeyId;
        ps["Format"] = "JSON";
        ps["Version"] = "2015-01-09";
        ps["SignatureMethod"] = "HMAC-SHA1";
        ps["SignatureVersion"] = "1.0";
        ps["SignatureNonce"] = Guid.NewGuid().ToString("N");
        ps["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // 签名：StringToSign = GET&%2F&<percentEncode(规范化查询串)>
        var canonical = string.Join("&", ps.Select(kv => PercentEncode(kv.Key) + "=" + PercentEncode(kv.Value)));
        var stringToSign = "GET&%2F&" + PercentEncode(canonical);
        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(_accessKeySecret + "&"));
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(stringToSign)));
        ps["Signature"] = sig;

        var url = "https://dns.aliyuncs.com/?" + string.Join("&", ps.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        using var resp = await Http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("Code", out var code) && code.ValueKind == JsonValueKind.String)
        {
            var message = root.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            throw new AlidnsException(code.GetString() ?? "", message);
        }
        return root;
    }

    private static string GetStr(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>RFC 3986 percent-encode（阿里云签名要求：十六进制大写、空格为 %20、不编码 ~）。</summary>
    private static string PercentEncode(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                || b == '-' || b == '_' || b == '.' || b == '~')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 从完整挑战记录名拆分出「阿里云根域 + 主机记录 RR」。
    /// 例：_acme-challenge.server.rubyonly.cn → (rubyonly.cn, _acme-challenge.server)
    ///     _acme-challenge.rubyonly.cn        → (rubyonly.cn, _acme-challenge)
    /// 对 foo.com.cn 这类「二级国家码域」会按 3 段识别根域。
    /// </summary>
    public static (string RootDomain, string Rr) SplitRecordName(string recordName)
    {
        var labels = recordName.TrimEnd('.').Split('.');
        var rootLen = 2;
        if (labels.Length >= 3)
        {
            var tld = labels[^1];
            var sld = labels[^2];
            if (tld.Length == 2 && sld is "com" or "net" or "org" or "gov" or "edu" or "ac" or "co" or "mil")
                rootLen = 3;
        }
        var root = string.Join(".", labels[^rootLen..]);
        var rr = string.Join(".", labels[..^rootLen]);
        return (root, rr);
    }
}

/// <summary>阿里云解析记录。</summary>
public class AlidnsRecord
{
    public string RecordId { get; set; } = "";
    public string Rr { get; set; } = "";
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
    public string Ttl { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Line { get; set; } = "";
    public string Status { get; set; } = "";

    /// <summary>列表展示用的摘要。</summary>
    public string Summary => $"{(string.IsNullOrEmpty(Rr) ? "@" : Rr)}  {Type}  {Value}";
}

/// <summary>阿里云域名（DescribeDomains 结果）。</summary>
public class AlidnsDomain
{
    public string DomainName { get; set; } = "";
    public string DomainId { get; set; } = "";
    public string RecordCount { get; set; } = "";
    public string PunyCode { get; set; } = "";
}

/// <summary>阿里云 API 返回的业务错误。</summary>
public class AlidnsException : Exception
{
    public string? Code { get; }
    public AlidnsException(string? code, string message) : base($"阿里云 API 错误（{code}）：{message}") => Code = code;
}
