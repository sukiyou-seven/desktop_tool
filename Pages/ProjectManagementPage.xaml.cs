using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopTool.Pages;

/// <summary>
/// 工程管理：创建 / 添加已有目录 / 列表 / 打开目录 / 删除。
/// 每张工程卡片内嵌 npm 执行：选目录（默认工程路径）→ 下拉命令（自动获取）→ 执行。
/// </summary>
public sealed partial class ProjectManagementPage : Page
{
    public ObservableCollection<ProjectCardVm> Projects { get; } = new();

    public ProjectManagementPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        Projects.Clear();
        var links = ServerStore.Load(); // 复用「服务器管理」的服务器信息
        foreach (var p in ProjectStore.Load().OrderByDescending(p => p.CreatedAt))
        {
            var vm = new ProjectCardVm(p);
            foreach (var l in links) vm.ServerLinks.Add(l);
            vm.RestoreSelectedServer(); // 恢复该工程上次选择的服务器
            RefreshNpmCommands(vm); // 自动加载命令并恢复上次选中，无需手动选目录
            Projects.Add(vm);
        }
        EmptyState.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        ProjectStore.Save(Projects.Select(vm => vm.Project));
        EmptyState.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // 添加已有工程（选择目录加入列表，不创建新文件）
    // =====================================================================

    private async void AddProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = App.MainWindow is null
                ? IntPtr.Zero
                : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

            var path = FolderDialog.PickFolder(hwnd);
            if (string.IsNullOrEmpty(path)) return;

