using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopTool;

/// <summary>服务器操作系统类型。</summary>
public enum ServerOsType { Linux, Windows }

/// <summary>一台远程服务器的连接信息（密码不存 JSON，走 Windows 凭据管理器）。</summary>
public class ServerInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    /// <summary>私钥文件路径（可选）。为空则用密码。</summary>
    public string? KeyFile { get; set; }
    public ServerOsType OsType { get; set; } = ServerOsType.Linux;
    public string Group { get; set; } = "默认";
    public string Notes { get; set; } = "";

    /// <summary>运行时密码（从凭据管理器加载，不序列化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Password { get; set; }
}

/// <summary>服务器列表持久化（JSON，密码走凭据管理器）。</summary>
public static class ServerStore
{
    private const string Key = "servers";
    private static string CredTarget(ServerInfo s) => $"ssh:{s.Id}";

    public static List<ServerInfo> Load()
    {
        MigrateLegacyServerLinks(); // 一次性迁移旧「服务器链接」数据，避免配置丢失
        var list = ConfigSync.Get<List<ServerInfo>>(Key, new List<ServerInfo>()) ?? new();
        foreach (var s in list)
        {
            try
            {
                if (WindowsCredentialStore.TryRead(CredTarget(s), out _, out var pwd))
                    s.Password = pwd;
            }
            catch { }
        }
        return list;
    }

    public static void Save(List<ServerInfo> servers)
    {
        foreach (var s in servers)
        {
            try
            {
                if (!string.IsNullOrEmpty(s.Password))
                    WindowsCredentialStore.Save(CredTarget(s), s.Username, s.Password);
            }
            catch { }
        }
        ConfigSync.Set(Key, servers);
    }

    public static void Delete(ServerInfo server)
    {
        try { WindowsCredentialStore.Delete(CredTarget(server)); }
        catch { }
    }

    // ---------------- 旧「服务器链接」数据迁移 ----------------

    private sealed class LegacyServerLink
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Protocol { get; set; } = "SFTP";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string PrivateKeyPath { get; set; } = "";
        public string DefaultRemoteDir { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>把历史版本「服务器链接」页存储的配置并入「服务器管理」，随后删除旧 key。</summary>
    private static void MigrateLegacyServerLinks()
    {
        const string legacyKey = "serverLinks";
        JsonElement? raw;
        try { raw = ConfigSync.GetRaw(legacyKey); }
        catch { return; }
        if (raw is null || raw.Value.ValueKind != JsonValueKind.Array) return;

        try
        {
            var legacy = JsonSerializer.Deserialize<List<LegacyServerLink>>(raw.Value.GetRawText());
            if (legacy is null || legacy.Count == 0) return;

            var servers = ConfigSync.Get<List<ServerInfo>>(Key, new List<ServerInfo>()) ?? new();
            var changed = false;
            foreach (var l in legacy)
            {
                if (string.IsNullOrWhiteSpace(l.Host)) continue;
                var dup = servers.Any(s =>
                    string.Equals(s.Host, l.Host, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Username, l.Username, StringComparison.OrdinalIgnoreCase));
                if (dup) continue;

                var s = new ServerInfo
                {
                    Id = string.IsNullOrEmpty(l.Id) ? Guid.NewGuid().ToString("N") : l.Id,
                    Name = string.IsNullOrWhiteSpace(l.Name) ? l.Host : l.Name,
                    Host = l.Host,
                    Port = l.Port > 0 ? l.Port : 22,
                    Username = string.IsNullOrWhiteSpace(l.Username) ? "root" : l.Username,
                    KeyFile = string.IsNullOrWhiteSpace(l.PrivateKeyPath) ? null : l.PrivateKeyPath,
                    Group = "默认",
                    Notes = string.IsNullOrWhiteSpace(l.DefaultRemoteDir) ? "从服务器链接迁移" : $"从服务器链接迁移（默认目录 {l.DefaultRemoteDir}）",
                };
                if (!string.IsNullOrEmpty(l.Password))
                {
                    try { WindowsCredentialStore.Save(CredTarget(s), s.Username, l.Password); }
                    catch { }
                }
                servers.Add(s);
                changed = true;
            }
            if (changed)
            {
                ConfigSync.Set(Key, servers);
                ConfigSync.Delete(legacyKey);
            }
            else if (legacy.Count > 0)
            {
                ConfigSync.Delete(legacyKey);
            }
        }
        catch { }
    }
}
