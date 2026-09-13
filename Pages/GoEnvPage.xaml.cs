using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

public class GoVersionItem
{
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? InstallPath { get; set; }
    public string? DownloadUrl { get; set; }
    public Visibility InstallBtnVisibility => InstallPath is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UninstallBtnVisibility => InstallPath is not null ? Visibility.Visible : Visibility.Collapsed;
}

public class GoPackageItem
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public class GoEnvEntry
{
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed partial class GoEnvPage : Page
{
    public ObservableCollection<GoVersionItem> GoVersions { get; } = new();
    public ObservableCollection<GoPackageItem> Packages { get; } = new();
    public ObservableCollection<GoEnvEntry> GoEnvs { get; } = new();

    private string? _pkgProjectDir;
    private string? _newProjectDir;

    public GoEnvPage()
    {
        InitializeComponent();
        PackageList.ItemsSource = Packages;
        GoVersionsList.ItemsSource = GoVersions;
        PathEnvCombo.ItemsSource = GoEnvs;
        _ = LoadOverviewAsync();
    }

    // =====================================================================
    // Tab 切换
    // =====================================================================
    private void PageSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ToolchainPanel.Visibility = tag == "toolchain" ? Visibility.Visible : Visibility.Collapsed;
        PackagesPanel.Visibility = tag == "packages" ? Visibility.Visible : Visibility.Collapsed;
        NewProjectPanel.Visibility = tag == "newproject" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "toolchain" && GoVersions.Count == 0)
            _ = LoadGoVersionsAsync();
    }

    // =====================================================================
    // 概览
    // =====================================================================
    private async Task LoadOverviewAsync()
    {
        await Task.Run(() =>
        {
            var ver = ProcessHelper.Run("go", "version");
            var goRoot = ProcessHelper.Run("go", "env GOROOT");
            var goPath = ProcessHelper.Run("go", "env GOPATH");
            var where = ProcessHelper.Run("where", "go");

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                GoVersionText.Text = string.IsNullOrEmpty(ver) ? "未检测到" : ver.Replace("go version ", "").Trim();
                GoPathText.Text = string.IsNullOrEmpty(where) ? "" : where.Split('\n')[0].Trim();
                GoRootText.Text = string.IsNullOrEmpty(goRoot) ? "-" : goRoot.Trim();
                GoPathEnvText.Text = string.IsNullOrEmpty(goPath) ? "-" : goPath.Trim();
                LoadEnvCombo();
            });
        });
    }

    private void LoadEnvCombo()
    {
        GoEnvs.Clear();
        // 从 go env GOROOT 获取
        var goRoot = ProcessHelper.Run("go", "env GOROOT")?.Trim();
        if (!string.IsNullOrEmpty(goRoot) && Directory.Exists(goRoot))
        {
            GoEnvs.Add(new GoEnvEntry { DisplayName = $"go ({goRoot})", Path = goRoot });
        }
        // 扫描安装目录
        var installRoot = Path.Combine(AppContext.BaseDirectory, "env", "installers", "go");
        if (Directory.Exists(installRoot))
        {
            foreach (var dir in Directory.GetDirectories(installRoot))
            {
                var name = Path.GetFileName(dir);
                if (File.Exists(Path.Combine(dir, "bin", "go.exe")))
                    GoEnvs.Add(new GoEnvEntry { DisplayName = $"{name} ({dir})", Path = dir });
            }
        }
    }

    private void SetPathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not GoEnvEntry env) return;
        var binDir = Path.Combine(env.Path, "bin");
        SetUserPath(binDir);
        PathStatusText.Text = $"已将 {binDir} 加入用户 PATH（新开终端生效）";
    }

    private void RemovePathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not GoEnvEntry env) return;
        var binDir = Path.Combine(env.Path, "bin");
        RemoveFromUserPath(binDir);
        PathStatusText.Text = $"已从用户 PATH 移除 {binDir}";
    }

    private static void SetUserPath(string dir)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(p => p.Trim().Equals(dir, StringComparison.OrdinalIgnoreCase)))
        {
            var newPath = string.Join(';', parts.Append(dir));
            Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);
        }
    }

    private static void RemoveFromUserPath(string dir)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Trim().Equals(dir, StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), EnvironmentVariableTarget.User);
    }

    // =====================================================================
    // 工具链
    // =====================================================================
    private async Task LoadGoVersionsAsync()
    {
        GoVersionsStatus.Text = "加载中...";
        GoVersions.Clear();

        await Task.Run(() =>
        {
            // 已安装版本
            var installed = new Dictionary<string, string>();
            var installRoot = Path.Combine(AppContext.BaseDirectory, "env", "installers", "go");
            if (Directory.Exists(installRoot))
            {
                foreach (var dir in Directory.GetDirectories(installRoot))
                {
                    var verFile = Path.Combine(dir, "VERSION");
                    if (File.Exists(verFile))
                    {
                        var v = File.ReadAllText(verFile).Trim();
                        installed[v] = dir;
                    }
                    else if (File.Exists(Path.Combine(dir, "bin", "go.exe")))
                    {
                        var v = ProcessHelper.Run(Path.Combine(dir, "bin", "go.exe"), "version", dir, 10000);
                        var match = Regex.Match(v ?? "", @"go(\d+\.\d+(\.\d+)?)");
                        if (match.Success) installed[match.Groups[1].Value] = dir;
                    }
                }
            }
            // 当前系统 go
            var sysVer = ProcessHelper.Run("go", "version");
            var sysMatch = Regex.Match(sysVer ?? "", @"go(\d+\.\d+(\.\d+)?)");
            if (sysMatch.Success && !installed.ContainsKey(sysMatch.Groups[1].Value))
            {
                var sysRoot = ProcessHelper.Run("go", "env GOROOT")?.Trim();
                if (!string.IsNullOrEmpty(sysRoot)) installed[sysMatch.Groups[1].Value] = sysRoot;
            }

            // 可下载版本（从 go.dev）
            var available = new List<string>();
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var html = http.GetStringAsync("https://go.dev/dl/").Result;
                foreach (Match m in Regex.Matches(html, @"go(\d+\.\d+(\.\d+)?)\.windows-amd64\.zip"))
                {
                    var v = m.Groups[1].Value;
                    if (!available.Contains(v)) available.Add(v);
                }
            }
            catch { }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var v in available.OrderByDescending(x => x, new VersionComparer()))
                {
                    GoVersions.Add(new GoVersionItem
                    {
                        Version = v,
                        Kind = installed.ContainsKey(v) ? "已安装" : "可下载",
                        InstallPath = installed.GetValueOrDefault(v),
                        DownloadUrl = $"https://go.dev/dl/go{v}.windows-amd64.zip",
                    });
                }
                foreach (var kv in installed.OrderByDescending(x => x.Key, new VersionComparer()))
                {
                    if (!available.Contains(kv.Key))
                    {
                        GoVersions.Add(new GoVersionItem
                        {
                            Version = kv.Key,
                            Kind = "已安装（本地）",
                            InstallPath = kv.Value,
                        });
                    }
                }
                GoVersionsStatus.Text = $"共 {GoVersions.Count} 个版本";
            });
        });
    }

    private void GoSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = (sender as TextBox)?.Text.Trim().ToLower();
        var view = GoVersionsList.ItemsSource as ObservableCollection<GoVersionItem>;
        // 简单过滤：重新加载太麻烦，这里用隐藏方式
        foreach (var item in GoVersionsList.Items)
        {
            if (item is GoVersionItem gi && GoVersionsList.ContainerFromItem(item) is ListViewItem container)
            {
                container.Visibility = string.IsNullOrEmpty(filter) || gi.Version.ToLower().Contains(filter)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void RefreshGoVersionsBtn_Click(object sender, RoutedEventArgs e) => _ = LoadGoVersionsAsync();

    private async void InstallGoBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not GoVersionItem item) return;
        var installDir = Path.Combine(AppContext.BaseDirectory, "env", "installers", "go", $"go{item.Version}");
        var downloadDir = Path.Combine(AppContext.BaseDirectory, "env", "downloads", "go");
        Directory.CreateDirectory(downloadDir);
        Directory.CreateDirectory(installDir);

        var zipPath = Path.Combine(downloadDir, $"go{item.Version}.zip");
        GoVersionsStatus.Text = $"下载 go{item.Version}...";

        await Task.Run(() =>
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var data = http.GetByteArrayAsync(item.DownloadUrl).Result;
                File.WriteAllBytes(zipPath, data);

                _ = DispatcherQueue.TryEnqueue(() => GoVersionsStatus.Text = "解压中...");
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, installDir, true);
                // go zip 解压后是 go/ 子目录，把内容移到上层
                var goSubDir = Path.Combine(installDir, "go");
                if (Directory.Exists(goSubDir))
                {
                    foreach (var f in Directory.GetFiles(goSubDir))
                        File.Move(f, Path.Combine(installDir, Path.GetFileName(f)), true);
                    foreach (var d in Directory.GetDirectories(goSubDir))
                        Directory.Move(d, Path.Combine(installDir, Path.GetFileName(d)));
                    Directory.Delete(goSubDir, true);
                }
                File.WriteAllText(Path.Combine(installDir, "VERSION"), item.Version);

                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    GoVersionsStatus.Text = $"go{item.Version} 安装完成";
                    _ = LoadGoVersionsAsync();
                    LoadEnvCombo();
                });
            }
            catch (Exception ex)
            {
                _ = DispatcherQueue.TryEnqueue(() => GoVersionsStatus.Text = $"安装失败：{ex.Message}");
            }
        });
    }

    private void UninstallGoBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not GoVersionItem item || item.InstallPath is null) return;
        try
        {
            // 只允许删除我们自己安装目录下的
            var installRoot = Path.Combine(AppContext.BaseDirectory, "env", "installers", "go");
            if (!item.InstallPath.StartsWith(installRoot))
            {
                GoVersionsStatus.Text = "只能卸载通过本工具安装的版本";
                return;
            }
            Directory.Delete(item.InstallPath, true);
            GoVersionsStatus.Text = $"已卸载 go{item.Version}";
            _ = LoadGoVersionsAsync();
            LoadEnvCombo();
        }
        catch (Exception ex)
        {
            GoVersionsStatus.Text = $"卸载失败：{ex.Message}";
        }
    }

    // =====================================================================
    // 包管理
    // =====================================================================
    private async void PkgPickDirBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _pkgProjectDir = folder.Path;
            PkgProjectBox.Text = folder.Path;
            _ = RefreshPackagesAsync();
        }
    }

    private async Task RefreshPackagesAsync()
    {
        if (string.IsNullOrEmpty(_pkgProjectDir)) return;
        Packages.Clear();
        PkgStatusText.Text = "加载中...";

        await Task.Run(() =>
        {
            var output = ProcessHelper.Run("go", "list -m all", _pkgProjectDir, 30000);
            var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var line in lines.Skip(1)) // 第一行是当前模块
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        Packages.Add(new GoPackageItem
                        {
                            Name = parts[0],
                            Version = parts.Length >= 2 ? parts[1] : "",
                        });
                    }
                }
                PkgStatusText.Text = $"共 {Packages.Count} 个依赖";
            });
        });
    }

    private void RefreshPackagesBtn_Click(object sender, RoutedEventArgs e) => _ = RefreshPackagesAsync();

    private async void GoModTidyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgProjectDir)) return;
        PkgStatusText.Text = "执行 go mod tidy...";
        await Task.Run(() =>
        {
            var result = ProcessHelper.Run("go", "mod tidy", _pkgProjectDir, 120000);
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                PkgStatusText.Text = "go mod tidy 完成";
                await RefreshPackagesAsync();
            });
        });
    }

    private async void AddPackageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgProjectDir)) return;
        var pkg = AddPkgBox.Text.Trim();
        if (string.IsNullOrEmpty(pkg)) return;
        PkgStatusText.Text = $"go get {pkg}...";
        await Task.Run(() =>
        {
            ProcessHelper.Run("go", $"get {pkg}", _pkgProjectDir, 120000);
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                AddPkgBox.Text = "";
                await RefreshPackagesAsync();
            });
        });
    }

    private async void RemovePackageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgProjectDir)) return;
        if ((sender as Button)?.Tag is not GoPackageItem pkg) return;
        PkgStatusText.Text = $"移除 {pkg.Name}...";
        await Task.Run(() =>
        {
            ProcessHelper.Run("go", $"mod edit -droprequire={pkg.Name}", _pkgProjectDir, 30000);
            ProcessHelper.Run("go", "mod tidy", _pkgProjectDir, 120000);
            _ = DispatcherQueue.TryEnqueue(async () => await RefreshPackagesAsync());
        });
    }

    // =====================================================================
    // 新建项目
    // =====================================================================
    private async void NewProjPickDirBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _newProjectDir = folder.Path;
            NewProjDirBox.Text = folder.Path;
        }
    }

    private async void CreateGoProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = NewProjNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(_newProjectDir))
        {
            NewProjStatusText.Text = "请填写项目名称和存储目录";
            return;
        }
        var module = string.IsNullOrEmpty(NewProjModuleBox.Text.Trim()) ? name : NewProjModuleBox.Text.Trim();
        var projDir = Path.Combine(_newProjectDir, name);
        if (Directory.Exists(projDir))
        {
            NewProjStatusText.Text = "目录已存在";
            return;
        }

        NewProjStatusText.Text = "创建中...";
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(projDir);
                ProcessHelper.Run("go", $"mod init {module}", projDir, 30000);

                var template = (NewProjTemplateCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "";
                if (template.Contains("Gin"))
                {
                    File.WriteAllText(Path.Combine(projDir, "main.go"),
                        "package main\n\nimport \"github.com/gin-gonic/gin\"\n\nfunc main() {\n\tr := gin.Default()\n\tr.GET(\"/\", func(c *gin.Context) {\n\t\tc.JSON(200, gin.H{\"message\": \"hello\"})\n\t})\n\tr.Run(\":8080\")\n}\n");
                    ProcessHelper.Run("go", "get github.com/gin-gonic/gin", projDir, 120000);
                }
                else if (template.Contains("CLI"))
                {
                    File.WriteAllText(Path.Combine(projDir, "main.go"),
                        "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"Hello, World!\")\n}\n");
                }
                else
                {
                    File.WriteAllText(Path.Combine(projDir, "main.go"),
                        "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"Hello, World!\")\n}\n");
                }

                _ = DispatcherQueue.TryEnqueue(() => NewProjStatusText.Text = $"项目创建成功：{projDir}");
            }
            catch (Exception ex)
            {
                _ = DispatcherQueue.TryEnqueue(() => NewProjStatusText.Text = $"创建失败：{ex.Message}");
            }
        });
    }
}

internal class VersionComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return 0;
        var ax = x.Split('.');
        var ay = y.Split('.');
        for (int i = 0; i < Math.Max(ax.Length, ay.Length); i++)
        {
            var vx = i < ax.Length && int.TryParse(ax[i], out var ix) ? ix : 0;
            var vy = i < ay.Length && int.TryParse(ay[i], out var iy) ? iy : 0;
            if (vx != vy) return vy.CompareTo(vx);
        }
        return 0;
    }
}
