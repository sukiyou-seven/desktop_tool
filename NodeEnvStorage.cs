using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopTool;

/// <summary>
/// 一条用户自定义添加的 Node.js 运行时记录。
/// </summary>
public class CustomNodeEntry
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 自定义 node.exe 路径的持久化存储（JSON 文件）。
/// 位置：%LOCALAPPDATA%\SeraphineAITool\custom_nodes.json
/// </summary>
public static class NodeEnvStorage
{
    private const string Key = "custom_nodes";
    public static List<CustomNodeEntry> Load()
        => ConfigSync.Get<List<CustomNodeEntry>>(Key, new List<CustomNodeEntry>()) ?? new();
    public static void Save(IEnumerable<CustomNodeEntry> entries)
        => ConfigSync.Set(Key, entries.ToList());
}

/// <summary>
/// 一条用户添加的 Node 项目目录记录（含 package.json 的项目文件夹）。
/// </summary>
public class NodeProjectEntry
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 项目目录的持久化存储（JSON 文件）。
/// 位置：%LOCALAPPDATA%\SeraphineAITool\custom_projects.json
/// </summary>
public static class NodeProjectStore
{
    private const string Key = "node_projects";
    public static List<NodeProjectEntry> Load()
        => ConfigSync.Get<List<NodeProjectEntry>>(Key, new List<NodeProjectEntry>()) ?? new();
    public static void Save(IEnumerable<NodeProjectEntry> entries)
        => ConfigSync.Set(Key, entries.ToList());
}
