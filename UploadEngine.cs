using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace DesktopTool;

/// <summary>上传进度信息。</summary>
public class UploadProgress
{
    public string CurrentFile { get; set; } = "";
    public long DoneFiles { get; set; }
    public long TotalFiles { get; set; }
    public long DoneBytes { get; set; }
    public long TotalBytes { get; set; }
    public string Message { get; set; } = "";
    public bool Finished { get; set; }
    public bool Error { get; set; }
}

/// <summary>
/// 上传引擎：把本地目录/文件通过 SFTP 上传到服务器（复用「服务器管理」的服务器信息），
/// 支持自动排除运行时产物 + 用户自定义排除（支持 * ? 通配符）。
/// </summary>
public static class UploadEngine
{
    /// <summary>自动排除的运行时产物（目录名 / 文件名 / 通配模式）。</summary>
    public static readonly string[] AutoExcludePatterns =
    {
        "node_modules", ".venv", "venv", "env", "__pycache__", ".pytest_cache", ".mypy_cache",
        ".ruff_cache", ".eggs", "*.egg-info", ".git", ".hg", ".svn", ".idea", ".vscode",
        ".next", ".nuxt", ".output", "dist", "build", "obj", "bin", "target",
        "*.pyc", "*.pyo", "*.log", "*.tmp", "*.bak", ".DS_Store", "Thumbs.db",
    };

