using System.Collections.Generic;

namespace DesktopTool.Pages;

/// <summary>
/// 一个 npm registry 源（镜像）。
/// </summary>
public class NodeSource
{
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>
/// 提供的主流 npm registry 源列表。
/// </summary>
public static class NodeSources
{
    public static readonly IReadOnlyList<NodeSource> All = new List<NodeSource>
    {
        new() { DisplayName = "官方 npmjs", Url = "https://registry.npmjs.org/" },
        new() { DisplayName = "淘宝 npmmirror", Url = "https://registry.npmmirror.com/" },
        new() { DisplayName = "腾讯云", Url = "https://mirrors.cloud.tencent.com/npm/" },
        new() { DisplayName = "华为云", Url = "https://repo.huaweicloud.com/repository/npm/" },
        new() { DisplayName = "中科大 USTC", Url = "https://mirrors.ustc.edu.cn/npm/" },
    };
}