            if (Projects.Any(vm => string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                await ShowToast("该目录已在工程列表中");
                return;
            }

            var (type, framework) = DetectProjectType(path);
            var project = new Project
            {
                Name = string.IsNullOrWhiteSpace(Path.GetFileName(path.TrimEnd('\\', '/')))
                    ? path
                    : Path.GetFileName(path.TrimEnd('\\', '/')),
                Type = type,
                Framework = framework,
                Path = path,
            };
            Projects.Insert(0, new ProjectCardVm(project));
            Save();
            await ShowToast($"已添加工程：{project.Name}（{type}）");
        }
        catch (Exception ex)
        {
            await ShowToast("添加失败：" + ex.Message);
        }
    }

    /// <summary>根据目录内容识别工程类型与框架。</summary>
    private static (string Type, string Framework) DetectProjectType(string path)
    {
        var pj = Path.Combine(path, "package.json");
        if (File.Exists(pj))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                var root = doc.RootElement;
                var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var p in d.EnumerateObject()) deps.Add(p.Name);
                if (root.TryGetProperty("devDependencies", out var dd) && dd.ValueKind == JsonValueKind.Object)
                    foreach (var p in dd.EnumerateObject()) deps.Add(p.Name);

                string framework;
                if (deps.Contains("uni-app") || deps.Contains("@dcloudio/vite-plugin-uni")) framework = "uni-app";
                else if (deps.Contains("vite")) framework = "vite";
                else if (deps.Contains("nuxt")) framework = "nuxt";
                else if (deps.Contains("next")) framework = "next";
                else if (deps.Contains("vue")) framework = "vue";
                else if (deps.Contains("react")) framework = "react";
                else if (deps.Contains("angular")) framework = "angular";
                else if (deps.Contains("svelte")) framework = "svelte";
                else framework = "npm";
                return ("Node", framework);
            }
            catch
            {
                return ("Node", "npm");
            }
        }
        if (File.Exists(Path.Combine(path, "composer.json"))) return ("PHP", "composer");
        if (File.Exists(Path.Combine(path, "requirements.txt")) || File.Exists(Path.Combine(path, "pyproject.toml")))
            return ("Python", "pip");
        return ("自定义", "");
    }

    // =====================================================================
    // 卡片内 npm 执行（每张卡片独立状态）
    // =====================================================================

    /// <summary>卡片「选目录」：重新指定该工程 npm 的工作目录，并自动刷新命令下拉。</summary>
    private async void CardPickDirBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProjectCardVm vm) return;

        var hwnd = App.MainWindow is null
            ? IntPtr.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

        // 对话框直接定位到当前工作目录（工程路径），方便确认或微调
        var path = FolderDialog.PickFolder(hwnd, vm.NpmDir);
        if (string.IsNullOrEmpty(path)) return;

        vm.NpmDir = path;
        vm.Project.NpmDir = path; // 记住该工程的工作目录，下次打开自动恢复
        Save();
        RefreshNpmCommands(vm);
    }

    /// <summary>自动获取 npm 命令填充下拉：常用命令 + 该目录 package.json 的 scripts。</summary>
    private static void RefreshNpmCommands(ProjectCardVm vm)
    {
        vm.NpmCommands.Clear();
        foreach (var c in new[] { "npm install", "npm run dev", "npm run build", "npm start", "npm test" })
            vm.NpmCommands.Add(c);

        var dir = vm.NpmDir.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            vm.NpmStatus = "目录不存在或未选择，请点「选目录」";
            return;
        }
        var pj = Path.Combine(dir, "package.json");
        if (!File.Exists(pj))
        {
            vm.NpmStatus = "该目录没有 package.json，仅提供常用命令";
            vm.SelectedNpmCommand = vm.NpmCommands.Count > 0 ? vm.NpmCommands[0] : "";
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pj));
            var root = doc.RootElement;
            var count = 0;
            if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in scripts.EnumerateObject())
                {
                    vm.NpmCommands.Add("npm run " + p.Name);
                    count++;
                }
            }
            vm.NpmStatus = count > 0
                ? $"已自动获取 {count} 个脚本：{dir}"
                : "package.json 没有 scripts，仅提供常用命令";
            // 恢复上次执行的命令；若脚本已变则选中第一项
            var remember = vm.Project.NpmCommand ?? "";
            vm.SelectedNpmCommand = vm.NpmCommands.Contains(remember) ? remember : vm.NpmCommands[0];
        }
        catch (Exception ex)
        {
            vm.NpmStatus = "解析 package.json 失败：" + ex.Message;
        }
    }

    private async void CardRunNpmBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectCardVm vm)
            await RunNpmAsync(vm);
    }

    private async Task RunNpmAsync(ProjectCardVm vm)
    {
        var dir = vm.NpmDir.Trim();
        var cmd = vm.SelectedNpmCommand?.Trim() ?? "";
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            AppendLog(vm, "错误：请先选择有效的工作目录");
            return;
        }
        if (string.IsNullOrEmpty(cmd))
        {
            AppendLog(vm, "请选择要执行的 npm 命令");
            return;
        }
        if (vm.Busy)
        {
            AppendLog(vm, "已有命令在运行，请等待完成");
            return;
        }

        vm.Project.NpmCommand = cmd; // 记住该工程上次执行的命令，下次打开恢复选中
        Save();

        vm.Busy = true;
        vm.NpmStatus = "运行中...";
        AppendLog(vm, "$ " + cmd);
        AppendLog(vm, "# 工作目录：" + dir);

        try
        {
            // 合并 用户+系统 PATH：应用启动后安装的 node/npm 也能解析到
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var combined = string.Join(';', userPath, machinePath).Trim(';');

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c \"" + cmd + "\"",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(combined))
                psi.EnvironmentVariables["PATH"] = combined;

            var proc = Process.Start(psi);
            if (proc is null)
            {
                AppendLog(vm, "错误：无法启动 cmd.exe");
                return;
            }
            proc.OutputDataReceived += (_, ev) =>
            {
                if (ev.Data is not null) _ = DispatcherQueue.TryEnqueue(() => AppendLog(vm, ev.Data));
            };
            proc.ErrorDataReceived += (_, ev) =>
            {
                if (ev.Data is not null) _ = DispatcherQueue.TryEnqueue(() => AppendLog(vm, ev.Data));
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync();
            vm.NpmStatus = $"已结束，退出码 {proc.ExitCode}（0 = 成功）";
            AppendLog(vm, $"# 命令结束，退出码 {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            AppendLog(vm, "执行失败：" + ex.Message);
            vm.NpmStatus = "执行失败";
        }
        finally
        {
            vm.Busy = false;
        }
    }

    private static void AppendLog(ProjectCardVm vm, string line)
        => vm.Output += line + Environment.NewLine;

    /// <summary>输出框内容变化时自动滚动到底部。</summary>
    private void CardOutputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.SelectionStart = tb.Text.Length;
            tb.SelectionLength = 0;
        }
    }

    /// <summary>下拉选择命令时立即缓存（不点执行也记住）。</summary>
    private void CardNpmCommandChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectCardVm vm && !string.IsNullOrEmpty(vm.SelectedNpmCommand))
        {
            vm.Project.NpmCommand = vm.SelectedNpmCommand;
            Save();
        }
    }

    /// <summary>选择服务器时立即缓存。</summary>
    private void CardServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectCardVm vm)
        {
            vm.Project.ServerLinkId = vm.SelectedServerLink?.Id ?? "";
            Save();
        }
    }

    /// <summary>卡片「选择本地目录」。</summary>
    private async void CardPickLocalDirBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProjectCardVm vm) return;
        var hwnd = App.MainWindow is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var path = FolderDialog.PickFolder(hwnd, vm.LocalUploadPath);
        if (!string.IsNullOrEmpty(path))
        {
            vm.LocalUploadPath = path;
            vm.Project.UploadLocalPath = path;
            Save();
        }
    }

    /// <summary>卡片「选择本地文件」。</summary>
    private async void CardPickLocalFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProjectCardVm vm) return;
        var hwnd = App.MainWindow is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var path = FolderDialog.PickFile(hwnd, vm.LocalUploadPath);
        if (!string.IsNullOrEmpty(path))
        {
            vm.LocalUploadPath = path;
            vm.Project.UploadLocalPath = path;
            Save();
        }
    }

    /// <summary>卡片「浏览服务器目录」：列服务器目录树选择。</summary>
    private async void CardBrowseRemoteBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProjectCardVm vm) return;
        if (vm.SelectedServerLink is null)
        {
            await ShowToast("请先选择服务器（可到「服务器管理」页添加）");
            return;
        }

        var startDir = vm.RemoteUploadDir.Trim();
        List<string> dirs;
        try
        {
            dirs = await Task.Run(() => UploadEngine.SftpListDirs(vm.SelectedServerLink, startDir));
        }
        catch (Exception ex)
        {
            await ShowToast("读取服务器目录失败：" + ex.Message);
            return;
        }

        var browser = new ServerDirBrowserDialog(vm.SelectedServerLink, startDir, dirs);
        var dlg = new ContentDialog
        {
            Title = "浏览服务器目录",
            PrimaryButtonText = "使用此目录",
            CloseButtonText = "取消",
            XamlRoot = this.XamlRoot,
            Content = browser,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 560d;

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            vm.RemoteUploadDir = browser.CurrentPath;
            vm.Project.UploadRemoteDir = browser.CurrentPath;
            Save();
        }
    }

    /// <summary>卡片「上传」：直接执行上传（复用服务器管理的凭据 + 排除规则）。</summary>
    private async void CardRunUploadBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProjectCardVm vm) return;
        if (vm.UploadBusy) return;
        if (vm.SelectedServerLink is null)
        {
            await ShowToast("请先选择服务器（可到「服务器管理」页添加）");
            return;
        }

        var local = vm.LocalUploadPath.Trim();
        var remote = vm.RemoteUploadDir.Trim();
        if (string.IsNullOrEmpty(local) || (!File.Exists(local) && !Directory.Exists(local)))
        {
            await ShowToast("请先选择有效的本地目录或文件");
            return;
        }
        if (string.IsNullOrEmpty(remote))
        {
            await ShowToast("请填写服务器目标目录");
            return;
        }

        // 持久化上传配置
        vm.Project.UploadLocalPath = local;
        vm.Project.UploadRemoteDir = remote;
        vm.Project.UploadCustomExcludes = vm.CustomExcludesText;
        Save();

        var custom = (vm.CustomExcludesText ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0);

        vm.UploadBusy = true;
        vm.UploadPercent = 0;
        vm.UploadStatus = "上传中...";
        AppendUploadLog(vm, $"$ 上传 {local} → {remote}（{vm.SelectedServerLink.Name}）");

        var progress = new Progress<UploadProgress>(p =>
        {
            if (p.TotalFiles > 0) vm.UploadPercent = Math.Round((double)p.DoneFiles / p.TotalFiles * 100, 1);
            if (!string.IsNullOrEmpty(p.CurrentFile))
                vm.UploadStatus = $"{p.DoneFiles}/{p.TotalFiles}  {Path.GetFileName(p.CurrentFile)}";
            if (!string.IsNullOrEmpty(p.Message))
                AppendUploadLog(vm, p.Message);
            if (p.Error)
            {
                vm.UploadBusy = false;
                vm.UploadStatus = "上传失败";
            }
            else if (p.Finished)
            {
                vm.UploadBusy = false;
                vm.UploadStatus = "上传完成";
                vm.UploadPercent = 100;
            }
        });

        try
        {
            await UploadEngine.RunAsync(vm.SelectedServerLink, local, remote, vm.AutoExclude, custom, progress);
        }
        catch (Exception ex)
        {
            vm.UploadBusy = false;
            vm.UploadStatus = "上传异常";
            AppendUploadLog(vm, "上传异常：" + ex.Message);
        }
    }

    private static void AppendUploadLog(ProjectCardVm vm, string line)
        => vm.UploadLog += line + Environment.NewLine;

    /// <summary>上传日志自动滚动到底部。</summary>
    private void CardUploadLogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.SelectionStart = tb.Text.Length;
            tb.SelectionLength = 0;
        }
    }

    // =====================================================================
    // 新建工程
    // =====================================================================

    private async void NewProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new NewProjectDialog();
            var dlg = new ContentDialog
            {
                Title = "新建工程",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot,
                Content = dialog,
            };
            dlg.Resources["ContentDialogMaxWidth"] = 620d;

            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var project = dialog.TryGetResult(out var error);
            if (project is null)
            {
                await ShowToast(error);
                return;
            }

            var createError = await dialog.CreateAsync(project);
            if (!string.IsNullOrEmpty(createError))
            {
                await ShowToast($"创建失败：{createError}");
                return;
            }

            Projects.Insert(0, new ProjectCardVm(project));
            Save();
            await ShowToast("工程创建成功");
        }
        catch (Exception ex)
        {
            await ShowToast($"出错：{ex.Message}");
        }
    }

    private async void MyProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new MyProjectDialog();
            var dlg = new ContentDialog
            {
                Title = "创建我的专属工程",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot,
                Content = dialog,
            };
            dlg.Resources["ContentDialogMaxWidth"] = 620d;

            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            // 对话框已关闭，在 UI 线程校验和读取输入
            var projectPath = dialog.Validate(out var error);
            if (projectPath is null)
            {
                await ShowToast(error);
                return;
            }
            var cfg = dialog.GetConfig();

            // 在页面上显示进度
            ProgressBorder.Visibility = Visibility.Visible;
            ProgressText.Text = "开始创建...";

            var createError = await Task.Run(() =>
                dialog.CreateAsync(projectPath, cfg, msg => _ = DispatcherQueue.TryEnqueue(() => ProgressText.Text = msg)));

            ProgressBorder.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(createError))
            {
                await ShowToast($"创建失败：{createError}");
                return;
            }

            var name = Path.GetFileName(projectPath);
            var project = new Project
            {
                Name = name,
                Type = "专属",
                Framework = "uni-app + Flask",
                Path = projectPath,
            };
            Projects.Insert(0, new ProjectCardVm(project));
            Save();
            await ShowToast("专属工程创建成功");
        }
        catch (Exception ex)
        {
            ProgressBorder.Visibility = Visibility.Collapsed;
            await ShowToast($"出错：{ex.Message}");
        }
    }

    // =====================================================================
    // 打开 / 删除
    // =====================================================================

    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectCardVm vm && Directory.Exists(vm.Path))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{vm.Path}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    private async void DeleteProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProjectCardVm vm) return;
        var dlg = new ContentDialog
        {
            Title = "删除工程",
            Content = $"确定从列表中移除「{vm.Name}」吗？（不会删除磁盘上的文件）",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            Projects.Remove(vm);
            Save();
        }
    }

    // =====================================================================
    // 辅助
    // =====================================================================

    private async Task ShowToast(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "工程管理",
            Content = msg,
            CloseButtonText = "确定",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