    /// <summary>判断某段相对路径（目录或文件）是否命中排除规则。</summary>
    public static bool IsExcluded(string relativePath, bool useAuto, IEnumerable<string> customPatterns)
    {
        var parts = relativePath.Split('/');
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (useAuto && MatchesAny(part, AutoExcludePatterns)) return true;
            if (customPatterns.Any(p => MatchesPattern(part, p))) return true;
        }
        return false;
    }

    private static bool MatchesAny(string name, IEnumerable<string> patterns)
        => patterns.Any(p => MatchesPattern(name, p));

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var p = pattern.Trim();
        if (string.IsNullOrEmpty(p)) return false;
        if (!p.Contains('*') && !p.Contains('?')) return string.Equals(name, p, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(p)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>把本地路径规范化成远端路径（\\ 转 /，保证以 / 开头且不以 / 结尾）。</summary>
    public static string NormalizeRemoteDir(string dir)
    {
        var d = (dir ?? "").Trim().Replace('\\', '/');
        if (d == "/") return "";
        d = d.TrimEnd('/');
        if (!d.StartsWith('/')) d = "/" + d;
        return d;
    }

    /// <summary>上传本地目录/文件（SFTP）。</summary>
    public static async Task RunAsync(
        ServerInfo server,
        string localPath,
        string remoteDir,
        bool useAutoExclude,
        IEnumerable<string> customExcludes,
        IProgress<UploadProgress> progress,
        CancellationToken ct = default)
    {
        var excludes = (customExcludes ?? Array.Empty<string>())
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        await Task.Run(() => SftpUpload(server, localPath, remoteDir, useAutoExclude, excludes, progress, ct), ct);
    }

    // ---------------- SFTP ----------------

    /// <summary>与「服务器管理」（SshService）完全一致的连接方式：密码 + 私钥双认证。</summary>
    private static SftpClient CreateSftpClient(ServerInfo server)
        => new SftpClient(SshService.CreateConnectionInfo(server));

    private static void SftpUpload(
        ServerInfo server, string localPath, string remoteDir,
        bool useAuto, List<string> excludes, IProgress<UploadProgress> progress, CancellationToken ct)
    {
        using var client = CreateSftpClient(server);
        client.Connect();

        var baseRemote = NormalizeRemoteDir(remoteDir);
        EnsureSftpDir(client, baseRemote);
        Report(progress, new UploadProgress { Message = $"已连接 {server.Username}@{server.Host}，目标 {baseRemote}" });

        if (File.Exists(localPath))
        {
            UploadOneFile(client, localPath, JoinRemote(baseRemote, Path.GetFileName(localPath)), progress, ct);
            Report(progress, new UploadProgress { Message = "上传完成", Finished = true });
            return;
        }
        if (!Directory.Exists(localPath))
        {
            Report(progress, new UploadProgress { Message = "本地路径不存在：" + localPath, Error = true });
            return;
        }

        var files = new List<string>();
        CollectFiles(localPath, useAuto, excludes, files);
        long totalBytes = 0;
        foreach (var f in files) totalBytes += new FileInfo(f).Length;
        var totalFiles = files.Count;

        Report(progress, new UploadProgress
        {
            Message = $"共 {totalFiles} 个文件，{FormatBytes(totalBytes)}",
            TotalFiles = totalFiles, TotalBytes = totalBytes,
        });

        long done = 0, bytes = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(localPath, f).Replace('\\', '/');
            var remoteFile = JoinRemote(baseRemote, rel);
            done++;
            Report(progress, new UploadProgress
            {
                CurrentFile = f, DoneFiles = done, TotalFiles = totalFiles,
                DoneBytes = bytes, TotalBytes = totalBytes,
                Message = $"[{done}/{totalFiles}] {rel}",
            });
            bytes += UploadOneFile(client, f, remoteFile, progress, ct);
        }

        client.Disconnect();
        Report(progress, new UploadProgress
        {
            Message = $"上传完成：{totalFiles} 个文件，{FormatBytes(totalBytes)}",
            Finished = true, DoneFiles = totalFiles, TotalFiles = totalFiles,
            DoneBytes = totalBytes, TotalBytes = totalBytes,
        });
    }

    private static long UploadOneFile(SftpClient client, string localFile, string remoteFile, IProgress<UploadProgress> progress, CancellationToken ct)
    {
        EnsureSftpDir(client, Path.GetDirectoryName(remoteFile)!.Replace('\\', '/'));
        using var fs = File.OpenRead(localFile);
        client.UploadFile(fs, remoteFile);
        return fs.Length;
    }

    private static void EnsureSftpDir(SftpClient client, string remoteDir)
    {
        var parts = (remoteDir ?? "").Trim('/').Split('/');
        var cur = "";
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            cur += "/" + p;
            if (!client.Exists(cur))
                client.CreateDirectory(cur);
        }
    }

    private static string JoinRemote(string baseRemote, string rel)
        => baseRemote.TrimEnd('/') + "/" + rel.TrimStart('/');

    /// <summary>SFTP 列出某目录的子目录（供浏览选择）。</summary>
    public static List<string> SftpListDirs(ServerInfo server, string remoteDir)
    {
        using var client = CreateSftpClient(server);
        client.Connect();
        var dirs = client.ListDirectory(NormalizeRemoteDir(remoteDir) == "" ? "/" : NormalizeRemoteDir(remoteDir))
            .Where(f => f.IsDirectory && f.Name is not "." and not "..")
            .Select(f => f.Name)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        client.Disconnect();
        return dirs;
    }

    /// <summary>测试服务器连接是否可用，返回错误信息，null = 成功。</summary>
    public static string? TestConnection(ServerInfo server)
    {
        try
        {
            using var client = CreateSftpClient(server);
            client.Connect();
            client.Disconnect();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------------- 通用 ----------------

    private static void CollectFiles(string root, bool useAuto, List<string> excludes, List<string> files)
    {
        foreach (var d in Directory.GetDirectories(root))
        {
            var rel = Path.GetRelativePath(root, d).Replace('\\', '/');
            if (IsExcluded(rel, useAuto, excludes)) continue;
            CollectFiles(d, useAuto, excludes, files);
        }
        foreach (var f in Directory.GetFiles(root))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (IsExcluded(rel, useAuto, excludes)) continue;
            files.Add(f);
        }
    }

    private static void Report(IProgress<UploadProgress> progress, UploadProgress p)
        => progress?.Report(p);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.##} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }
}
