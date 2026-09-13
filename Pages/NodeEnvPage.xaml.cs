using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

/// <summary>
/// Node 环境页面：展示本机的 Node.js 运行时（系统 + 托管 + 自定义合并）、工具链，并提供包管理。
/// </summary>
public sealed partial class NodeEnvPage : Page
{
    public ObservableCollection<NodeEnvironment> Environments { get; } = new();
    public ObservableCollection<NodeProject> Projects { get; } = new();
    public ObservableCollection<InfoItem> ToolItems { get; } = new();
    public ObservableCollection<InfoItem> PathItems { get; } = new();

    public NodeEnvPage()
    {
        InitializeComponent();
        Loaded += NodeEnvPage_Loaded;
    }

    private void PageSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ProjectsPanel.Visibility = tag == "projects" ? Visibility.Visible : Visibility.Collapsed;
        RuntimesPanel.Visibility = tag == "runtimes" ? Visibility.Visible : Visibility.Collapsed;
        ToolchainPanel.Visibility = tag == "toolchain" ? Visibility.Visible : Visibility.Collapsed;
        PathPanel.Visibility = tag == "path" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NodeEnvPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadProjects();
        LoadingRing.IsActive = true;
        try
        {
            var snap = await Task.Run(CollectAll);
            Apply(snap);
        }
        catch
        {
            NodeVersionText.Text = "收集失败";
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void LoadProjects()
    {
        Projects.Clear();
        foreach (var entry in NodeProjectStore.Load())
        {
            if (string.IsNullOrEmpty(entry.Path) || !Directory.Exists(entry.Path)) continue;
            Projects.Add(new NodeProject
            {
                DisplayName = string.IsNullOrEmpty(entry.Name) ? Path.GetFileName(entry.Path.TrimEnd('\\')) : entry.Name!,
                Path = entry.Path,
                AddedAt = entry.AddedAt,
            });
        }
    }

    private void SaveProjects()
    {
        NodeProjectStore.Save(Projects.Select(p => new NodeProjectEntry
        {
            Path = p.Path,
            Name = p.DisplayName,
            AddedAt = p.AddedAt ?? DateTime.Now,
        }));
    }

    private async void AddProjectBtn_Click(object sender, RoutedEventArgs e)
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
        if (folder is null) return;

        var path = folder.Path;
        if (string.IsNullOrEmpty(path)) return;
        if (Projects.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        // 没有 package.json 的项目无法做依赖管理，提示但不阻断添加
        var hasPkg = File.Exists(Path.Combine(path, "package.json"));
        var proj = new NodeProject
        {
            DisplayName = Path.GetFileName(path.TrimEnd('\\')),
            Path = path,
            AddedAt = DateTime.Now,
        };
        Projects.Add(proj);
        SaveProjects();

        if (!hasPkg)
        {
            var dialog = new ContentDialog
            {
                Title = "未找到 package.json",
                Content = "该文件夹没有 package.json，无法用 npm / yarn / pnpm 管理依赖。可以先在项目里运行 npm init 生成，或删除该项目。",
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            };
            _ = dialog.ShowAsync();
        }
    }

    private void NewEnvBtn_Click(object sender, RoutedEventArgs e)
    {
        NewEnvDirBox.Text = "";
        NewEnvNameBox.Text = "";
        NewEnvHintText.Visibility = Visibility.Collapsed;
        NewEnvProgressPanel.Visibility = Visibility.Collapsed;
        NewEnvDialog.IsPrimaryButtonEnabled = true;
        NewEnvDialog.XamlRoot = XamlRoot;
        _ = NewEnvDialog.ShowAsync();
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
        var dir = NewEnvDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            NewEnvHintText.Text = "请填写项目目录";
            NewEnvHintText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        args.Cancel = true;
        var deferral = args.GetDeferral();
        NewEnvHintText.Visibility = Visibility.Collapsed;
        NewEnvProgressPanel.Visibility = Visibility.Visible;
        NewEnvStatusText.Text = "正在初始化项目（npm init）…";
        NewEnvDialog.IsPrimaryButtonEnabled = false;
        try
        {
            var fullDir = Path.GetFullPath(dir);
            Directory.CreateDirectory(fullDir);

            var name = NewEnvNameBox.Text.Trim();
            if (name.Length == 0) name = Path.GetFileName(fullDir.TrimEnd('\\', '/'));
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                NewEnvHintText.Text = "项目名称包含非法字符";
                NewEnvHintText.Visibility = Visibility.Visible;
                return;
            }

            // 1. 生成 package.json（名称与输入一致）
            var pkgPath = Path.Combine(fullDir, "package.json");
            if (!File.Exists(pkgPath))
            {
                var nodeExe = FindNodeExe();
                if (nodeExe is null)
                {
                    // node 不可用时直接写默认 package.json
                    File.WriteAllText(pkgPath, BuildDefaultPackageJson(name), Encoding.UTF8);
                }
                else
                {
                    var nodeDir = Path.GetDirectoryName(nodeExe);
                    var npmCli = string.IsNullOrEmpty(nodeDir) ? "" : Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");

                    var psi = new ProcessStartInfo
                    {
                        FileName = nodeExe,
                        Arguments = File.Exists(npmCli) ? $"\"{npmCli}\" init -y" : "init -y",
                        WorkingDirectory = fullDir,
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
                            NewEnvHintText.Text = "初始化失败：无法启动 node";
                            NewEnvHintText.Visibility = Visibility.Visible;
                            return;
                        }
                        var err = await proc.StandardError.ReadToEndAsync();
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode != 0)
                        {
                            NewEnvHintText.Text = $"npm init 失败（退出码 {proc.ExitCode}）：{FirstLine(err)}";
                            NewEnvHintText.Visibility = Visibility.Visible;
                            return;
                        }
                    }

                    // 2. 同步 name 到 package.json
                    SetPackageJsonName(pkgPath, name);
                }
            }

