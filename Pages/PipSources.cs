using System.Collections.Generic;

namespace DesktopTool.Pages;

/// <summary>
/// 一个 pip 源（镜像）。
/// </summary>
public class PipSource
{
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>
/// 提供的主流 pip 源列表。
/// </summary>
public static class PipSources
{
    public static readonly IReadOnlyList<PipSource> All = new List<PipSource>
    {
        new() { DisplayName = "官方 PyPI", Url = "https://pypi.org/simple" },
        new() { DisplayName = "清华 TUNA", Url = "https://pypi.tuna.tsinghua.edu.cn/simple" },
        new() { DisplayName = "阿里云", Url = "https://mirrors.aliyun.com/pypi/simple" },
        new() { DisplayName = "腾讯云", Url = "https://mirrors.cloud.tencent.com/pypi/simple" },
        new() { DisplayName = "中科大 USTC", Url = "https://pypi.mirrors.ustc.edu.cn/simple" },
        new() { DisplayName = "华为云", Url = "https://mirrors.huaweicloud.com/repository/pypi/simple" },
    };
}
