using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace DesktopTool;

/// <summary>服务器实时状态信息。</summary>
public class ServerStats
{
    public string Hostname { get; set; } = "";
    public string OsName { get; set; } = "";
    public string Kernel { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string CpuModel { get; set; } = "";
    public int CpuCores { get; set; }
    public double CpuUsagePercent { get; set; }
    public long MemoryTotalMb { get; set; }
    public long MemoryUsedMb { get; set; }
    public long MemoryFreeMb { get; set; }
    public double MemoryUsagePercent { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
    public List<NetworkInfo> Networks { get; set; } = new();
    public List<ProcessInfo> TopProcesses { get; set; } = new();
    public string RawOutput { get; set; } = "";
}

public class DiskInfo
{
    public string Filesystem { get; set; } = "";
    public string Size { get; set; } = "";
    public string Used { get; set; } = "";
    public string Avail { get; set; } = "";
    public string UsePercent { get; set; } = "";
    public string Mount { get; set; } = "";
}

public class NetworkInfo
{
    public string Interface { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Mac { get; set; } = "";
}

public class ProcessInfo
{
    public string Pid { get; set; } = "";
    public string CpuPercent { get; set; } = "";
    public string MemPercent { get; set; } = "";
    public string Command { get; set; } = "";
}

/// <summary>远程文件条目。</summary>
public class RemoteFile
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    /// <summary>文件夹图标或文件图标。</summary>
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }
}

/// <summary>
/// SSH 远程管理服务封装：连接、命令执行、状态采集、SFTP 文件管理。
/// 面向 Linux（SSH 原生）；Windows 需启用 OpenSSH 服务器后同样可用。
/// </summary>
public static class SshService
{
    /// <summary>创建并连接一个 SSH 客户端。调用方负责 Dispose。</summary>
    public static SshClient Connect(ServerInfo server)
    {
        var client = new SshClient(CreateConnectionInfo(server));
        try
        {
            client.Connect();
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw WrapAuthError(ex);
        }
    }

    /// <summary>创建并连接一个 SFTP 客户端。调用方负责 Dispose。</summary>
    public static SftpClient ConnectSftp(ServerInfo server)
    {
        var client = new SftpClient(CreateConnectionInfo(server));
        try
        {
            client.Connect();
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw WrapAuthError(ex);
        }
    }

    /// <summary>
    /// 构建连接信息：同时提供密码与私钥两种认证方式（谁可用用谁），
    /// 兼容「服务器已禁用密码登录、仅收公钥」等场景。
    /// </summary>
    public static ConnectionInfo CreateConnectionInfo(ServerInfo server)
    {
        var port = server.Port > 0 ? server.Port : 22;
        var methods = new List<AuthenticationMethod>
        {
            new PasswordAuthenticationMethod(server.Username, server.Password ?? ""),
        };
        var keyPath = ResolveKeyPath(server.KeyFile);
        if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
        {
            try
            {
                methods.Add(new PrivateKeyAuthenticationMethod(server.Username, new PrivateKeyFile(keyPath)));
            }
            catch
            {
                // 私钥解析失败（格式/口令）时忽略，回退密码认证
            }
        }
        return new ConnectionInfo(server.Host, port, server.Username, methods.ToArray());
    }

    /// <summary>私钥路径支持 ~ 展开（~/.ssh/id_rsa → C:\Users\xxx\.ssh\id_rsa）。</summary>
    public static string? ResolveKeyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"', '\'');
        if (p == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p.StartsWith("~/") || p.StartsWith("~\\"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..].TrimStart('\\', '/'));
        return p;
    }

    /// <summary>把 SSH.NET 的认证类错误转成带指引的中文提示。</summary>
    private static Exception WrapAuthError(Exception ex)
    {
        if (ex.Message.Contains("No suitable authentication method", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "认证失败：服务器不接受当前凭据。请检查：①「服务器管理」里该服务器的密码/私钥路径是否正确；②服务器是否已禁用密码登录（若已禁用，需在服务器开启密码登录，或配置密钥对并在工具里填写私钥路径）。",
                ex);
        }
        return ex;
    }

    /// <summary>执行单条命令，返回退出码和输出。</summary>
    public static (int ExitCode, string Output, string Error) Execute(SshClient client, string command)
    {
        using var cmd = client.RunCommand(command);
        return (cmd.ExitStatus, cmd.Result ?? "", cmd.Error ?? "");
    }

