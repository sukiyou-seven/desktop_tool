using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace DesktopTool;

/// <summary>已安装的应用程序信息。</summary>
public class InstalledApp
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallLocation { get; set; }
    public string? IconPath { get; set; }
    public string? UninstallString { get; set; }

    /// <summary>可直接绑定到 Image.Source 的图标文件路径（临时 PNG 或原图）。</summary>
    public string? IconFile { get; set; }

    /// <summary>可执行文件路径，用于点击启动。</summary>
    public string? ExePath { get; set; }

    /// <summary>用户设置的排序值，越大越靠前；null 表示未设置（按字母排）。</summary>
    public int? SortOrder { get; set; }

    public bool HasIcon => !string.IsNullOrEmpty(IconFile);
    public Visibility IconVisibility => HasIcon ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DefaultIconVisibility => HasIcon ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>从 Windows 注册表扫描已安装软件，并提取本地图标。</summary>
public static class InstalledApps
{
    private static readonly string[] RegistryPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    private static readonly string[] ImageExts = { ".ico", ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    private static readonly string[] IconSourceExts = { ".exe", ".dll", ".ico" };

    /// <summary>扫描所有已安装应用（去重，按名称排序）。</summary>
    public static List<InstalledApp> Scan()
    {
        var dict = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in RegistryPaths)
        {
            ScanHive(Registry.LocalMachine, path, dict);
            ScanHive(Registry.CurrentUser, path, dict);
        }

        return dict.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanHive(RegistryKey hive, string subPath, Dictionary<string, InstalledApp> dict)
    {
        try
        {
            using var key = hive.OpenSubKey(subPath);
            if (key is null) return;
            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var appKey = key.OpenSubKey(subName);
                    if (appKey is null) continue;

                    var name = appKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.Contains("Security Update", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Update for", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var iconPath = appKey.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        var comma = iconPath.IndexOf(',');
                        if (comma > 0) iconPath = iconPath.Substring(0, comma);
                        iconPath = iconPath.Trim('"', ' ');
                    }

                    var installLocation = appKey.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installLocation))
                        installLocation = installLocation.Trim('"', ' ');

                    var app = new InstalledApp
                    {
                        Name = name.Trim(),
                        Version = appKey.GetValue("DisplayVersion") as string,
                        Publisher = appKey.GetValue("Publisher") as string,
                        InstallLocation = installLocation,
                        IconPath = iconPath,
                        UninstallString = appKey.GetValue("UninstallString") as string,
                    };

                    app.IconFile = ExtractIcon(iconPath);
                    app.ExePath = FindExe(iconPath, installLocation);

                    if (!dict.ContainsKey(app.Name))
                        dict[app.Name] = app;
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>从图标路径提取可绑定的图片文件。图片文件直接用；exe/dll 提取为临时 PNG。</summary>
    private static string? ExtractIcon(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath)) return null;

        var ext = Path.GetExtension(iconPath).ToLowerInvariant();
        if (ImageExts.Contains(ext))
            return iconPath;

        if (ext is ".exe" or ".dll")
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(iconPath);
                if (icon is null) return null;
                var temp = Path.Combine(Path.GetTempPath(),
                    $"seraphine_icon_{Path.GetFileNameWithoutExtension(iconPath)}_{Math.Abs(iconPath.GetHashCode()):X}.png");
                if (!File.Exists(temp))
                {
                    using var bmp = icon.ToBitmap();
                    bmp.Save(temp, ImageFormat.Png);
                }
                return temp;
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>推断可执行文件路径：优先图标指向的 exe，其次安装目录下第一个 exe。</summary>
    private static string? FindExe(string? iconPath, string? installLocation)
    {
        if (!string.IsNullOrEmpty(iconPath)
            && iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(iconPath))
            return iconPath;

        if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
        {
            try
            {
                var exes = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length > 0) return exes[0];
            }
            catch { }
        }
        return null;
    }
}
