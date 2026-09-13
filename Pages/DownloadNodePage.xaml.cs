using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopTool.Pages;

/// <summary>
/// 一个可下载安装的 Node.js 版本（UI 展示模型）。
/// </summary>
public class NodeDownloadItem : INotifyPropertyChanged
{
    public string Version { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime? ReleaseDate { get; set; }
    public string ReleaseDateText => ReleaseDate is null ? "" : ReleaseDate.Value.ToString("yyyy-MM-dd");
    public string DownloadUrl { get; set; } = "";

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled != value)
            {
                _isInstalled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstalled)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonText)));
            }
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotBusy)));
            }
        }
    }

    public string StatusText => IsInstalled ? "已安装" : "";
    public Visibility StatusVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public string ButtonText => IsInstalled ? "重装" : "安装";
    public bool NotBusy => !IsBusy;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 下载安装 Node 页面：从 nodejs.org 官方源获取版本列表，下载 Windows 便携版 zip 解压到工具目录。
/// </summary>
public sealed partial class DownloadNodePage : Page
{
    private const string DistIndexUrl = "https://nodejs.org/dist/index.json";

    private readonly ObservableCollection<NodeDownloadItem> _items = new();
    private readonly List<NodeDownloadItem> _allItems = new();
    private bool _pageBusy;

    public DownloadNodePage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _items;
        Loaded += DownloadNodePage_Loaded;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void DownloadNodePage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadVersionsAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await LoadVersionsAsync();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        _items.Clear();
        foreach (var item in _allItems)
        {
            if (q.Length == 0
                || item.Version.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                _items.Add(item);
        }
    }

    private void SetPageBusy(bool busy)
    {
        _pageBusy = busy;
        BusyRing.IsActive = busy;
        RefreshBtn.IsEnabled = !busy;
    }

    private void Log(string line)
    {
        LogBox.Text += line + Environment.NewLine;
        LogBox.SelectionStart = LogBox.Text.Length;
        LogBox.SelectionLength = 0;
    }

    private static string ToolNodeDir => ToolPaths.InstallNode;

    private async Task LoadVersionsAsync()
    {
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            Log("正在从 nodejs.org 获取版本列表…");
            var current = await Task.Run(DetectInstalledNode);

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var json = await client.GetStringAsync(DistIndexUrl);

            _items.Clear();
            _allItems.Clear();
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var version = el.GetProperty("version").GetString() ?? "";
                if (version.Length == 0 || version.Contains("rc", StringComparison.OrdinalIgnoreCase)) continue;
                var ver = version.TrimStart('v');

                DateTime? date = null;
                if (el.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(d.GetString(), out var dt))
                    date = dt;

                // 标记 LTS（lts 字段非 null 且非 false）
                var isLts = el.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.String;
                var url = $"https://nodejs.org/dist/{version}/node-{version}-win-x64.zip";

                _allItems.Add(new NodeDownloadItem
                {
                    Version = ver,
                    DisplayName = $"Node v{ver}{(isLts ? " (LTS)" : "")}",
                    ReleaseDate = date,
                    DownloadUrl = url,
                    IsInstalled = current != null && string.Equals(ver, current, StringComparison.OrdinalIgnoreCase),
                });
            }

            Log($"获取到 {_allItems.Count} 个版本");
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log($"获取版本列表失败：{ex.Message}");
        }
        finally
        {
            SetPageBusy(false);
        }
    }

    /// <summary>检测本机当前托管 Node 版本（工具目录 + PATH）。</summary>
    private static string? DetectInstalledNode()
    {
        var toolNode = Path.Combine(ToolNodeDir, "node.exe");
        if (File.Exists(toolNode))
        {
            var v = ExtractVersion(RunCommand(toolNode, "--version"));
            if (!string.IsNullOrEmpty(v)) return v;
        }

        var pathNode = FirstLine(RunCommand("where", "node"));
        if (!string.IsNullOrEmpty(pathNode))
        {
            var v = ExtractVersion(RunCommand("node", "--version"));
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NodeDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;
        await DownloadAndInstallAsync(item);
    }

    private async Task<bool> ConfirmUninstallAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "卸载 Node",
            Content = message,
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NodeDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;

        if (!await ConfirmUninstallAsync(
                $"将删除工具托管的 Node 安装目录：\n{ToolNodeDir}\n\n此操作不可恢复。"))
            return;

        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            if (Directory.Exists(ToolNodeDir))
                Directory.Delete(ToolNodeDir, recursive: true);
            Log($"已卸载 Node v{item.Version}");
            await LoadVersionsAsync();
        }
        catch (Exception ex)
        {
            Log($"卸载失败：{ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
            SetPageBusy(false);
        }
    }

    private async Task DownloadAndInstallAsync(NodeDownloadItem item)
    {
        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";

        var installersDir = ToolPaths.DownloadsNode;
        Directory.CreateDirectory(installersDir);
        var zipPath = Path.Combine(installersDir, $"node-v{item.Version}-win-x64.zip");
        var stagingDir = Path.Combine(installersDir, $"node_staging_{item.Version}");

        try
        {
            // 1. 下载 zip
            Log($"下载 {item.DownloadUrl}");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            using (var response = await client.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var content = await response.Content.ReadAsStreamAsync();
                await using var fs = File.Create(zipPath);

                var buffer = new byte[81920];
                long read = 0;
                long lastPct = -1;
                int n;
                while ((n = await content.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total > 0)
                    {
                        var pct = read * 100 / total;
                        if (pct >= lastPct + 10)
                        {
                            lastPct = pct;
                            Log($"下载中 {pct}%");
                        }
                    }
                }
            }
            Log($"下载完成：{zipPath}");

            // 2. 解压到临时目录
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            // zip 内顶层为 node-vXXX-win-x64 目录
            var inner = Directory.GetDirectories(stagingDir).FirstOrDefault();
            if (inner is null || !File.Exists(Path.Combine(inner, "node.exe")))
            {
                Log("解压后未找到 node.exe，安装可能异常");
                return;
            }

            // 3. 复制到工具目录（覆盖）
            if (Directory.Exists(ToolNodeDir))
                Directory.Delete(ToolNodeDir, recursive: true);
            Directory.CreateDirectory(ToolNodeDir);
            foreach (var dir in Directory.GetDirectories(inner, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(ToolNodeDir, Path.GetRelativePath(inner, dir)));
            foreach (var file in Directory.GetFiles(inner, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(ToolNodeDir, Path.GetRelativePath(inner, file)), overwrite: true);

            Log($"已安装到 {Path.Combine(ToolNodeDir, "node.exe")}");

            // 4. 刷新列表
            await LoadVersionsAsync();
        }
        catch (Exception ex)
        {
            Log($"操作失败：{ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
            item.IsBusy = false;
            SetPageBusy(false);
        }
    }

    private static string? RunCommand(string fileName, string args) => ProcessHelper.Run(fileName, args);

    private static string ExtractVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var m = Regex.Match(output.Trim().TrimStart('v'), @"\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : output.Trim().TrimStart('v');
    }

    private static string FirstLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        return output.Split('\n')[0].Trim();
    }
}
