using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopTool.Pages;

/// <summary>
/// 一个可下载安装的 uv 版本（UI 展示模型）。
/// </summary>
public class UvDownloadItem : INotifyPropertyChanged
{
    public string Tag { get; set; } = "";
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
/// 下载安装 uv 页面：从 GitHub 官方 Releases 获取版本，下载 Windows 压缩包解压到工具目录。
/// </summary>
public sealed partial class DownloadUvPage : Page
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/astral-sh/uv/releases";
    private const string AssetName = "uv-x86_64-pc-windows-msvc.zip";

    private readonly ObservableCollection<UvDownloadItem> _items = new();
    private bool _pageBusy;

    public DownloadUvPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _items;
        Loaded += DownloadUvPage_Loaded;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void DownloadUvPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadVersionsAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await LoadVersionsAsync();

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

    private static string ToolsDir => ToolPaths.InstallUv;

    private async Task LoadVersionsAsync()
    {
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            Log("正在从 GitHub 获取 uv Releases…");
            var current = await Task.Run(DetectInstalledUv);
            CurrentVersionText.Text = current is null ? "本机未检测到 uv" : $"本机当前 uv {current}";

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SeraphineAITool", "1.0"));
            var json = await client.GetStringAsync(ReleasesApiUrl);

            _items.Clear();
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var tag = el.GetProperty("tag_name").GetString() ?? "";
                if (tag.Length == 0) continue;
                var ver = tag.TrimStart('v');

                DateTime? date = null;
                if (el.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(pa.GetString(), out var dt))
                    date = dt;

                var url = $"https://github.com/astral-sh/uv/releases/download/{tag}/{AssetName}";
                _items.Add(new UvDownloadItem
                {
                    Tag = tag,
                    DisplayName = $"uv {ver}",
                    ReleaseDate = date,
                    DownloadUrl = url,
                    IsInstalled = current != null && string.Equals(ver, current, StringComparison.OrdinalIgnoreCase),
                });
            }

            Log($"获取到 {_items.Count} 个版本");
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

    /// <summary>检测本机当前 uv 版本（PATH 优先，其次工具目录）。</summary>
    private static string? DetectInstalledUv()
    {
        var pathUv = FirstLine(RunCommand("where", "uv"));
        if (!string.IsNullOrEmpty(pathUv))
        {
            var v = ExtractVersion(RunCommand("uv", "--version"));
            if (!string.IsNullOrEmpty(v)) return v;
        }

        var toolUv = Path.Combine(ToolsDir, "uv.exe");
        if (File.Exists(toolUv))
        {
            var v = ExtractVersion(RunCommand(toolUv, "--version"));
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not UvDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;
        await DownloadAndInstallAsync(item);
    }

    private async Task<bool> ConfirmUninstallAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "卸载 uv",
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
        if (sender is not Button btn || btn.Tag is not UvDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;

        if (!await ConfirmUninstallAsync(
                $"将删除工具托管的 uv 安装目录：\n{ToolsDir}\n\n此操作不可恢复。"))
            return;

        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            if (Directory.Exists(ToolsDir))
                Directory.Delete(ToolsDir, recursive: true);
            Log($"已卸载 uv {item.DisplayName}");
            CurrentVersionText.Text = "本机未检测到 uv";
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

    private async Task DownloadAndInstallAsync(UvDownloadItem item)
    {
        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";

        var installersDir = ToolPaths.DownloadsUv;
        Directory.CreateDirectory(installersDir);
        var zipPath = Path.Combine(installersDir, $"{AssetName.Replace("-windows-msvc", "")}-{item.Tag}.zip");

        try
        {
            // 1. 下载 zip
            Log($"下载 {item.DownloadUrl}");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SeraphineAITool", "1.0"));
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

            // 2. 解压到工具目录（先清理旧目录实现覆盖）
            if (Directory.Exists(ToolsDir))
                Directory.Delete(ToolsDir, recursive: true);
            Directory.CreateDirectory(ToolsDir);
            ZipFile.ExtractToDirectory(zipPath, ToolsDir);
            var uvExe = Path.Combine(ToolsDir, "uv.exe");
            if (!File.Exists(uvExe))
            {
                Log("解压后未找到 uv.exe，安装可能异常");
                return;
            }
            Log($"已安装到 {uvExe}");

            // 3. 刷新列表
            await LoadVersionsAsync();
        }
        catch (Exception ex)
        {
            Log($"操作失败：{ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
            SetPageBusy(false);
        }
    }

    private static string? RunCommand(string fileName, string args) => ProcessHelper.Run(fileName, args);

    private static string ExtractVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var m = Regex.Match(output.Trim(), @"\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : output.Trim();
    }

    private static string FirstLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        return output.Split('\n')[0].Trim();
    }
}
