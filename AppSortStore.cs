using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopTool;

/// <summary>已安装应用的自定义排序持久化（应用名 → 排序数字）。</summary>
public static class AppSortStore
{
    private const string Key = "app_sort";
    private static Dictionary<string, int>? _cache;

    private static Dictionary<string, int> Load()
    {
        if (_cache is not null) return _cache;
        _cache = ConfigSync.Get<Dictionary<string, int>>(Key, new Dictionary<string, int>()) ?? new();
        return _cache;
    }

    private static void Save() => ConfigSync.Set(Key, _cache ?? new());

    public static int? Get(string name) => Load().TryGetValue(name, out var v) ? v : null;
    public static void Set(string name, int value) { Load()[name] = value; Save(); }
    public static void Remove(string name) { if (Load().Remove(name)) Save(); }
}