    /// <summary>采集服务器状态（Linux）。</summary>
    public static ServerStats GetStats(SshClient client)
    {
        var stats = new ServerStats();

        // 主机名 / 系统
        var (_, hostname, _) = SafeExec(client, "hostname");
        stats.Hostname = hostname.Trim();

        var (_, uname, _) = SafeExec(client, "uname -srmo");
        stats.Kernel = uname.Trim();

        var (_, osRelease, _) = SafeExec(client, "cat /etc/os-release 2>/dev/null | grep PRETTY_NAME | cut -d'\"' -f2");
        stats.OsName = string.IsNullOrWhiteSpace(osRelease) ? stats.Kernel : osRelease.Trim();

        var (_, uptime, _) = SafeExec(client, "uptime -p 2>/dev/null || uptime");
        stats.Uptime = uptime.Trim();

        // CPU
        var (_, lscpu, _) = SafeExec(client, "lscpu 2>/dev/null | grep 'Model name' | cut -d: -f2 | xargs");
        stats.CpuModel = lscpu.Trim();
        var (_, nproc, _) = SafeExec(client, "nproc");
        int.TryParse(nproc.Trim(), out var cores);
        stats.CpuCores = cores > 0 ? cores : 1;

        var (_, cpuLine, _) = SafeExec(client, "top -bn1 | grep 'Cpu(s)' | head -1");
        var cpuMatch = Regex.Match(cpuLine, @"([\d.]+)\s*%?\s*id");
        if (cpuMatch.Success && double.TryParse(cpuMatch.Groups[1].Value, out var idle))
            stats.CpuUsagePercent = Math.Round(100 - idle, 1);

        // 内存
        var (_, memLine, _) = SafeExec(client, "free -m | grep Mem");
        var memParts = memLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (memParts.Length >= 4)
        {
            long.TryParse(memParts[1], out var total);
            long.TryParse(memParts[2], out var used);
            long.TryParse(memParts[3], out var free);
            stats.MemoryTotalMb = total;
            stats.MemoryUsedMb = used;
            stats.MemoryFreeMb = free;
            stats.MemoryUsagePercent = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0;
        }

        // 磁盘
        var (_, dfOut, _) = SafeExec(client, "df -h --output=source,size,used,avail,pcent,target 2>/dev/null | tail -n +2");
        foreach (var line in dfOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6 && !parts[0].StartsWith("tmpfs") && !parts[0].StartsWith("devtmpfs"))
                stats.Disks.Add(new DiskInfo
                {
                    Filesystem = parts[0],
                    Size = parts[1],
                    Used = parts[2],
                    Avail = parts[3],
                    UsePercent = parts[4],
                    Mount = parts[5],
                });
        }

        // 网络
        var (_, ipOut, _) = SafeExec(client, "ip -o addr show 2>/dev/null | grep inet | grep -v 127.0.0.1");
        foreach (var line in ipOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
                stats.Networks.Add(new NetworkInfo
                {
                    Interface = parts[1].TrimEnd(':'),
                    IpAddress = parts[3],
                });
        }

        // 进程 Top 10
        var (_, psOut, _) = SafeExec(client, "ps aux --sort=-%cpu | head -11 | tail -n +2");
        foreach (var line in psOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 11)
                stats.TopProcesses.Add(new ProcessInfo
                {
                    Pid = parts[1],
                    CpuPercent = parts[2],
                    MemPercent = parts[3],
                    Command = string.Join(" ", parts.Skip(10)),
                });
        }

        stats.RawOutput = "";
        return stats;
    }

    private static (int, string, string) SafeExec(SshClient client, string cmd)
    {
        try { return Execute(client, cmd); }
        catch { return (-1, "", ""); }
    }

    // ---------- SFTP 文件管理 ----------

    public static List<RemoteFile> ListDirectory(SftpClient sftp, string path)
    {
        var files = sftp.ListDirectory(path);
        return files
            .Where(f => f.Name != "." && f.Name != "..")
            .OrderByDescending(f => f.IsDirectory)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new RemoteFile
            {
                Name = f.Name,
                FullPath = f.FullName,
                IsDirectory = f.IsDirectory,
                Size = f.Length,
                Modified = f.LastWriteTime,
            })
            .ToList();
    }

    public static void UploadFile(SftpClient sftp, string localPath, string remotePath)
    {
        using var fs = File.OpenRead(localPath);
        sftp.UploadFile(fs, remotePath, true);
    }

    public static void DownloadFile(SftpClient sftp, string remotePath, string localPath)
    {
        using var fs = File.Create(localPath);
        sftp.DownloadFile(remotePath, fs);
    }

    public static void DeleteRemote(SftpClient sftp, string path, bool isDirectory)
    {
        if (isDirectory)
            sftp.DeleteDirectory(path);
        else
            sftp.DeleteFile(path);
    }

    public static void CreateDirectory(SftpClient sftp, string path)
    {
        sftp.CreateDirectory(path);
    }
}
