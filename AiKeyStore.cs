using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopTool;

/// <summary>AI 平台类型。</summary>
public enum AiPlatform
{
    DeepSeek,
}

/// <summary>已保存的 AI API Key。</summary>
public class AiKeyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AiPlatform Platform { get; set; }
    public string Label { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

/// <summary>AI API Key 持久化存储。</summary>
public static class AiKeyStore
{
    private const string Key = "ai_keys";
    private static List<AiKeyEntry>? _cache;

    public static List<AiKeyEntry> Load()
    {
        if (_cache is not null) return _cache;
        _cache = ConfigSync.Get<List<AiKeyEntry>>(Key, new List<AiKeyEntry>()) ?? new();
        return _cache;
    }

    private static void Persist() => ConfigSync.Set(Key, _cache ?? new());

    public static void Add(AiKeyEntry entry) { Load().Add(entry); Persist(); }
    public static void Remove(string id) { Load().RemoveAll(e => e.Id == id); Persist(); }
    public static List<AiKeyEntry> ByPlatform(AiPlatform p) => Load().Where(e => e.Platform == p).ToList();
}
