using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// Python 环境页面：展示本机的 Python 解释器（系统 + 自定义合并）、工具链、虚拟环境，并提供包管理。
/// </summary>
public sealed partial class PythonEnvPage : Page
{
    public ObservableCollection<PythonEnvironment> Environments { get; } = new();
    public ObservableCollection<InfoItem> ToolItems { get; } = new();
    public ObservableCollection<InfoItem> CondaEnvItems { get; } = new();
    public ObservableCollection<InfoItem> PathItems { get; } = new();

    public PythonEnvPage()
    {
        InitializeComponent();
        Loaded += PythonEnvPage_Loaded;
    }

    private void PageSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        InterpreterPanel.Visibility = tag == "interpreter" ? Visibility.Visible : Visibility.Collapsed;
        ToolchainPanel.Visibility = tag == "toolchain" ? Visibility.Visible : Visibility.Collapsed;
        CondaPanel.Visibility = tag == "conda" ? Visibility.Visible : Visibility.Collapsed;
        PathPanel.Visibility = tag == "path" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PythonEnvPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadingRing.IsActive = true;
        try
        {
            var snap = await Task.Run(CollectAll);
            Apply(snap);
        }
        catch
        {
            PythonVersionText.Text = "收集失败";
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    // =====================================================================
    // 数据收集
    // =====================================================================

    private sealed class SystemPython
    {
        public string Version = "";
        public bool IsDefault;
        public string Path = "";
    }

    private sealed class EnvSnapshot
    {
        public string PythonVersion = "";
        public string PythonPath = "";
        public string PipVersion = "";
        public string PipPath = "";
        public string UvVersion = "";
        public string UvPath = "";
        public string CondaVersion = "";
        public int CondaEnvCount;
        public List<SystemPython> SystemPythons = new();
        public List<InfoItem> Tools = new();
        public List<InfoItem> CondaEnvs = new();
        public List<InfoItem> Paths = new();
    }

    private static EnvSnapshot CollectAll()
    {
        var snap = new EnvSnapshot();

        // ---- 已安装的 Python（py launcher） ----
        var pyListOutput = RunCommand("py", "-0p");
        if (!string.IsNullOrEmpty(pyListOutput))
        {
            foreach (var rawLine in pyListOutput.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                var m = Regex.Match(line, @"-V:([\d.]+)\s*(\*)?\s*(.*)$");
                if (!m.Success) continue;
                snap.SystemPythons.Add(new SystemPython
                {
                    Version = m.Groups[1].Value,
                    IsDefault = m.Groups[2].Value == "*",
                    Path = m.Groups[3].Value.Trim(),
                });
            }
        }

        // ---- python --version / 解释器路径 ----
        snap.PythonVersion = ExtractVersion(RunCommand("python", "--version"));
        var exePath = RunCommand("python", "-c \"import sys;print(sys.executable)\"");
        if (string.IsNullOrWhiteSpace(exePath) && snap.SystemPythons.Count > 0)
            exePath = snap.SystemPythons[0].Path;
        snap.PythonPath = exePath?.Trim() ?? "";

        // ---- pip ----
        var pipOutput = RunCommand("pip", "--version");
        if (!string.IsNullOrEmpty(pipOutput))
        {
            var parts = pipOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            snap.PipVersion = parts.Length >= 2 ? parts[1] : pipOutput;
            var fromM = Regex.Match(pipOutput, @"from\s+(\S+)");
            snap.PipPath = fromM.Success ? fromM.Groups[1].Value : "";
        }

        // ---- uv ----
        var uvOutput = RunCommand("uv", "--version");
        if (!string.IsNullOrEmpty(uvOutput))
        {
            var parts = uvOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            snap.UvVersion = parts.Length >= 2 ? parts[1] : uvOutput;
        }
        snap.UvPath = FirstLine(RunCommand("where", "uv"));

        // ---- conda ----
        var condaOutput = RunCommand("conda", "--version");
        if (!string.IsNullOrEmpty(condaOutput))
        {
            snap.CondaVersion = condaOutput;
        }
        var condaEnvOutput = RunCommand("conda", "env list");
        if (!string.IsNullOrEmpty(condaEnvOutput))
        {
            foreach (var rawLine in condaEnvOutput.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = Regex.Match(line, @"^(\S+)\s+(\*)?\s*(.*)$");
                if (!m.Success) continue;
                var envName = m.Groups[1].Value;
                var isActive = m.Groups[2].Value == "*";
                var envPath = m.Groups[3].Value.Trim();
                snap.CondaEnvs.Add(new InfoItem
                {
                    Label = envName + (isActive ? "（当前）" : ""),
                    Value = string.IsNullOrEmpty(envPath) ? "—" : envPath,
                });
            }
            snap.CondaEnvCount = snap.CondaEnvs.Count;
        }

        // ---- 工具链版本汇总 ----
        snap.Tools.Add(new InfoItem
        {
            Label = "python",
            Value = string.IsNullOrEmpty(snap.PythonVersion) ? "未检测到（不在 PATH）" : snap.PythonVersion,
            HasAction = true,
            ActionTag = "python",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "pip",
            Value = string.IsNullOrEmpty(snap.PipVersion) ? "未检测到" : $"{snap.PipVersion}  (from {snap.PipPath})",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "uv",
            Value = string.IsNullOrEmpty(snap.UvVersion) ? "未检测到" : $"{snap.UvVersion}  ({snap.UvPath})",
            HasAction = true,
            ActionTag = "uv",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "conda",
            Value = string.IsNullOrEmpty(snap.CondaVersion) ? "未检测到" : snap.CondaVersion,
            HasAction = true,
            ActionTag = "conda",
        });

        // ---- PATH 中的 python ----
        var whereOutput = RunCommand("where", "python");
        if (!string.IsNullOrEmpty(whereOutput))
        {
            int i = 1;
            foreach (var rawLine in whereOutput.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                snap.Paths.Add(new InfoItem { Label = $"入口 {i++}", Value = line });
            }
        }
        if (snap.Paths.Count == 0)
        {
            snap.Paths.Add(new InfoItem { Label = "入口 1", Value = "未在 PATH 中找到 python" });
        }

        return snap;
    }

    private void Apply(EnvSnapshot snap)
    {
        PythonVersionText.Text = string.IsNullOrEmpty(snap.PythonVersion) ? "未检测到" : snap.PythonVersion;
        PythonPathText.Text = Shorten(snap.PythonPath, 40);

        PipVersionText.Text = string.IsNullOrEmpty(snap.PipVersion) ? "未检测到" : snap.PipVersion;
        PipPathText.Text = Shorten(snap.PipPath, 40);

        UvVersionText.Text = string.IsNullOrEmpty(snap.UvVersion) ? "未检测到" : snap.UvVersion;
        UvPathText.Text = Shorten(snap.UvPath, 40);

        CondaVersionText.Text = string.IsNullOrEmpty(snap.CondaVersion) ? "未检测到" : snap.CondaVersion;
        CondaEnvText.Text = snap.CondaEnvCount > 0 ? $"{snap.CondaEnvCount} 个环境" : "";

        foreach (var item in snap.Tools) ToolItems.Add(item);
        foreach (var item in snap.CondaEnvs) CondaEnvItems.Add(item);
        if (snap.CondaEnvs.Count == 0)
            CondaEnvItems.Add(new InfoItem { Label = "conda", Value = "未检测到 conda（未安装或不在 PATH）" });
        foreach (var item in snap.Paths) PathItems.Add(item);

        BuildEnvironmentList(snap);
        PathEnvCombo.ItemsSource = Environments;
    }

    // =====================================================================
    // 环境列表（系统 + 自定义 合并）
    // =====================================================================

    private void BuildEnvironmentList(EnvSnapshot snap)
    {
        Environments.Clear();

        // 1. 系统检测到的（py -0p）
        foreach (var sp in snap.SystemPythons)
        {
            if (string.IsNullOrEmpty(sp.Path) || !File.Exists(sp.Path)) continue;
            Environments.Add(new PythonEnvironment
            {
                DisplayName = $"Python {sp.Version}",
                Path = sp.Path,
                IsDefault = sp.IsDefault,
                Version = sp.Version,
            });
        }

        // 2. 自定义添加的（持久化），与系统检测去重
        foreach (var entry in PythonEnvStorage.Load())
        {
            if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) continue;
            if (Environments.Any(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;

            var env = new PythonEnvironment
            {
                DisplayName = string.IsNullOrEmpty(entry.Name) ? Path.GetFileNameWithoutExtension(entry.Path) : entry.Name!,
                Path = entry.Path,
                IsCustom = true,
                AddedAt = entry.AddedAt,
            };
            Environments.Add(env);
            ProbeVersionAsync(env);
        }

        // 3. 官方安装器默认目录（本工具托管 / 用户级 / 系统级）
        var officialRoots = new[]
        {
            ToolPaths.InstallPython,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Python"),
        };
        foreach (var root in officialRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var py = Path.Combine(dir, "python.exe");
                if (!File.Exists(py)) continue;
                if (Environments.Any(e => string.Equals(e.Path, py, StringComparison.OrdinalIgnoreCase))) continue;

                var name = Path.GetFileName(dir);
                var m = Regex.Match(name, @"^Python(3)(\d+)$");
                var ver = m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : name;
                Environments.Add(new PythonEnvironment
                {
                    DisplayName = $"Python {ver}",
                    Path = py,
                    Version = ver,
                });
            }
        }
    }

    private void SaveCustomEnvironments()
    {
        PythonEnvStorage.Save(Environments
            .Where(e => e.IsCustom)
            .Select(e => new CustomPythonEntry
            {
                Path = e.Path,
                Name = e.DisplayName,
                AddedAt = e.AddedAt ?? DateTime.Now,
            }));
    }

    private PythonEnvironment? _manualBase;

    private void NewEnvBtn_Click(object sender, RoutedEventArgs e)
    {
        // 填充基解释器下拉
        _manualBase = null;
        NewEnvBaseCombo.ItemsSource = Environments.ToList();
        NewEnvBaseCombo.SelectedItem = Environments.FirstOrDefault(x => x.IsDefault) ?? Environments.FirstOrDefault();
        NewEnvDirBox.Text = "";
        NewEnvNameBox.Text = ".seraphineVenv";
        NewEnvHintText.Visibility = Visibility.Collapsed;
        NewEnvProgressPanel.Visibility = Visibility.Collapsed;
        NewEnvDialog.IsPrimaryButtonEnabled = true;
        NewEnvDialog.XamlRoot = XamlRoot;
        _ = NewEnvDialog.ShowAsync();
    }

    private async void NewEnvBrowseBase_Click(object sender, RoutedEventArgs e)
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
        if (file is null || string.IsNullOrEmpty(file.Path)) return;

        _manualBase = new PythonEnvironment
        {
            DisplayName = $"自定义：{Path.GetFileNameWithoutExtension(file.Path)}",
            Path = file.Path,
        };

        var list = Environments.ToList();
        list.Add(_manualBase);
        NewEnvBaseCombo.ItemsSource = list;
        NewEnvBaseCombo.SelectedItem = _manualBase;
    }