            // 3. 加入项目列表
            if (Projects.Any(p => string.Equals(p.Path, fullDir, StringComparison.OrdinalIgnoreCase)))
            {
                NewEnvHintText.Text = "该项目已存在于列表中";
                NewEnvHintText.Visibility = Visibility.Visible;
                return;
            }

            Projects.Add(new NodeProject
            {
                DisplayName = name,
                Path = fullDir,
                AddedAt = DateTime.Now,
            });
            SaveProjects();
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

    private static string BuildDefaultPackageJson(string name)
    {
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = name,
            ["version"] = "1.0.0",
            ["description"] = "",
            ["main"] = "index.js",
            ["scripts"] = new System.Text.Json.Nodes.JsonObject
            {
                ["test"] = "echo \"Error: no test specified\" && exit 1",
            },
            ["keywords"] = new System.Text.Json.Nodes.JsonArray(),
            ["author"] = "",
            ["license"] = "ISC",
        };
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void SetPackageJsonName(string pkgPath, string name)
    {
        try
        {
            if (!File.Exists(pkgPath)) return;
            var json = File.ReadAllText(pkgPath);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["name"] = name;
                File.WriteAllText(pkgPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }
        catch
        {
            // 改写失败不阻断
        }
    }

    private void DeleteProjectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NodeProject proj)
        {
            Projects.Remove(proj);
            SaveProjects();
        }
    }

    private void ManageProjectPackagesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NodeProject proj)
        {
            Frame.Navigate(typeof(NodePackageManagerPage), proj);
        }
    }

    // =====================================================================
    // 数据收集
    // =====================================================================

    private sealed class EnvSnapshot
    {
        public string NodeVersion = "";
        public string NodePath = "";
        public string NpmVersion = "";
        public string NpmPath = "";
        public string NpxVersion = "";
        public string NpxPath = "";
        public string YarnVersion = "";
        public string YarnPath = "";
        public string PnpmVersion = "";
        public List<string> RuntimePaths = new();
        public List<InfoItem> Tools = new();
        public List<InfoItem> Paths = new();
    }

    private static string ToolNodeDir => ToolPaths.InstallNode;

    private static EnvSnapshot CollectAll()
    {
        var snap = new EnvSnapshot();

        // ---- node ----
        snap.NodeVersion = ExtractVersion(RunCommand("node", "--version"));
        snap.NodePath = FirstLine(RunCommand("where", "node"));
        if (!string.IsNullOrEmpty(snap.NodePath) && File.Exists(snap.NodePath))
            snap.RuntimePaths.Add(snap.NodePath);

        // ---- npm / npx / yarn / pnpm ----
        var npmOut = RunCommand("npm", "--version");
        snap.NpmVersion = npmOut?.Trim() ?? "";
        snap.NpmPath = FirstLine(RunCommand("where", "npm"));

        var npxOut = RunCommand("npx", "--version");
        snap.NpxVersion = npxOut?.Trim() ?? "";
        snap.NpxPath = FirstLine(RunCommand("where", "npx"));

        var yarnOut = RunCommand("yarn", "--version");
        snap.YarnVersion = yarnOut?.Trim() ?? "";
        snap.YarnPath = FirstLine(RunCommand("where", "yarn"));

        var pnpmOut = RunCommand("pnpm", "--version");
        snap.PnpmVersion = pnpmOut?.Trim() ?? "";

        // ---- 工具链版本汇总 ----
        snap.Tools.Add(new InfoItem
        {
            Label = "node",
            Value = string.IsNullOrEmpty(snap.NodeVersion) ? "未检测到（不在 PATH）" : $"v{snap.NodeVersion}",
            HasAction = true,
            ActionTag = "node",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "npm",
            Value = string.IsNullOrEmpty(snap.NpmVersion) ? "未检测到" : $"{snap.NpmVersion}  ({snap.NpmPath})",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "yarn",
            Value = string.IsNullOrEmpty(snap.YarnVersion) ? "未检测到" : $"{snap.YarnVersion}  ({snap.YarnPath})",
        });
        snap.Tools.Add(new InfoItem
        {
            Label = "pnpm",
            Value = string.IsNullOrEmpty(snap.PnpmVersion) ? "未检测到" : snap.PnpmVersion,
        });

        // ---- PATH 中的 node ----
        var whereOut = RunCommand("where", "node");
        if (!string.IsNullOrEmpty(whereOut))
        {
            int i = 1;
            foreach (var rawLine in whereOut.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                snap.Paths.Add(new InfoItem { Label = $"入口 {i++}", Value = line });
            }
        }
        if (snap.Paths.Count == 0)
            snap.Paths.Add(new InfoItem { Label = "入口 1", Value = "未在 PATH 中找到 node" });

        return snap;
    }

    private void Apply(EnvSnapshot snap)
    {
        NodeVersionText.Text = string.IsNullOrEmpty(snap.NodeVersion) ? "未检测到" : $"v{snap.NodeVersion}";
        NodePathText.Text = Shorten(snap.NodePath, 40);

        NpmVersionText.Text = string.IsNullOrEmpty(snap.NpmVersion) ? "未检测到" : snap.NpmVersion;
        NpmPathText.Text = Shorten(snap.NpmPath, 40);

        NpxVersionText.Text = string.IsNullOrEmpty(snap.NpxVersion) ? "未检测到" : snap.NpxVersion;
        NpxPathText.Text = Shorten(snap.NpxPath, 40);

        YarnVersionText.Text = string.IsNullOrEmpty(snap.YarnVersion) ? "未检测到" : snap.YarnVersion;
        YarnPathText.Text = Shorten(snap.YarnPath, 40);

        foreach (var item in snap.Tools) ToolItems.Add(item);
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

        // 1. 系统默认 node（PATH）
        if (!string.IsNullOrEmpty(snap.NodePath) && File.Exists(snap.NodePath))
        {
            Environments.Add(new NodeEnvironment
            {
                DisplayName = $"Node {snap.NodeVersion}",
                Path = snap.NodePath,
                IsDefault = true,
                Version = $"Node v{snap.NodeVersion}",
            });
        }

        // 2. 工具托管目录（下载安装的便携版）
        var toolNode = Path.Combine(ToolNodeDir, "node.exe");
        if (File.Exists(toolNode))
        {
            if (!Environments.Any(e => string.Equals(e.Path, toolNode, StringComparison.OrdinalIgnoreCase)))
            {
                var env = new NodeEnvironment
                {
                    DisplayName = "Node（托管）",
                    Path = toolNode,
                };
                Environments.Add(env);
                ProbeVersionAsync(env);
            }
        }

        // 3. 自定义添加的（持久化），去重
        foreach (var entry in NodeEnvStorage.Load())
        {
            if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) continue;
            if (Environments.Any(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;

            var env = new NodeEnvironment
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
        NodeEnvStorage.Save(Environments
            .Where(e => e.IsCustom)
            .Select(e => new CustomNodeEntry
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

        var env = new NodeEnvironment
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
        if (sender is Button btn && btn.Tag is NodeEnvironment env && env.IsCustom)
        {
            Environments.Remove(env);
            SaveCustomEnvironments();
        }
    }

    private void ManagePackagesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NodeEnvironment env)
        {
            Frame.Navigate(typeof(NodePackageManagerPage), env);
        }
    }

    private void ToolchainActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && tag == "node")
        {
            Frame.Navigate(typeof(DownloadNodePage));
        }
    }

    private void ProbeVersionAsync(NodeEnvironment env)
    {
        _ = Task.Run(() =>
        {
            var version = ExtractVersion(RunCommand(env.Path, "--version"));
            DispatcherQueue.TryEnqueue(() =>
            {
                env.Version = string.IsNullOrEmpty(version) ? "无法检测版本" : $"Node v{version}";
            });
        });
    }

    // =====================================================================
    // 辅助函数
    // =====================================================================

    /// <summary>自动定位 node.exe：工具托管目录优先，其次 PATH。</summary>
    private static string? FindNodeExe()
    {
        var toolNode = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeraphineAITool", "tools", "node", "node.exe");
        if (File.Exists(toolNode)) return toolNode;

        var whereNode = FirstLine(RunCommand("where", "node"));
        if (!string.IsNullOrEmpty(whereNode) && File.Exists(whereNode)) return whereNode;
        return null;
    }

    private static string? RunCommand(string fileName, string args) => ProcessHelper.Run(fileName, args);

    private static string ExtractVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var text = output.Trim().TrimStart('v');
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
        if (PathEnvCombo.SelectedItem is not NodeEnvironment env)
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
            NodeEnvPage_Loaded(sender, e);
        }
        catch (Exception ex)
        {
            PathStatusText.Text = $"设置失败：{ex.Message}";
        }
    }

    private void RemovePathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PathEnvCombo.SelectedItem is not NodeEnvironment env)
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
            NodeEnvPage_Loaded(sender, e);
        }
        catch (Exception ex)
        {
            PathStatusText.Text = $"移除失败：{ex.Message}";
        }
    }
}
