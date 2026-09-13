using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopTool;

/// <summary>
/// 一条用户自定义添加的 PHP 运行时记录。
/// </summary>
public class CustomPhpEntry
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 自定义 php.exe 路径的持久化存储（JSON 文件）。
/// 位置：%LOCALAPPDATA%\SeraphineAITool\custom_php.json
/// </summary>
public static class PhpEnvStorage
{
    private const string Key = "custom_phps";
    public static List<CustomPhpEntry> Load()
        => ConfigSync.Get<List<CustomPhpEntry>>(Key, new List<CustomPhpEntry>()) ?? new();
    public static void Save(IEnumerable<CustomPhpEntry> entries)
        => ConfigSync.Set(Key, entries.ToList());
}
