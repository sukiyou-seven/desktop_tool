using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

/// <summary>
/// PHP 环境页面：展示本机的 PHP 运行时（系统 + 托管 + 自定义合并）、工具链、配置与 PATH。
/// </summary>
public sealed partial class PhpEnvPage : Page
{
    public ObservableCollection<PhpEnvironment> Environments { get; } = new();
    public ObservableCollection<InfoItem> ToolItems { get; } = new();
    public ObservableCollection<InfoItem> ConfigItems { get; } = new();
    public ObservableCollection<InfoItem> PathItems { get; } = new();
    public ObservableCollection<PhpDevServer> DevServers { get; } = new();

    /// <summary>全局运行中的 PHP 开发服务器进程，应用退出时统一清理。</summary>
    private static readonly List<Process> _allDevServerProcesses = new();

    public PhpEnvPage()
    {
        InitializeComponent();
        Loaded += PhpEnvPage_Loaded;
    }

    private void PageSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        RuntimesPanel.Visibility = tag == "runtimes" ? Visibility.Visible : Visibility.Collapsed;
        ToolchainPanel.Visibility = tag == "toolchain" ? Visibility.Visible : Visibility.Collapsed;
        ConfigPanel.Visibility = tag == "config" ? Visibility.Visible : Visibility.Collapsed;
        PathPanel.Visibility = tag == "path" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PhpEnvPage_Loaded(object sender, RoutedEventArgs e)
    {
        InitDevServerIps();
        LoadingRing.IsActive = true;
        try
        {
            var snap = await Task.Run(CollectAll);
            Apply(snap);
        }
        catch
        {
            PhpVersionText.Text = "收集失败";
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    // =====================================================================
    // 数据收集
    // =====================================================================

    private sealed class EnvSnapshot
    {
        public string PhpVersion = "";
        public string PhpPath = "";
        public string ComposerVersion = "";
        public string ComposerPath = "";
        public string PeclVersion = "";
        public string PeclPath = "";
        public string IniPath = "";
        public string ExtensionDir = "";
        public List<InfoItem> Tools = new();
        public List<InfoItem> Configs = new();
        public List<InfoItem> Paths = new();
        public List<PhpEnvironment> SystemPhps = new();
    }

    private static string ToolPhpRoot => ToolPaths.InstallPhp;

    private static EnvSnapshot CollectAll()
    {
        var snap = new EnvSnapshot();

        // ---- php -v / 默认解释器 ----
        var phpPath = FirstLine(RunCommand("where", "php"));
        snap.PhpPath = phpPath;
        snap.PhpVersion = ExtractVersion(RunCommand("php", "-v"));

        // ---- composer ----
        snap.ComposerVersion = FirstLine(RunCommand("composer", "--version"));
        snap.ComposerPath = FirstLine(RunCommand("where", "composer"));

        // ---- pecl ----
        snap.PeclPath = FirstLine(RunCommand("where", "pecl"));
        snap.PeclVersion = FirstLine(RunCommand("pecl", "version"));

        // ---- php.ini 信息 ----
        var iniOutput = RunCommand("php", "--ini");
        if (!string.IsNullOrEmpty(iniOutput))
        {
            foreach (var rawLine in iniOutput.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Loaded Configuration File"))
                {
                    var idx = line.IndexOf(' ');
                    if (idx > 0) snap.IniPath = line[(idx + 1)..].Trim();
                }
            }
        }
        var phpInfo = RunCommand("php", "-i");
        if (!string.IsNullOrEmpty(phpInfo))
        {
            foreach (var rawLine in phpInfo.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("extension_dir =>"))
                {
                    var idx = line.IndexOf("=>");
                    if (idx > 0)
                        snap.ExtensionDir = line[(idx + 2)..].Trim().Split("=>")[0].Trim();
                    break;
                }
            }
        }

        // ---- 工具链版本汇总 ----
        snap.Tools.Add(new InfoItem
        {
            Label = "php",
            Value = string.IsNullOrEmpty(snap.PhpVersion) ? "未检测到（不在 PATH）" : snap.PhpVersion,
            HasAction = true,
            ActionTag = "php",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "composer",
            Value = string.IsNullOrEmpty(snap.ComposerVersion) ? "未检测到" : $"{snap.ComposerVersion}  ({snap.ComposerPath})",
            HasAction = true,
            ActionTag = "composer",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "pecl",
            Value = string.IsNullOrEmpty(snap.PeclVersion) ? "未检测到" : $"{snap.PeclVersion}  ({snap.PeclPath})",
        });

        // ---- 配置 ----
        snap.Configs.Add(new InfoItem
        {
            Label = "php.ini",
            Value = string.IsNullOrEmpty(snap.IniPath) ? "未加载（未检测到）" : snap.IniPath,
        });
        snap.Configs.Add(new InfoItem
        {
            Label = "扩展目录",
            Value = string.IsNullOrEmpty(snap.ExtensionDir) ? "—" : snap.ExtensionDir,
        });

        // ---- PATH 中的 php / composer ----
        int i = 1;
        var wherePhp = RunCommand("where", "php");
        if (!string.IsNullOrEmpty(wherePhp))
        {
            bool firstSystem = true;
            foreach (var rawLine in wherePhp.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                snap.Paths.Add(new InfoItem { Label = $"php 入口 {i++}", Value = line });
                snap.SystemPhps.Add(new PhpEnvironment
                {
                    DisplayName = "系统 PHP",
                    Path = line,
                    IsDefault = firstSystem,
                });
                firstSystem = false;
            }
        }
        i = 1;
        var whereComposer = RunCommand("where", "composer");
        if (!string.IsNullOrEmpty(whereComposer))
        {
            foreach (var rawLine in whereComposer.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                snap.Paths.Add(new InfoItem { Label = $"composer 入口 {i++}", Value = line });
            }
        }
        if (snap.Paths.Count == 0)
            snap.Paths.Add(new InfoItem { Label = "入口", Value = "PATH 中未找到 php / composer" });

        return snap;
    }

    private void Apply(EnvSnapshot snap)
    {
        PhpVersionText.Text = string.IsNullOrEmpty(snap.PhpVersion) ? "未检测到" : snap.PhpVersion;
        PhpPathText.Text = Shorten(snap.PhpPath, 40);

        ComposerVersionText.Text = string.IsNullOrEmpty(snap.ComposerVersion) ? "未检测到" : snap.ComposerVersion;
        ComposerPathText.Text = Shorten(snap.ComposerPath, 40);

        PeclVersionText.Text = string.IsNullOrEmpty(snap.PeclVersion) ? "未检测到" : snap.PeclVersion;
        PeclPathText.Text = Shorten(snap.PeclPath, 40);

        IniVersionText.Text = string.IsNullOrEmpty(snap.IniPath) ? "未加载" : "已加载";
        IniPathText.Text = Shorten(snap.IniPath, 40);

        foreach (var item in snap.Tools) ToolItems.Add(item);
        foreach (var item in snap.Configs) ConfigItems.Add(item);
        foreach (var item in snap.Paths) PathItems.Add(item);

        BuildEnvironmentList(snap);
        PathEnvCombo.ItemsSource = Environments;
    }

    // =====================================================================
    // 运行时列表（系统 + 托管 + 自定义 合并）
    // =====================================================================

    private void BuildEnvironmentList(EnvSnapshot snap)
    {
        Environments.Clear();

        // 1. 系统检测（where php，后台已采集），第一条为默认
        foreach (var env in snap.SystemPhps)
        {
            if (string.IsNullOrEmpty(env.Path)) continue;
            Environments.Add(env);
            ProbeVersionAsync(env);
        }

        // 2. 工具托管目录（下载安装的 PHP）
        if (Directory.Exists(ToolPhpRoot))
        {
            foreach (var dir in Directory.GetDirectories(ToolPhpRoot))
            {
                var exe = Path.Combine(dir, "php.exe");
                if (!File.Exists(exe)) continue;
                if (Environments.Any(e => string.Equals(e.Path, exe, StringComparison.OrdinalIgnoreCase))) continue;
                var env = new PhpEnvironment
                {
                    DisplayName = Path.GetFileName(dir),
                    Path = exe,
                };
                Environments.Add(env);
                ProbeVersionAsync(env);
            }
        }

        // 3. 自定义（持久化），去重
        foreach (var entry in PhpEnvStorage.Load())
        {
            if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) continue;
            if (Environments.Any(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;

            var env = new PhpEnvironment
            {
                DisplayName = string.IsNullOrEmpty(entry.Name) ? Path.GetFileNameWithoutExtension(entry.Path) : entry.Name!,
                Path = entry.Path,
                IsCustom = true,
                AddedAt = entry.AddedAt,
            };
            Environments.Add(env);
            ProbeVersionAsync(env);
        }
    }

    private void SaveCustomEnvironments()
    {
        PhpEnvStorage.Save(Environments
            .Where(e => e.IsCustom)
            .Select(e => new CustomPhpEntry
            {
                Path = e.Path,
                Name = e.DisplayName,
                AddedAt = e.AddedAt ?? DateTime.Now,
            }));
    }

    private async void AddCustomBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var path = file.Path;
        if (string.IsNullOrEmpty(path)) return;
        if (Environments.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        var env = new PhpEnvironment
        {
            DisplayName = Path.GetFileNameWithoutExtension(path),
            Path = path,
            IsCustom = true,
            AddedAt = DateTime.Now,
        };
        Environments.Add(env);
        SaveCustomEnvironments();
        ProbeVersionAsync(env);
    }

    private void DeleteEnvBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PhpEnvironment env && env.IsCustom)
        {
            Environments.Remove(env);
            SaveCustomEnvironments();
        }
    }

