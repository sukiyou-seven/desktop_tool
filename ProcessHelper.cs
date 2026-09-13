using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DesktopTool;

/// <summary>
/// 子进程执行辅助：并行读取 stdout / stderr 原始字节（后台线程池，避免 UI 线程 .Result 死锁），
/// 解码时优先严格 UTF-8，非法序列回退系统 ANSI 代码页（中文系统为 GBK），
/// 避免中文 Windows 下子进程输出被误按 UTF-8 解码导致乱码。
/// 注意：本方法会同步阻塞调用线程直到命令结束（最长 15 秒），
/// 在 UI 线程调用请用 async + Task.Run 包裹，避免界面卡顿。
/// </summary>
public static class ProcessHelper
{
    private static readonly Encoding AnsiEncoding = CreateAnsiEncoding();

    private static Encoding CreateAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(cp);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>执行命令并返回合并输出；失败或空输出返回 null。同步阻塞最长 15 秒。</summary>
    public static string? Run(string fileName, string args) => Run(fileName, args, null, 15000);

    /// <summary>执行命令并返回合并输出；可指定工作目录和超时（毫秒）。</summary>
    public static string? Run(string fileName, string args, string? workingDirectory, int timeoutMs = 15000)
    {
        try
        {
            // 始终用最新的 用户+系统 PATH，既用于子进程环境，也用于解析可执行文件完整路径
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var combined = string.Join(';', userPath, machinePath).Trim(';');

            // UseShellExecute=false 时 CreateProcess 用当前进程 PATH 搜索可执行文件，
            // 应用启动后修改的 PATH 不生效，因此手动解析完整路径。
            var resolved = ResolveExecutable(fileName, combined);

            var psi = new ProcessStartInfo
            {
                FileName = resolved ?? fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
            if (!string.IsNullOrEmpty(combined))
                psi.EnvironmentVariables["PATH"] = combined;
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // 在线程池上同步读取两个流，避免管道缓冲死锁；这里不捕获 UI 上下文，
            // 因此即使在 UI 线程调用也不会产生 async 死锁（仅同步阻塞）。
            var stdoutTask = Task.Run(() => ReadAllBytes(proc.StandardOutput.BaseStream));
            var stderrTask = Task.Run(() => ReadAllBytes(proc.StandardError.BaseStream));

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
            }

            var stdout = Decode(stdoutTask.GetAwaiter().GetResult());
            var stderr = Decode(stderrTask.GetAwaiter().GetResult());
            var text = (stdout + stderr).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>执行命令但不保留输出（后台异步读取丢弃，避免缓冲区死锁），返回退出码；-1=启动失败，-2=超时。</summary>
    public static int RunNoCapture(string fileName, string args, string? workingDirectory = null, int timeoutMs = 15000)
    {
        try
        {
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var combined = string.Join(';', userPath, machinePath).Trim(';');
            var resolved = ResolveExecutable(fileName, combined);

            var psi = new ProcessStartInfo
            {
                FileName = resolved ?? fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
            if (!string.IsNullOrEmpty(combined))
                psi.EnvironmentVariables["PATH"] = combined;
            using var proc = Process.Start(psi);
            if (proc is null) return -1;
            // 后台异步读取并丢弃，防止管道缓冲区满导致死锁
            _ = Task.Run(() => { try { proc.StandardOutput.BaseStream.CopyTo(Stream.Null); } catch { } });
            _ = Task.Run(() => { try { proc.StandardError.BaseStream.CopyTo(Stream.Null); } catch { } });
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                return -2;
            }
            return proc.ExitCode;
        }
        catch { return -1; }
    }

    /// <summary>在指定 PATH 中搜索可执行文件，返回完整路径；找不到返回 null。</summary>
    private static string? ResolveExecutable(string fileName, string pathVar)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        // 已经是绝对路径或包含目录分隔符，直接用
        if (Path.IsPathRooted(fileName) || fileName.Contains('\\') || fileName.Contains('/'))
            return File.Exists(fileName) ? fileName : null;

        var exts = new[] { "", ".exe", ".cmd", ".bat", ".com" };
        var dirs = pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
        {
            var d = dir.Trim();
            if (string.IsNullOrEmpty(d)) continue;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(d, fileName + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static byte[] ReadAllBytes(Stream s)
    {
        try
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        try
        {
            // 严格 UTF-8：非法字节序列会抛出 DecoderFallbackException
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return AnsiEncoding.GetString(bytes);
        }
    }
}
