using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopTool;

/// <summary>
/// 一条用户自定义添加的 Python 解释器记录。
/// </summary>
public class CustomPythonEntry
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 自定义解释器路径的持久化存储（JSON 文件）。
/// 位置：%LOCALAPPDATA%\SeraphineAITool\custom_pythons.json
/// </summary>
public static class PythonEnvStorage
{
    private const string Key = "custom_pythons";

    public static List<CustomPythonEntry> Load()
        => ConfigSync.Get<List<CustomPythonEntry>>(Key, new List<CustomPythonEntry>()) ?? new();

    public static void Save(IEnumerable<CustomPythonEntry> entries)
        => ConfigSync.Set(Key, entries.ToList());
}

/// <summary>
/// 应用级设置的轻量持久化（JSON）。
/// 位置：%LOCALAPPDATA%\SeraphineAITool\settings.json
/// </summary>
public static class SettingsStore
{
    public static string? GetPipSource() => ConfigSync.Get<string?>("pip_source_url");
    public static void SetPipSource(string url) => ConfigSync.Set("pip_source_url", url);
    public static string? GetNpmSource() => ConfigSync.Get<string?>("npm_source_url");
    public static void SetNpmSource(string url) => ConfigSync.Set("npm_source_url", url);
}
