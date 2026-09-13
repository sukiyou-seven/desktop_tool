using System;
using System.IO;
using System.Text.Json;

namespace DesktopTool;

/// <summary>自定义 git 仓库（自建服务器）的配置。</summary>
public class GitCustomConfig
{
    public string ServerUrl { get; set; } = "";
    public string ApiType { get; set; } = "gitea"; // gitea / gitlab
}

/// <summary>自定义 git 仓库配置的持久化（存到用户数据目录）。</summary>
public static class GitSettings
{
    private const string Key = "git_custom";
    public static GitCustomConfig Load()
        => ConfigSync.Get<GitCustomConfig>(Key, new GitCustomConfig()) ?? new();
    public static void Save(GitCustomConfig config)
        => ConfigSync.Set(Key, config);
}
