using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DesktopTool.Pages;

/// <summary>
/// 一个可下载安装的官方 Python 版本（UI 展示模型）。
/// </summary>
public class PythonDownloadItem : INotifyPropertyChanged
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
/// 下载安装 Python 页面：从 python.org 官方源获取版本列表，下载官方安装器并静默安装。
/// </summary>
public sealed partial class DownloadPythonPage : Page
{
    private const string ReleaseApiUrl = "https://www.python.org/api/v2/downloads/release/";

    private readonly ObservableCollection<PythonDownloadItem> _items = new();
    private readonly List<PythonDownloadItem> _allItems = new();
    private bool _pageBusy;

    public DownloadPythonPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _items;
        Loaded += DownloadPythonPage_Loaded;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void DownloadPythonPage_Loaded(object sender, RoutedEventArgs e)
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

    private async Task LoadVersionsAsync()
    {
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            Log("正在从 python.org 获取版本列表…");
            var installed = GetInstalledVersions();

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var json = await client.GetStringAsync(ReleaseApiUrl);

            _items.Clear();
            _allItems.Clear();
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.GetProperty("name").GetString() ?? "";

                // 跳过预发布
                if (el.TryGetProperty("is_prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                    continue;

                // 只保留 3.x 稳定版（官方 Windows .exe 安装器）
                var m = Regex.Match(name, @"^Python\s+3\.(\d+)\.(\d+)$");
                if (!m.Success) continue;

                var version = $"3.{m.Groups[1].Value}.{m.Groups[2].Value}";
                DateTime? releaseDate = null;
                if (el.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(rd.GetString(), out var dt))
                    releaseDate = dt;

                var url = $"https://www.python.org/ftp/python/{version}/python-{version}-amd64.exe";

                _items.Add(new PythonDownloadItem
                {
                    Version = version,
                    DisplayName = $"Python {version}",
                    ReleaseDate = releaseDate,
                    DownloadUrl = url,
                    IsInstalled = installed.Contains(version),
                });
            }
            _allItems.AddRange(_items);

            Log($"获取到 {_items.Count} 个稳定版本");
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

    /// <summary>检测本机已安装的官方 Python 版本（用户级 + 系统级默认目录）。</summary>
    private static HashSet<string> GetInstalledVersions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            ToolPaths.InstallPython,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Python"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, "python.exe"))) continue;
                var name = Path.GetFileName(dir);
                var m = Regex.Match(name, @"Python(3)(\d+)");
                if (m.Success)
                    set.Add($"{m.Groups[1].Value}.{m.Groups[2].Value}");
            }
        }
        return set;
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PythonDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;
        await DownloadAndInstallAsync(item);
    }

    private async Task<bool> ConfirmUninstallAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "卸载 Python",
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
        if (sender is not Button btn || btn.Tag is not PythonDownloadItem item) return;
        if (_pageBusy || item.IsBusy) return;

        if (!await ConfirmUninstallAsync(
                $"将卸载 Python {item.Version}（用户级安装）。\n\n此操作不可恢复。"))
            return;

        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";
        try
        {
            var majorMinor = string.Join("", item.Version.Split('.').Take(2));
            var installDir = Path.Combine(ToolPaths.InstallPython, $"Python{majorMinor}");
            var installerPath = Path.Combine(ToolPaths.DownloadsPython, $"python-{item.Version}-amd64.exe");

            // 1. 优先用缓存的官方安装器静默卸载（干净，清注册项）
            if (File.Exists(installerPath))
            {
                Log("运行官方安装器卸载（/uninstall /quiet）…");
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/uninstall /quiet",
                    UseShellExecute = true,
                };
                var proc = Process.Start(psi);
                if (proc is not null) await proc.WaitForExitAsync();
                Log("卸载器执行完成");
            }
            else
            {
                Log("未找到缓存的安装器，将直接删除安装目录");
            }

            // 2. 兜底：确保安装目录被删除
            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
                Log($"已删除安装目录 {installDir}");
            }

            Log($"已卸载 Python {item.Version}");
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

    private async Task DownloadAndInstallAsync(PythonDownloadItem item)
    {
        item.IsBusy = true;
        SetPageBusy(true);
        LogBox.Text = "";

        var destDir = ToolPaths.DownloadsPython;
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, $"python-{item.Version}-amd64.exe");

        try
        {
            // 1. 下载官方安装器
            Log($"下载 {item.DownloadUrl}");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            using (var response = await client.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var content = await response.Content.ReadAsStreamAsync();
                await using var fs = File.Create(dest);

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
                        if (pct >= lastPct + 5)
                        {
                            lastPct = pct;
                            Log($"下载中 {pct}%");
                        }
                    }
                }
            }
            Log($"下载完成：{dest}");

            // 2. 静默安装（用户级，不修改 PATH、不装 launcher，装到工具安装目录）
            var majorMinor = string.Join("", item.Version.Split('.').Take(2));
            var targetDir = Path.Combine(ToolPaths.InstallPython, $"Python{majorMinor}");
            Log($"开始静默安装（用户级，装到 {targetDir}）…");
            var psi = new ProcessStartInfo
            {
                FileName = dest,
                Arguments = $"/quiet InstallAllUsers=0 PrependPath=0 Include_launcher=0 InstallLauncherAllUsers=0 Include_pip=1 Include_test=0 TargetDir=\"{targetDir}\"",
                UseShellExecute = true,
            };
            var proc = Process.Start(psi);
            if (proc is null)
            {
                Log("安装器启动失败");
                return;
            }
            await proc.WaitForExitAsync();
            Log(proc.ExitCode == 0 ? "安装完成" : $"安装器退出码 {proc.ExitCode}（可能安装失败）");

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
}
