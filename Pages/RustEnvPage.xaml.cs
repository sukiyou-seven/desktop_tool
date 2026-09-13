using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

public class RustToolchainItem
{
    public string Name { get; set; } = "";
    public string IsDefault { get; set; } = "";
}

public class RustCrateItem
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public class RustEnvEntry
{
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed partial class RustEnvPage : Page
{
    public ObservableCollection<RustToolchainItem> Toolchains { get; } = new();
    public ObservableCollection<RustCrateItem> Crates { get; } = new();
    public ObservableCollection<RustEnvEntry> Envs { get; } = new();

    private string? _pkgDir;
    private string? _newProjDir;

    public RustEnvPage()
    {
        InitializeComponent();
        ToolchainList.ItemsSource = Toolchains;
        PackageList.ItemsSource = Crates;
        PathEnvCombo.ItemsSource = Envs;
        _ = LoadOverviewAsync();
    }

    private void PageSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ToolchainPanel.Visibility = tag == "toolchain" ? Visibility.Visible : Visibility.Collapsed;
        PackagesPanel.Visibility = tag == "packages" ? Visibility.Visible : Visibility.Collapsed;
        NewProjectPanel.Visibility = tag == "newproject" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "toolchain") _ = LoadToolchainsAsync();
    }

    private async Task LoadOverviewAsync()
    {
        await Task.Run(() =>
        {
            var rustc = ProcessHelper.Run("rustc", "--version");
            var cargo = ProcessHelper.Run("cargo", "--version");
            var def = ProcessHelper.Run("rustup", "show active-toolchain");
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                RustcVersionText.Text = string.IsNullOrEmpty(rustc) ? "未检测到" : rustc.Trim();
                CargoVersionText.Text = string.IsNullOrEmpty(cargo) ? "未检测到" : cargo.Trim();
                ToolchainText.Text = string.IsNullOrEmpty(def) ? "-" : def.Trim();
                LoadEnvCombo();
            });
        });
    }

    private void LoadEnvCombo()
    {
        Envs.Clear();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cargoBin = Path.Combine(home, ".cargo", "bin");
        if (Directory.Exists(cargoBin))
            Envs.Add(new RustEnvEntry { DisplayName = $"cargo ({cargoBin})", Path = cargoBin });
    }

    private void SetPathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not RustEnvEntry env) return;
        SetUserPath(env.Path);
        PathStatusText.Text = $"已将 {env.Path} 加入用户 PATH";
    }

    private void RemovePathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not RustEnvEntry env) return;
        RemoveFromUserPath(env.Path);
        PathStatusText.Text = $"已从用户 PATH 移除 {env.Path}";
    }

    private static void SetUserPath(string dir)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(p => p.Trim().Equals(dir, StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("PATH", string.Join(';', parts.Append(dir)), EnvironmentVariableTarget.User);
    }

    private static void RemoveFromUserPath(string dir)
    {
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Trim().Equals(dir, StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), EnvironmentVariableTarget.User);
    }

    private async Task LoadToolchainsAsync()
    {
        ToolchainStatus.Text = "加载中...";
        Toolchains.Clear();
        await Task.Run(() =>
        {
            var rustupCheck = ProcessHelper.Run("rustup", "--version");
            if (string.IsNullOrEmpty(rustupCheck))
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    ToolchainStatus.Text = "未检测到 rustup，请点击「安装 rustup」后再使用";
                });
                return;
            }
            var output = ProcessHelper.Run("rustup", "toolchain list");
            var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var line in lines)
                {
                    var t = line.Trim();
                    if (string.IsNullOrEmpty(t)) continue;
                    var isDefault = t.Contains("(default)") ? "默认" : "";
                    var name = Regex.Replace(t, @"\s*\(default\)", "").Trim();
                    Toolchains.Add(new RustToolchainItem { Name = name, IsDefault = isDefault });
                }
                ToolchainStatus.Text = $"共 {Toolchains.Count} 个工具链";
            });
        });
    }

    private async void InstallRustupBtn_Click(object sender, RoutedEventArgs e)
    {
        ToolchainStatus.Text = "下载 rustup-init...";
        await Task.Run(() =>
        {
            try
            {
                var url = "https://win.rustup.rs/x86_64";
                var installer = Path.Combine(Path.GetTempPath(), "rustup-init.exe");
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var data = http.GetByteArrayAsync(url).Result;
                File.WriteAllBytes(installer, data);
                _ = DispatcherQueue.TryEnqueue(() => ToolchainStatus.Text = "正在运行 rustup-init（默认安装）...");
                ProcessHelper.RunNoCapture(installer, "-y", null, 300000);
                _ = DispatcherQueue.TryEnqueue(async () => { ToolchainStatus.Text = "rustup 安装完成"; await LoadToolchainsAsync(); });
            }
            catch (Exception ex)
            {
                _ = DispatcherQueue.TryEnqueue(() => ToolchainStatus.Text = $"安装失败：{ex.Message}");
            }
        });
    }

    private void RefreshToolchainsBtn_Click(object sender, RoutedEventArgs e) => _ = LoadToolchainsAsync();

    private async void InstallStableBtn_Click(object sender, RoutedEventArgs e)
    {
        ToolchainStatus.Text = "安装 stable...";
        await Task.Run(() => { ProcessHelper.Run("rustup", "toolchain install stable", null, 300000); });
        await LoadToolchainsAsync();
    }

    private async void InstallNightlyBtn_Click(object sender, RoutedEventArgs e)
    {
        ToolchainStatus.Text = "安装 nightly...";
        await Task.Run(() => { ProcessHelper.Run("rustup", "toolchain install nightly", null, 300000); });
        await LoadToolchainsAsync();
    }

    private async void UninstallToolchainBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RustToolchainItem item) return;
        ToolchainStatus.Text = $"卸载 {item.Name}...";
        await Task.Run(() => { ProcessHelper.Run("rustup", $"toolchain uninstall {item.Name}", null, 120000); });
        await LoadToolchainsAsync();
    }

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
        if (folder is not null) { _pkgDir = folder.Path; PkgProjectBox.Text = folder.Path; await RefreshCratesAsync(); }
    }

    private async Task RefreshCratesAsync()
    {
        if (string.IsNullOrEmpty(_pkgDir)) return;
        Crates.Clear();
        PkgStatusText.Text = "加载中...";
        await Task.Run(() =>
        {
            var output = ProcessHelper.Run("cargo", "tree --depth 1", _pkgDir, 60000);
            var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var line in lines.Skip(1))
                {
                    var m = Regex.Match(line.Trim(), @"^([\w-]+)\s+v([\d.]+)");
                    if (m.Success) Crates.Add(new RustCrateItem { Name = m.Groups[1].Value, Version = m.Groups[2].Value });
                }
                PkgStatusText.Text = $"共 {Crates.Count} 个依赖";
            });
        });
    }

    private void RefreshPackagesBtn_Click(object sender, RoutedEventArgs e) => _ = RefreshCratesAsync();

    private async void CargoBuildBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgDir)) return;
        PkgStatusText.Text = "cargo build...";
        await Task.Run(() => { ProcessHelper.Run("cargo", "build", _pkgDir, 300000); });
        PkgStatusText.Text = "build 完成";
    }

    private async void AddCrateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgDir)) return;
        var c = AddCrateBox.Text.Trim();
        if (string.IsNullOrEmpty(c)) return;
        PkgStatusText.Text = $"cargo add {c}...";
        await Task.Run(() => { ProcessHelper.Run("cargo", $"add {c}", _pkgDir, 120000); });
        AddCrateBox.Text = "";
        await RefreshCratesAsync();
    }

    private async void RemoveCrateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgDir)) return;
        if ((sender as Button)?.Tag is not RustCrateItem c) return;
        await Task.Run(() => { ProcessHelper.Run("cargo", $"remove {c.Name}", _pkgDir, 60000); });
        await RefreshCratesAsync();
    }

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
        if (folder is not null) { _newProjDir = folder.Path; NewProjDirBox.Text = folder.Path; }
    }

    private async void CreateProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = NewProjNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(_newProjDir))
        {
            NewProjStatusText.Text = "请填写项目名称和存储目录";
            return;
        }
        var projDir = Path.Combine(_newProjDir, name);
        if (Directory.Exists(projDir)) { NewProjStatusText.Text = "目录已存在"; return; }
        var isLib = NewProjTypeCombo.SelectedIndex == 1;
        NewProjStatusText.Text = "创建中...";
        await Task.Run(() =>
        {
            var arg = isLib ? "new --lib" : "new --bin";
            ProcessHelper.Run("cargo", $"{arg} \"{projDir}\"", _newProjDir, 60000);
            _ = DispatcherQueue.TryEnqueue(() => NewProjStatusText.Text = $"项目创建成功：{projDir}");
        });
    }
}