    private async void NewEnvBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !string.IsNullOrEmpty(folder.Path))
            NewEnvDirBox.Text = folder.Path;
    }

    private async void NewEnvDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (NewEnvBaseCombo.SelectedItem is not PythonEnvironment baseEnv)
        {
            NewEnvHintText.Text = "请先选择基解释器";
            NewEnvHintText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        var name = NewEnvNameBox.Text.Trim();
        if (name.Length == 0) name = ".seraphineVenv";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            NewEnvHintText.Text = "环境名称包含非法字符";
            NewEnvHintText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        var parentDir = NewEnvDirBox.Text.Trim();
        if (parentDir.Length == 0)
        {
            NewEnvHintText.Text = "请填写父目录";
            NewEnvHintText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        // 防止重复提交，先取消默认关闭，由我们控制
        args.Cancel = true;
        var deferral = args.GetDeferral();
        NewEnvHintText.Visibility = Visibility.Collapsed;
        NewEnvProgressPanel.Visibility = Visibility.Visible;
        NewEnvStatusText.Text = $"正在创建虚拟环境（{baseEnv.DisplayName}）…";
        NewEnvDialog.IsPrimaryButtonEnabled = false;
        try
        {
            // 用环境名称新建子文件夹，venv 创建到该文件夹
            var venvDir = Path.Combine(Path.GetFullPath(parentDir), name);

            // 1. 执行 python -m venv
            var psi = new ProcessStartInfo
            {
                FileName = baseEnv.Path,
                Arguments = $"-m venv \"{venvDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var proc = Process.Start(psi))
            {
                if (proc is null)
                {
                    NewEnvHintText.Text = "创建失败：无法启动解释器";
                    NewEnvHintText.Visibility = Visibility.Visible;
                    return;
                }
                var err = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0)
                {
                    NewEnvHintText.Text = $"创建失败（退出码 {proc.ExitCode}）：{FirstLine(err)}";
                    NewEnvHintText.Visibility = Visibility.Visible;
                    return;
                }
            }

            // 2. 校验 venv 的解释器
            var pythonExe = Path.Combine(venvDir, "Scripts", "python.exe");
            if (!File.Exists(pythonExe))
            {
                NewEnvHintText.Text = "创建完成但未找到 Scripts\\python.exe";
                NewEnvHintText.Visibility = Visibility.Visible;
                return;
            }

            // 3. 加入环境列表
            if (Environments.Any(i => string.Equals(i.Path, pythonExe, StringComparison.OrdinalIgnoreCase)))
            {
                NewEnvHintText.Text = "该环境已存在于列表中";
                NewEnvHintText.Visibility = Visibility.Visible;
                return;
            }

            var env = new PythonEnvironment
            {
                DisplayName = name,
                Path = pythonExe,
                IsCustom = true,
                IsVenv = true,
                AddedAt = DateTime.Now,
            };
            Environments.Add(env);
            SaveCustomEnvironments();
            ProbeVersionAsync(env);

            NewEnvDialog.Hide();
        }
        catch (Exception ex)
        {
            NewEnvHintText.Text = $"创建失败：{ex.Message}";
            NewEnvHintText.Visibility = Visibility.Visible;
        }
        finally
        {
            NewEnvProgressPanel.Visibility = Visibility.Collapsed;
            NewEnvDialog.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
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

        var env = new PythonEnvironment
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
        if (sender is Button btn && btn.Tag is PythonEnvironment env && env.IsCustom)
        {
            Environments.Remove(env);
            SaveCustomEnvironments();
        }
    }

    private void ManagePackagesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PythonEnvironment env)
        {
            Frame.Navigate(typeof(PackageManagerPage), env);
        }
    }

    private void ToolchainActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            switch (tag)
            {
                case "python":
                    Frame.Navigate(typeof(DownloadPythonPage));
                    break;
                case "uv":
                    Frame.Navigate(typeof(DownloadUvPage));
                    break;
                case "conda":
                    Frame.Navigate(typeof(DownloadCondaPage));
                    break;
            }
        }
    }

    private void ProbeVersionAsync(PythonEnvironment env)
    {
        _ = Task.Run(() =>
        {
            var version = ExtractVersion(RunCommand(env.Path, "--version"));
            DispatcherQueue.TryEnqueue(() =>
            {
                env.Version = string.IsNullOrEmpty(version) ? "无法检测版本" : $"Python {version}";
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
    // 一键设置环境变量
    // =====================================================================

    private void SetPathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not PythonEnvironment env)
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
            PythonEnvPage_Loaded(sender, e);
        }
        catch (Exception ex)
        {
            PathStatusText.Text = $"设置失败：{ex.Message}";
        }
    }

    private void RemovePathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not PythonEnvironment env)
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
            PythonEnvPage_Loaded(sender, e);
        }
        catch (Exception ex)
        {
            PathStatusText.Text = $"移除失败：{ex.Message}";
        }
    }
}
