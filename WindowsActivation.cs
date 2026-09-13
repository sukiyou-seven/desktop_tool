using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Principal;
using System.Text;

namespace DesktopTool;

/// <summary>
/// Windows 激活信息（只读查询 + 通过系统官方 slmgr 命令执行合法激活）。
/// 支持：使用用户自有的产品密钥安装/激活（/ipk、/ato），以及配置 KMS 客户端（/skms）——
/// 均为 Windows 系统自带命令，KMS 仅适用于合法的批量授权（企业/教育）环境。
/// 不包含任何 KMS 模拟、伪造授权、绕过激活等规避许可的实现。
/// 激活操作需要管理员权限。
/// </summary>
public class WindowsActivationInfo
{
    public string ProductName { get; set; } = "";
    public int LicenseStatus { get; set; } = -1;
    public string LicenseStatusText { get; set; } = "未知";
    public string ProductKeyChannel { get; set; } = "";
    public string PartialProductKey { get; set; } = "";
    public string ExpirationText { get; set; } = "";

    /// <summary>LicenseStatus == 1 表示已激活（Licensed）。</summary>
    public bool IsActivated => LicenseStatus == 1;
}

/// <summary>Windows 激活状态查询器与合法激活操作（封装系统官方 slmgr 命令）。</summary>
public static class WindowsActivation
{
    /// <summary>查询本机 Windows 激活信息；失败时返回可读错误提示。</summary>
    public static WindowsActivationInfo Query()
    {
        var info = new WindowsActivationInfo();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\cimv2",
                "SELECT Name, LicenseStatus, PartialProductKey, ProductKeyChannel, GracePeriodRemaining FROM SoftwareLicensingProduct");

            foreach (var obj in searcher.Get())
            {
                var name = Convert.ToString(obj["Name"]) ?? "";
                var partialKey = Convert.ToString(obj["PartialProductKey"]) ?? "";
                // 仅取 Windows 的激活实例（跳过 Office 等），且必须有产品密钥尾号
                if (!name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(partialKey))
                    continue;

                info.ProductName = name;
                info.PartialProductKey = partialKey;
                info.ProductKeyChannel = FormatChannel(Convert.ToString(obj["ProductKeyChannel"]) ?? "");

                if (obj["LicenseStatus"] is not null)
                    info.LicenseStatus = Convert.ToInt32(obj["LicenseStatus"]);

                long graceMinutes = obj["GracePeriodRemaining"] is not null
                    ? Convert.ToInt64(obj["GracePeriodRemaining"])
                    : 0;
                info.ExpirationText = FormatExpiration(info.LicenseStatus, graceMinutes);
                break;
            }
        }
        catch (Exception ex)
        {
            info.ProductName = "";
            info.LicenseStatusText = "无法读取（" + ShortError(ex) + "）";
            info.ExpirationText = "";
            return info;
        }

        info.LicenseStatusText = LicenseStatusText(info.LicenseStatus);
        if (info.LicenseStatus == -1)
            info.LicenseStatusText = "未查询到激活实例（可能为精简系统或服务未运行）";
        return info;
    }

    /// <summary>当前进程是否以管理员权限运行。</summary>
    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>安装产品密钥（需管理员）。密钥为用户自有密钥。</summary>
    public static string InstallProductKey(string key)
    {
        var k = (key ?? "").Trim();
        if (k.Length == 0)
            return "产品密钥不能为空";
        return RunSlmgr($"/ipk {k}");
    }

    /// <summary>设置 KMS 服务器地址（需管理员）。仅供合法的批量授权（企业/教育）环境使用。</summary>
    public static string SetKmsServer(string server)
    {
        var s = (server ?? "").Trim();
        if (s.Length == 0)
            return "KMS 服务器地址不能为空";
        return RunSlmgr($"/skms {s}");
    }

    /// <summary>尝试在线激活当前系统（需管理员）。</summary>
    public static string ActivateOnline() => RunSlmgr("/ato");

    /// <summary>显示许可证信息（/dli 简版）。</summary>
    public static string ShowLicenseInfo() => RunSlmgr("/dli");

    /// <summary>运行 slmgr.vbs 并返回输出（含 stderr）。</summary>
    private static string RunSlmgr(string arguments)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var slmgrPath = Path.Combine(Environment.SystemDirectory, "slmgr.vbs");
            if (!File.Exists(slmgrPath))
                return "未找到 slmgr.vbs（当前系统可能不支持）";

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cscript.exe"),
                Arguments = $"//nologo \"{slmgrPath}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // slmgr 输出为本地 ANSI 编码（中文系统为 GBK）
            var enc = Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.ANSICodePage);
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;

            using var p = Process.Start(psi);
            if (p is null)
                return "无法启动 slmgr 命令";

            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);

            var text = (output + Environment.NewLine + error).Trim();
            return string.IsNullOrWhiteSpace(text) ? "命令已执行（无输出），退出码 " + p.ExitCode : text;
        }
        catch (Exception ex)
        {
            return "执行失败：" + ex.Message;
        }
    }

    private static string LicenseStatusText(int status) => status switch
    {
        0 => "未授权",
        1 => "已激活",
        2 => "未激活（宽限期）",
        3 => "延长宽限期",
        4 => "非正版（宽限期）",
        5 => "通知模式",
        6 => "延长宽限期",
        _ => "未知",
    };

    private static string FormatChannel(string channel) => channel switch
    {
        "Retail" => "零售版 (Retail)",
        "Volume:MAK" => "批量授权 (MAK)",
        "Volume:KMS" => "批量授权 (KMS)",
        "OEM" => "OEM 预装",
        "" => "未知",
        _ => channel,
    };

    private static string FormatExpiration(int status, long graceMinutes)
    {
        if (status == 1)
        {
            return graceMinutes > 0
                ? $"已激活，当前授权剩余 {FormatGrace(graceMinutes)}"
                : "已激活（永久授权）";
        }

        return graceMinutes > 0
            ? $"当前宽限期剩余 {FormatGrace(graceMinutes)}"
            : "—";
    }

    private static string FormatGrace(long minutes)
    {
        var ts = TimeSpan.FromMinutes(minutes);
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays} 天 {ts.Hours} 小时";
        if (ts.TotalHours >= 1)
            return $"{ts.Hours} 小时 {ts.Minutes} 分";
        return $"{ts.Minutes} 分";
    }

    private static string ShortError(Exception ex)
    {
        var msg = ex.Message?.Replace('\r', ' ').Replace('\n', ' ') ?? "";
        return msg.Length > 60 ? msg[..60] + "…" : msg;
    }
}
