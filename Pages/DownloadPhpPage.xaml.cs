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
/// 一个可下载安装的 PHP 版本（UI 展示模型），支持 TS / NTS 与 x64 / x86 变体选择。
/// </summary>
public class PhpDownloadItem : INotifyPropertyChanged
{
    public string Version { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>友好变体名列表（如 "NTS · VS17 · x64"）。</summary>
    public List<string> Variants { get; } = new();

    /// <summary>友好变体名 → zip 文件名。</summary>
    public Dictionary<string, string> VariantFiles { get; } = new();

    private string _selectedVariant = "";
    public string SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (_selectedVariant != value)
            {
                _selectedVariant = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVariant)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadUrl)));
            }
        }
    }

    public string DownloadUrl => VariantFiles.TryGetValue(SelectedVariant, out var file)
        ? $"https://downloads.php.net/~windows/releases/{file}"
        : "";

    /// <summary>目标安装目录名（基于 zip 文件名，去扩展名）。</summary>
    public string InstallDirName => VariantFiles.TryGetValue(SelectedVariant, out var file)
        ? Path.GetFileNameWithoutExtension(file)
        : $"php-{Version}";

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
/// 下载安装 PHP 页面：从官方 releases.json 获取版本与变体，下载 Windows 便携版 zip 解压到工具目录。
/// </summary>
public sealed partial class DownloadPhpPage : Page
{
    private const string ReleasesJsonUrl = "https://downloads.php.net/~windows/releases/releases.json";

    private readonly ObservableCollection<PhpDownloadItem> _items = new();
    private readonly List<PhpDownloadItem> _allItems = new();
    private bool _pageBusy;

    public DownloadPhpPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _items;
        Loaded += DownloadPhpPage_Loaded;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void DownloadPhpPage_Loaded(object sender, RoutedEventArgs e)
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

    private static string ToolPhpRoot => ToolPaths.InstallPhp;

    private static string FriendlyVariant(string key)
    {
        var parts = key.Split('-');
        if (parts.Length < 3) return key;
        return $"{parts[0].ToUpperInvariant()} · {parts[1].ToUpperInvariant()} · {parts[2]}";
    }

    private async Task LoadVersionsAsync()
    {
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            Log("正在从 downloads.php.net 获取版本列表…");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var json = await client.GetStringAsync(ReleasesJsonUrl);

            _items.Clear();
            _allItems.Clear();
            using var doc = JsonDocument.Parse(json);

            foreach (var majorProp in doc.RootElement.EnumerateObject())
            {
                var major = majorProp.Name;
                if (!majorProp.Value.TryGetProperty("version", out var verProp))
                    continue;
                var ver = verProp.GetString() ?? "";
                if (ver.Length == 0) continue;

                var item = new PhpDownloadItem
                {
                    Version = ver,
                    DisplayName = $"PHP {ver}",
                };

                // 遍历变体（ts-vs*/nts-vs* 等含 -vs 的键）
                foreach (var variantProp in majorProp.Value.EnumerateObject())
                {
                    if (!variantProp.Name.Contains("-vs", StringComparison.Ordinal)) continue;
                    if (!variantProp.Value.TryGetProperty("zip", out var zipProp)) continue;
                    if (!zipProp.TryGetProperty("path", out var pathProp)) continue;
                    var zipFile = pathProp.GetString();
                    if (string.IsNullOrEmpty(zipFile)) continue;

                    var friendly = FriendlyVariant(variantProp.Name);
                    item.Variants.Add(friendly);
                    item.VariantFiles[friendly] = zipFile;
                }

                if (item.Variants.Count == 0) continue;

                // 默认选择 NTS x64，否则第一个
                item.SelectedVariant = item.Variants.FirstOrDefault(v => v.StartsWith("NTS") && v.Contains("x64"))
                                       ?? item.Variants[0];

                // 已安装检测：工具目录下存在对应目录
                var installDir = Path.Combine(ToolPhpRoot, item.InstallDirName);
                item.IsInstalled = File.Exists(Path.Combine(installDir, "php.exe"));

                _allItems.Add(item);
            }

            // 按版本号降序排序（大版本优先）
            _allItems.Sort((a, b) => CompareVersions(b.Version, a.Version));

            Log($"获取到 {_allItems.Count} 个大版本");
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

    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PhpDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;
        await DownloadAndInstallAsync(item);
    }

    private async Task<bool> ConfirmUninstallAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "卸载 PHP",
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
        if (sender is not Button btn || btn.Tag is not PhpDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;

        var dir = Path.Combine(ToolPhpRoot, item.InstallDirName);
        if (!await ConfirmUninstallAsync(
                $"将删除该版本的安装目录：\n{dir}\n\n此操作不可恢复。"))
            return;

        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Log($"已卸载 {item.DisplayName}");
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

    private async Task DownloadAndInstallAsync(PhpDownloadItem item)
    {
        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";

        var installersDir = ToolPaths.DownloadsPhp;
        Directory.CreateDirectory(installersDir);

        var zipName = item.VariantFiles[item.SelectedVariant];
        var zipPath = Path.Combine(installersDir, zipName);
        var stagingDir = Path.Combine(installersDir, "php_staging");
        var targetDir = Path.Combine(ToolPhpRoot, item.InstallDirName);

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

            // 2. 解压到临时目录（PHP zip 解压后文件直接在根目录）
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            if (!File.Exists(Path.Combine(stagingDir, "php.exe")))
            {
                Log("解压后未找到 php.exe，安装可能异常");
                return;
            }

            // 3. 复制到工具目录（覆盖，先建子目录）
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(targetDir, Path.GetRelativePath(stagingDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }

            Log($"已安装到 {Path.Combine(targetDir, "php.exe")}");

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
}
