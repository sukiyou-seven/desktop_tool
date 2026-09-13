using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopTool;

/// <summary>已保存的数据库连接参数（带名称）。</summary>
public class DbSavedConnection
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string AuthSource { get; set; } = "admin";
}

/// <summary>按 服务器ID+数据库类型 持久化多份命名数据库连接。</summary>
public static class DbConnectionStore
{
    private const string Key = "db_connections";
    // key: "serverId|dbType"  value: 该类型下的命名连接列表
    private static Dictionary<string, List<DbSavedConnection>>? _cache;

    private static Dictionary<string, List<DbSavedConnection>> Load()
    {
        if (_cache is not null) return _cache;
        _cache = ConfigSync.Get<Dictionary<string, List<DbSavedConnection>>>(Key,
            new Dictionary<string, List<DbSavedConnection>>()) ?? new();
        return _cache;
    }

    private static void Persist() => ConfigSync.Set(Key, _cache ?? new());

    private static string KeyOf(string serverId, DbType dbType) => $"{serverId}|{dbType}";

    public static List<DbSavedConnection> ListAll(string serverId, DbType dbType)
        => Load().TryGetValue(KeyOf(serverId, dbType), out var list) ? list.ToList() : new();

    public static DbSavedConnection? Get(string serverId, DbType dbType, string name)
        => ListAll(serverId, dbType).FirstOrDefault(c => c.Name == name);

    public static void Save(string serverId, DbType dbType, DbSavedConnection conn)
    {
        var dict = Load();
        var k = KeyOf(serverId, dbType);
        if (!dict.TryGetValue(k, out var list)) { list = new(); dict[k] = list; }
        var existing = list.FirstOrDefault(c => c.Name == conn.Name);
        if (existing is not null) list.Remove(existing);
        list.Add(conn);
        Persist();
    }

    public static void Delete(string serverId, DbType dbType, string name)
    {
        var dict = Load();
        var k = KeyOf(serverId, dbType);
        if (dict.TryGetValue(k, out var list))
        {
            list.RemoveAll(c => c.Name == name);
            Persist();
        }
    }
}
