using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DesktopTool;

/// <summary>
/// Windows IKEv2 VPN 一键操作（电脑端）。
/// 通过系统原生 VPN 基础设施实现：PowerShell Add-VpnConnection 创建连接（需管理员），
/// rasdial 触发连接/断开（无需管理员）。手机端用同样的服务器/账号在 iOS 系统 IKEv2 配置。
/// </summary>
public static class Ikev2Vpn
{
    /// <summary>Windows 中创建的 VPN 连接名称。</summary>
    public const string ConnectionName = "Seraphine VPN";

    private static readonly Lazy<Encoding> AnsiEncoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.ANSICodePage);
    });

    /// <summary>创建 IKEv2 VPN 连接（需要管理员权限）。已存在则返回成功。</summary>
    public static string CreateConnection(string server)
    {
        if (ConnectionExists())
            return "连接已存在";

        if (!WindowsActivation.IsAdministrator)
            return "创建 VPN 连接需要管理员权限：请以管理员身份运行本程序后重试";

        var s = (server ?? "").Trim();
        if (s.Length == 0)
            return "服务器地址不能为空";

        var ps = $"Add-VpnConnection -Name '{ConnectionName}' -ServerAddress '{s}' -TunnelType Ikev2 -AuthenticationMethod EAP -EncryptionLevel Required -RememberCredential";
        var output = RunPowerShell(ps);
        return string.IsNullOrWhiteSpace(output) || output.Contains("已成功", StringComparison.OrdinalIgnoreCase)
            ? "VPN 连接已创建"
            : "创建失败：" + output;
    }

    /// <summary>连接（自动创建连接；创建需管理员，连接本身不需要）。返回结果文本。</summary>
    public static string Connect(string server, string user, string pass)
    {
        var createResult = CreateConnection(server);
        if (!createResult.StartsWith("VPN 连接已创建", StringComparison.Ordinal) && createResult != "连接已存在")
            return createResult;

        return RunProcess("rasdial",
            $"\"{ConnectionName}\" \"{user}\" \"{pass}\"");
    }

    /// <summary>断开 VPN。返回结果文本。</summary>
    public static string Disconnect()
    {
        return RunProcess("rasdial", $"\"{ConnectionName}\" /disconnect");
    }

    /// <summary>当前是否已建立该 VPN 连接。</summary>
    public static bool IsConnected()
    {
        try
        {
            var output = RunProcess("rasdial", "");
            return output.Contains(ConnectionName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>连接是否已在系统中创建。</summary>
    public static bool ConnectionExists()
    {
        try
        {
            var output = RunPowerShell($"Get-VpnConnection -Name '{ConnectionName}' -ErrorAction SilentlyContinue");
            return !string.IsNullOrWhiteSpace(output) && output.Contains(ConnectionName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        return Run(psi);
    }

    private static string RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        return Run(psi);
    }

    private static string Run(ProcessStartInfo psi)
    {
        try
        {
            var enc = AnsiEncoding.Value;
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            using var p = Process.Start(psi);
            if (p is null) return "无法启动进程";
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            var text = (output + Environment.NewLine + error).Trim();
            return string.IsNullOrWhiteSpace(text) ? "命令已执行（无输出）" : text;
        }
        catch (Exception ex)
        {
            return "执行失败：" + ex.Message;
        }
    }
}
