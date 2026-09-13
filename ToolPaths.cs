using System;
using System.IO;

namespace DesktopTool;

/// <summary>
/// 统一管理工具的数据目录（下载缓存 + 安装产物），
/// 位于软件运行目录下的 env 文件夹：
///   env\downloads\<语言>   —— 下载的安装包 / 压缩包缓存
///   env\installers\<语言>  —— 安装后的工具产物
/// </summary>
public static class ToolPaths
{
    /// <summary>软件运行目录（exe 所在目录）。</summary>
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    private static string EnvDir => Path.Combine(BaseDir, "env");

    public static string Downloads(string lang) => Path.Combine(EnvDir, "downloads", lang);
    public static string Installers(string lang) => Path.Combine(EnvDir, "installers", lang);

    // ---- 下载缓存目录 ----
    public static string DownloadsPython => Downloads("python");
    public static string DownloadsNode => Downloads("node");
    public static string DownloadsPhp => Downloads("php");
    public static string DownloadsUv => Downloads("uv");
    public static string DownloadsConda => Downloads("conda");
    public static string DownloadsComposer => Downloads("composer");

    // ---- 安装产物目录 ----
    public static string InstallPython => Installers("python");
    public static string InstallNode => Installers("node");
    public static string InstallPhp => Installers("php");
    public static string InstallUv => Installers("uv");
    public static string InstallConda => Installers("conda");
    public static string InstallComposer => Installers("composer");

    // ---- 关键文件快捷路径 ----
    public static string NodeExe => Path.Combine(InstallNode, "node.exe");
    public static string ComposerFile => Path.Combine(InstallComposer, "composer.phar");

    /// <summary>Python 用户级安装器装到的根目录（各版本在其下 Python3xx 子目录）。</summary>
    public static string PythonRoot => InstallPython;
}
