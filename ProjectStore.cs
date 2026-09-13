using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopTool;

/// <summary>一个工程记录。</summary>
public class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Python / Node / PHP</summary>
    public string Type { get; set; } = "";
    /// <summary>框架：vue / react / vite / laravel / thinkphp / 空</summary>
    public string Framework { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>虚拟环境路径（Python venv 等），可选</summary>
    public string EnvPath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Description { get; set; } = "";
    /// <summary>npm 工作目录（用户上次选择，空 = 用工程路径）</summary>
    public string NpmDir { get; set; } = "";
    /// <summary>npm 命令（用户上次执行，重开时恢复选中）</summary>
    public string NpmCommand { get; set; } = "";
    /// <summary>上传用的服务器链接 Id</summary>
    public string ServerLinkId { get; set; } = "";
    /// <summary>上次上传的本地路径（目录或文件）</summary>
    public string UploadLocalPath { get; set; } = "";
    /// <summary>上次上传的服务器目标目录</summary>
    public string UploadRemoteDir { get; set; } = "";
    /// <summary>上传自定义排除（多行，逐行保存）</summary>
    public string UploadCustomExcludes { get; set; } = "";

    public string TypeIcon => Type switch
    {
        "Python" => "\uE8A7",
        "Node" => "\uE90F",
        "PHP" => "\uED13",
        _ => "\uE8A7",
    };

    public string CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// 工程列表持久化：%LOCALAPPDATA%\SeraphineAITool\projects.json
/// </summary>
public static class ProjectStore
{
    private const string Key = "projects";
    public static List<Project> Load()
        => ConfigSync.Get<List<Project>>(Key, new List<Project>()) ?? new();
    public static void Save(IEnumerable<Project> projects)
        => ConfigSync.Set(Key, projects.ToList());
}