    private void ToolchainActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            switch (tag)
            {
                case "php":
                    Frame.Navigate(typeof(DownloadPhpPage));
                    break;
                case "composer":
                    Frame.Navigate(typeof(DownloadComposerPage));
                    break;
            }
        }
    }

    private void ProbeVersionAsync(PhpEnvironment env)
    {
        _ = Task.Run(() =>
        {
            var version = ExtractVersion(RunCommand(env.Path, "-v"));
            DispatcherQueue.TryEnqueue(() =>
            {
                env.Version = string.IsNullOrEmpty(version) ? "无法检测版本" : $"PHP {version}";
            });
        });
    }

    // =====================================================================
    // 辅助函数
    // =====================================================================

    private static string? RunCommand(string fileName, string args) => ProcessHelper.Run(fileName, args);

    private static string ExtractVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var text = output.Trim();
        var m = Regex.Match(text, @"\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : text;
    }

    private static string FirstLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        return output.Split('\n')[0].Trim();
    }

    private static string Shorten(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }

    // =====================================================================
    // PHP 开发服务器（多实例）
    // =====================================================================

    /// <summary>一个运行中的 PHP 开发服务器实例。</summary>
    public sealed class PhpDevServer
    {
        public string Url { get; set; } = "";
        public string RootText { get; set; } = "";
        public Process? Process { get; set; }
    }

    /// <summary>应用退出时结束所有 PHP 开发服务器进程。</summary>
    public static void KillAllDevServers()
    {
        foreach (var p in _allDevServerProcesses)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        _allDevServerProcesses.Clear();
    }

    private void InitDevServerIps()
    {
        DevServerIpCombo.Items.Clear();
        DevServerIpCombo.Items.Add("127.0.0.1");
        DevServerIpCombo.Items.Add("0.0.0.0");
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = ua.Address.ToString();
                        if (!DevServerIpCombo.Items.Contains(ip))
                            DevServerIpCombo.Items.Add(ip);
                    }
                }
            }
        }
        catch { }
        DevServerIpCombo.SelectedIndex = 0;
    }

    private async void DevServerPickRootBtn_Click(object sender, RoutedEventArgs e)
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
            DevServerRootBox.Text = folder.Path;
    }

    private void DevServerStartBtn_Click(object sender, RoutedEventArgs e)
    {
        var ip = DevServerIpCombo.SelectedItem as string ?? "127.0.0.1";
        if (!int.TryParse(DevServerPortBox.Text, out var port) || port < 1 || port > 65535)
        {
            ShowDevServerToast("端口无效");
            return;
        }
        // 端口冲突检测
        if (DevServers.Any(s => s.Url.EndsWith($":{port}")))
        {
            ShowDevServerToast($"端口 {port} 已被占用");
            return;
        }
        var phpPath = FirstLine(RunCommand("where", "php"));
        if (string.IsNullOrEmpty(phpPath) || !File.Exists(phpPath))
        {
            ShowDevServerToast("未找到 php.exe，请先安装 PHP 或加入 PATH");
            return;
        }

        var root = DevServerRootBox.Text.Trim();
        var args = $"-S {ip}:{port}";
        if (!string.IsNullOrEmpty(root))
            args += $" -t \"{root}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = phpPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc is null)
            {
                ShowDevServerToast("启动失败");
                return;
            }
            var url = ip == "0.0.0.0" ? $"http://127.0.0.1:{port}" : $"http://{ip}:{port}";
            var server = new PhpDevServer
            {
                Url = url,
                RootText = string.IsNullOrEmpty(root) ? "(PHP 所在目录)" : root,
                Process = proc,
            };
            DevServers.Add(server);
            _allDevServerProcesses.Add(proc);
        }
        catch (Exception ex)
        {
            ShowDevServerToast($"启动失败：{ex.Message}");
        }
    }

    private void DevServerStopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PhpDevServer server)
        {
            StopServer(server);
        }
    }

    private void DevServerOpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PhpDevServer server && !string.IsNullOrEmpty(server.Url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(server.Url) { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void StopServer(PhpDevServer server)
    {
        try
        {
            if (server.Process is not null && !server.Process.HasExited)
                server.Process.Kill(entireProcessTree: true);
        }
        catch { }
        if (server.Process is not null)
        {
            _allDevServerProcesses.Remove(server.Process);
            try { server.Process.Dispose(); } catch { }
        }
        DevServers.Remove(server);
    }

    private async void ShowDevServerToast(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "PHP 开发服务器",
            Content = msg,
            CloseButtonText = "确定",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // =====================================================================
    // 一键设置环境变量
    // =====================================================================

    private void SetPathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not PhpEnvironment env)
        {
            PathStatusText.Text = "请先选择一个环境";
            return;
        }
        var dir = System.IO.Path.GetDirectoryName(env.Path);
        if (string.IsNullOrEmpty(dir))
        {
            PathStatusText.Text = "路径无效";
            return;
        }
        try
        {
            if (EnvVarHelper.AddToUserPath(dir))
                PathStatusText.Text = $"已添加到用户 PATH：{dir}（新开的 cmd 生效）";
            else
                PathStatusText.Text = $"已在用户 PATH 中：{dir}";
            PhpEnvPage_Loaded(sender, e);
        }
        catch (Exception ex)
        {
            PathStatusText.Text = $"设置失败：{ex.Message}";
        }
    }

    private void RemovePathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not PhpEnvironment env)
        {
            PathStatusText.Text = "请先选择一个环境";
            return;
        }
        var dir = System.IO.Path.GetDirectoryName(env.Path);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (EnvVarHelper.RemoveFromUserPath(dir))
                PathStatusText.Text = $"已从用户 PATH 移除：{dir}";
            else
                PathStatusText.Text = $"不在用户 PATH 中：{dir}";
            PhpEnvPage_Loaded(sender, e);
        }
        catch (Exception ex)
        {
            PathStatusText.Text = $"移除失败：{ex.Message}";
        }
    }
}
