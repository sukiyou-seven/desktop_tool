using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>winget 包管理器封装：检测、安装、卸载。</summary>
public static class WingetHelper
{
    /// <summary>winget 是否可用。</summary>
    public static bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>安装路径根目录：软件运行目录/apps/installers/</summary>
    public static string InstallRoot => Path.Combine(ToolPaths.BaseDir, "apps", "installers");

    /// <summary>
    /// 通过 winget 静默安装指定包到自定义路径。
    /// 返回 (success, output)。
    /// </summary>
    public static async Task<(bool Success, string Output)> InstallAsync(
        string wingetId, string? installLocation = null, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(wingetId))
            return (false, "wingetId 为空");

        var sb = new StringBuilder();
        var location = string.IsNullOrWhiteSpace(installLocation)
            ? Path.Combine(InstallRoot, Sanitize(wingetId))
            : installLocation;

        try
        {
            Directory.CreateDirectory(location);

            var args = $"install --id {wingetId} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --location \"{location}\"";

            progress?.Report($"winget {args}");

            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    sb.AppendLine(e.Data);
                    progress?.Report(e.Data);
                }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    sb.AppendLine(e.Data);
                    progress?.Report(e.Data);
                }
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await Task.Run(() => p.WaitForExit());

            return (p.ExitCode == 0, sb.ToString().Trim());
        }
        catch (Exception ex)
        {
            return (false, $"winget 执行异常：{ex.Message}\n{sb}");
        }
    }

    /// <summary>通过 winget 卸载指定包。</summary>
    public static async Task<(bool Success, string Output)> UninstallAsync(string wingetId, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(wingetId))
            return (false, "wingetId 为空");

        var sb = new StringBuilder();
        try
        {
            var args = $"uninstall --id {wingetId} --silent --accept-source-agreements --disable-interactivity";
            progress?.Report($"winget {args}");

            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) { sb.AppendLine(e.Data); progress?.Report(e.Data); }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) { sb.AppendLine(e.Data); progress?.Report(e.Data); }
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await Task.Run(() => p.WaitForExit());
            return (p.ExitCode == 0, sb.ToString().Trim());
        }
        catch (Exception ex)
        {
            return (false, $"winget 执行异常：{ex.Message}\n{sb}");
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim();
    }
}
