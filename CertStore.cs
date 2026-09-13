using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopTool;

/// <summary>一次证书申请会话的持久化状态（第一步与第二步之间可能跨程序重启，
/// 因此必须落盘保存订单/挑战/密钥路径等信息）。
/// </summary>
public class CertSession
{
    public string CaName { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Email { get; set; } = "";
    public string Kid { get; set; } = "";
    public string OrderUrl { get; set; } = "";
    public string FinalizeUrl { get; set; } = "";
    public string CertKeyPath { get; set; } = "";
    public string KeyAlgorithm { get; set; } = "ecdsa";
    public List<CertChallenge> Challenges { get; set; } = new();
}

/// <summary>一张已存在证书的信息（用于证书管理 / 续期）。</summary>
public class CertInstalledInfo
{
    public string Domain { get; set; } = "";
    public string Dir { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public string KeyAlgorithm { get; set; } = "ecdsa";
    public bool HasPrivKey { get; set; }

    /// <summary>列表显示用摘要（过期时间 + 算法）。</summary>
    public string Summary
    {
        get
        {
            var algoText = KeyAlgorithm == "rsa" ? "RSA 2048" : "ECDSA P-256";
            var exp = ExpiresAt is { } e
                ? (e < DateTime.Now
                    ? "已过期 " + e.ToString("yyyy-MM-dd")
                    : "剩余 " + (int)Math.Ceiling((e - DateTime.Now).TotalDays) + " 天 · " + e.ToString("yyyy-MM-dd"))
                : "无法读取过期时间";
            return exp + " · " + algoText + (HasPrivKey ? "" : " · 缺少私钥");
        }
    }
}

/// <summary>
/// 证书数据目录管理。
/// 目录结构（位于软件运行目录 env/certs 下）：
///   account_keys/<ca>.pem   —— 全局 ACME 账户私钥（每 CA 一份，复用）
///   state/<domain>.json     —— 申请会话状态（跨重启续作）
///   live/<domain>/          —— 签发的证书产物
/// </summary>
public static class CertStore
{
    public static string CertsRoot => Path.Combine(ToolPaths.BaseDir, "env", "certs");

    public static string AccountKeyPath(string caName)
        => Path.Combine(CertsRoot, "account_keys", Sanitize(caName) + ".pem");

    public static string DomainDir(string domain)
        => Path.Combine(CertsRoot, "live", Sanitize(domain));

    public static string SessionPath(string domain)
        => Path.Combine(CertsRoot, "state", Sanitize(domain) + ".json");

    private static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '.' && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        }
        return new string(chars).Trim('.');
    }

    /// <summary>加载（不存在则生成）指定 CA 的全局 ECDSA P-256 账户密钥。</summary>
    public static ECDsa LoadOrCreateAccountKey(string caName)
    {
        var path = AccountKeyPath(caName);
        if (File.Exists(path))
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, newKey.ExportECPrivateKeyPem());
        return newKey;
    }

    /// <summary>生成证书私钥并保存到 live/&lt;domain&gt;/privkey.pem，返回路径。</summary>
    public static string GenerateCertKey(string domain, string algorithm)
    {
        var dir = DomainDir(domain);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "privkey.pem");
        if (algorithm == "rsa")
        {
            using var rsa = RSA.Create(2048);
            File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        }
        else
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(path, ecdsa.ExportECPrivateKeyPem());
        }
        return path;
    }

    /// <summary>加载证书私钥（自动识别 ECDSA / RSA）。</summary>
    public static AsymmetricAlgorithm LoadCertKey(string path)
    {
        var pem = File.ReadAllText(path);
        if (pem.Contains("EC PRIVATE KEY", StringComparison.Ordinal))
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return ecdsa;
        }
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }

    /// <summary>为域名生成 CSR（SAN 包含域名；泛域名额外附带根域）。</summary>
    public static byte[] CreateCsr(AsymmetricAlgorithm key, string domain)
    {
        var isWildcard = domain.StartsWith("*.", StringComparison.Ordinal);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        if (isWildcard)
            san.AddDnsName(domain.Substring(2));

        if (key is ECDsa ecdsa)
        {
            var req = new CertificateRequest("CN=" + domain, ecdsa, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(san.Build());
            return req.CreateSigningRequest();
        }

        var rsa = (RSA)key;
        var req2 = new CertificateRequest("CN=" + domain, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req2.CertificateExtensions.Add(san.Build());
        return req2.CreateSigningRequest();
    }

    public static void SaveSession(CertSession session)
    {
        var path = SessionPath(session.Domain);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static CertSession? LoadSession(string domain)
    {
        var path = SessionPath(domain);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CertSession>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static void DeleteSession(string domain)
    {
        var path = SessionPath(domain);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>把下载的 PEM 证书链拆分为 cert.pem / chain.pem / fullchain.pem。</summary>
    public static void SaveCertificateBundle(string domain, string fullchainPem)
    {
        var dir = DomainDir(domain);
        Directory.CreateDirectory(dir);
        var blocks = Regex.Matches(fullchainPem, @"-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----")
            .Select(m => m.Value.Trim())
            .Where(b => b.Length > 0)
            .ToList();
        File.WriteAllText(Path.Combine(dir, "fullchain.pem"), string.Join("\n", blocks) + "\n");
        File.WriteAllText(Path.Combine(dir, "cert.pem"), blocks.Count > 0 ? blocks[0] + "\n" : "");
        if (blocks.Count > 1)
            File.WriteAllText(Path.Combine(dir, "chain.pem"), string.Join("\n", blocks.Skip(1)) + "\n");
    }

    // ---------------- 已存在证书管理 ----------------

    /// <summary>列出已签发的证书（live 目录），按过期时间排序。</summary>
    public static List<CertInstalledInfo> ListInstalledCerts()
    {
        var liveDir = Path.Combine(CertsRoot, "live");
        var result = new List<CertInstalledInfo>();
        if (!Directory.Exists(liveDir)) return result;
        foreach (var dir in Directory.GetDirectories(liveDir))
        {
            var certPath = Path.Combine(dir, "cert.pem");
            if (!File.Exists(certPath)) continue;
            DateTime? expires = null;
            try
            {
                using var cert = X509Certificate2.CreateFromPemFile(certPath);
                expires = cert.NotAfter;
            }
            catch { }

            var keyPath = Path.Combine(dir, "privkey.pem");
            var algo = "ecdsa";
            if (File.Exists(keyPath))
            {
                try
                {
                    var pem = File.ReadAllText(keyPath);
                    algo = pem.Contains("EC PRIVATE KEY", StringComparison.Ordinal) ? "ecdsa" : "rsa";
                }
                catch { }
            }
            result.Add(new CertInstalledInfo
            {
                Domain = Path.GetFileName(dir),
                Dir = dir,
                ExpiresAt = expires,
                KeyAlgorithm = algo,
                HasPrivKey = File.Exists(keyPath),
            });
        }
        return result.OrderBy(c => c.ExpiresAt ?? DateTime.MaxValue).ToList();
    }
}
