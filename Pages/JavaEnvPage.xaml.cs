using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

public class JdkVersionItem
{
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? InstallPath { get; set; }
    public string? DownloadUrl { get; set; }
    public Visibility InstallBtnVisibility => InstallPath is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UninstallBtnVisibility => InstallPath is not null ? Visibility.Visible : Visibility.Collapsed;
}

public class JavaDepItem
{
    public string Name { get; set; } = "";
}

public class JdkEntry
{
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed partial class JavaEnvPage : Page
{
    public ObservableCollection<JdkVersionItem> Jdks { get; } = new();
    public ObservableCollection<JavaDepItem> Deps { get; } = new();
    public ObservableCollection<JdkEntry> JdkEntries { get; } = new();

    private string? _pkgDir;
    private string? _newProjDir;

    public JavaEnvPage()
    {
        InitializeComponent();
        JdkList.ItemsSource = Jdks;
        DepList.ItemsSource = Deps;
        JdkCombo.ItemsSource = JdkEntries;
        _ = LoadOverviewAsync();
    }

    private void PageSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ToolchainPanel.Visibility = tag == "toolchain" ? Visibility.Visible : Visibility.Collapsed;
        PackagesPanel.Visibility = tag == "packages" ? Visibility.Visible : Visibility.Collapsed;
        NewProjectPanel.Visibility = tag == "newproject" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "toolchain" && Jdks.Count == 0) _ = LoadJdksAsync();
    }

    private async Task LoadOverviewAsync()
    {
        await Task.Run(() =>
        {
            var java = ProcessHelper.Run("java", "-version 2>&1");
            var javac = ProcessHelper.Run("javac", "-version 2>&1");
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                JavaVersionText.Text = string.IsNullOrEmpty(java) ? "未检测到" : java.Split('\n')[0].Trim();
                JavacVersionText.Text = string.IsNullOrEmpty(javac) ? "未检测到" : javac.Trim();
                JavaHomeText.Text = string.IsNullOrEmpty(javaHome) ? "未设置" : javaHome;
                LoadJdkCombo();
            });
        });
    }

    private void LoadJdkCombo()
    {
        JdkEntries.Clear();
        var installRoot = Path.Combine(AppContext.BaseDirectory, "env", "installers", "java");
        if (Directory.Exists(installRoot))
        {
            foreach (var dir in Directory.GetDirectories(installRoot))
            {
                if (File.Exists(Path.Combine(dir, "bin", "java.exe")))
                    JdkEntries.Add(new JdkEntry { DisplayName = $"{Path.GetFileName(dir)} ({dir})", Path = dir });
            }
        }
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome) && Directory.Exists(javaHome) && !JdkEntries.Any(e => e.Path == javaHome))
            JdkEntries.Add(new JdkEntry { DisplayName = $"JAVA_HOME ({javaHome})", Path = javaHome });
    }

    private void SetJavaHomeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (JdkCombo.SelectedItem is not JdkEntry jdk) return;
        Environment.SetEnvironmentVariable("JAVA_HOME", jdk.Path, EnvironmentVariableTarget.User);
        var binDir = Path.Combine(jdk.Path, "bin");
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        if (!current.Split(';').Any(p => p.Trim().Equals(binDir, StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("PATH", current.TrimEnd(';') + ";" + binDir, EnvironmentVariableTarget.User);
        JavaStatusText.Text = $"已设置 JAVA_HOME={jdk.Path} 并加入 PATH";
        _ = LoadOverviewAsync();
    }

    private void RemoveJavaHomeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (JdkCombo.SelectedItem is not JdkEntry jdk) return;
        Environment.SetEnvironmentVariable("JAVA_HOME", null, EnvironmentVariableTarget.User);
        var binDir = Path.Combine(jdk.Path, "bin");
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Trim().Equals(binDir, StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), EnvironmentVariableTarget.User);
        JavaStatusText.Text = "已移除 JAVA_HOME 和 PATH";
        _ = LoadOverviewAsync();
    }

    private async Task LoadJdksAsync()
    {
        JdkStatusText.Text = "加载中...";
        Jdks.Clear();
        await Task.Run(() =>
        {
            var installed = new Dictionary<string, string>();
            var installRoot = Path.Combine(AppContext.BaseDirectory, "env", "installers", "java");
            if (Directory.Exists(installRoot))
            {
                foreach (var dir in Directory.GetDirectories(installRoot))
                {
                    var name = Path.GetFileName(dir);
                    var m = Regex.Match(name, @"(\d+(?:\.\d+)*)");
                    if (m.Success) installed[m.Groups[1].Value] = dir;
                }
            }
            var available = new List<(string ver, string url)>();
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var json = http.GetStringAsync("https://api.adoptium.net/v3/info/available_releases").Result;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("available_releases", out var releases))
                {
                    foreach (var r in releases.EnumerateArray())
                    {
                        var ver = r.GetInt32().ToString();
                        var url = $"https://api.adoptium.net/v3/binary/latest/{ver}/ga/windows/x64/jdk/hotspot/normal/eclipse?project=jdk";
                        available.Add((ver, url));
                    }
                }
            }
            catch { }
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var (ver, url) in available.OrderByDescending(x => int.TryParse(x.ver, out var v) ? v : 0))
                {
                    Jdks.Add(new JdkVersionItem
                    {
                        Version = ver,
                        Kind = installed.ContainsKey(ver) ? "已安装" : "Temurin 可下载",
                        InstallPath = installed.GetValueOrDefault(ver),
                        DownloadUrl = url,
                    });
                }
                foreach (var kv in installed)
                {
                    if (!available.Any(a => a.ver == kv.Key))
                        Jdks.Add(new JdkVersionItem { Version = kv.Key, Kind = "已安装（本地）", InstallPath = kv.Value });
                }
                JdkStatusText.Text = $"共 {Jdks.Count} 个版本";
            });
        });
    }

    private void JdkSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = (sender as TextBox)?.Text.Trim();
        foreach (var item in JdkList.Items)
        {
            if (item is JdkVersionItem j && JdkList.ContainerFromItem(item) is ListViewItem c)
                c.Visibility = string.IsNullOrEmpty(filter) || j.Version.Contains(filter) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RefreshJdksBtn_Click(object sender, RoutedEventArgs e) => _ = LoadJdksAsync();

    private async void InstallJdkBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not JdkVersionItem item) return;
        var installDir = Path.Combine(AppContext.BaseDirectory, "env", "installers", "java", $"jdk-{item.Version}");
        var downloadDir = Path.Combine(AppContext.BaseDirectory, "env", "downloads", "java");
        Directory.CreateDirectory(downloadDir);
        Directory.CreateDirectory(installDir);
        var zipPath = Path.Combine(downloadDir, $"jdk-{item.Version}.zip");
        JdkStatusText.Text = $"下载 JDK {item.Version}...";
        await Task.Run(() =>
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                var data = http.GetByteArrayAsync(item.DownloadUrl).Result;
                File.WriteAllBytes(zipPath, data);
                _ = DispatcherQueue.TryEnqueue(() => JdkStatusText.Text = "解压中...");
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, installDir, true);
                // jdk zip 解压后通常有一个子目录，把内容移到上层
                var subDirs = Directory.GetDirectories(installDir);
                if (subDirs.Length == 1)
                {
                    var sub = subDirs[0];
                    foreach (var f in Directory.GetFiles(sub)) File.Move(f, Path.Combine(installDir, Path.GetFileName(f)), true);
                    foreach (var d in Directory.GetDirectories(sub)) Directory.Move(d, Path.Combine(installDir, Path.GetFileName(d)));
                    Directory.Delete(sub, true);
                }
                _ = DispatcherQueue.TryEnqueue(() => { JdkStatusText.Text = $"JDK {item.Version} 安装完成"; _ = LoadJdksAsync(); LoadJdkCombo(); });
            }
            catch (Exception ex)
            {
                _ = DispatcherQueue.TryEnqueue(() => JdkStatusText.Text = $"安装失败：{ex.Message}");
            }
        });
    }

    private void UninstallJdkBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not JdkVersionItem item || item.InstallPath is null) return;
        try
        {
            var installRoot = Path.Combine(AppContext.BaseDirectory, "env", "installers", "java");
            if (!item.InstallPath.StartsWith(installRoot)) { JdkStatusText.Text = "只能卸载通过本工具安装的版本"; return; }
            Directory.Delete(item.InstallPath, true);
            JdkStatusText.Text = $"已卸载 JDK {item.Version}";
            _ = LoadJdksAsync();
            LoadJdkCombo();
        }
        catch (Exception ex) { JdkStatusText.Text = $"卸载失败：{ex.Message}"; }
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
        if (folder is not null) { _pkgDir = folder.Path; PkgProjectBox.Text = folder.Path; await RefreshDepsAsync(); }
    }

    private async Task RefreshDepsAsync()
    {
        if (string.IsNullOrEmpty(_pkgDir)) return;
        Deps.Clear();
        PkgStatusText.Text = "加载中...";
        await Task.Run(() =>
        {
            string? output = null;
            if (File.Exists(Path.Combine(_pkgDir, "pom.xml")))
                output = ProcessHelper.Run("mvn", "dependency:list -q", _pkgDir, 120000);
            else if (File.Exists(Path.Combine(_pkgDir, "build.gradle")))
                output = ProcessHelper.Run("gradle", "dependencies --configuration runtimeClasspath -q", _pkgDir, 120000);
            var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var line in lines.Where(l => l.Contains(':') && !l.StartsWith("[") && !l.StartsWith("(")))
                    Deps.Add(new JavaDepItem { Name = line.Trim() });
                PkgStatusText.Text = $"共 {Deps.Count} 个依赖";
            });
        });
    }

    private void RefreshDepsBtn_Click(object sender, RoutedEventArgs e) => _ = RefreshDepsAsync();

    private async void MvnInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgDir)) return;
        PkgStatusText.Text = "mvn install...";
        await Task.Run(() => ProcessHelper.Run("mvn", "install -DskipTests", _pkgDir, 300000));
        PkgStatusText.Text = "mvn install 完成";
    }

    private async void GradleBuildBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pkgDir)) return;
        PkgStatusText.Text = "gradle build...";
        await Task.Run(() => ProcessHelper.Run("gradle", "build -x test", _pkgDir, 300000));
        PkgStatusText.Text = "gradle build 完成";
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
        var parts = name.Split('.');
        var artifactId = parts.Length > 1 ? parts[^1] : name;
        var groupId = parts.Length > 1 ? string.Join('.', parts[..^1]) : "com.example";
        var projDir = Path.Combine(_newProjDir, artifactId);
        if (Directory.Exists(projDir)) { NewProjStatusText.Text = "目录已存在"; return; }
        var buildIdx = NewProjBuildCombo.SelectedIndex;
        NewProjStatusText.Text = "创建中...";
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(projDir);
                if (buildIdx == 0) // Maven
                {
                    ProcessHelper.Run("mvn",
                        $"archetype:generate -DgroupId={groupId} -DartifactId={artifactId} -DarchetypeArtifactId=maven-archetype-quickstart -DinteractiveMode=false",
                        _newProjDir, 120000);
                }
                else if (buildIdx == 1) // Gradle
                {
                    ProcessHelper.Run("gradle", "init --type java-application --dsl groovy --project-name " + artifactId, projDir, 120000);
                }
                else // 纯目录
                {
                    var srcDir = Path.Combine(projDir, "src", groupId.Replace('.', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(srcDir);
                    File.WriteAllText(Path.Combine(srcDir, "Main.java"),
                        $"package {groupId};\n\npublic class Main {{\n    public static void main(String[] args) {{\n        System.out.println(\"Hello, World!\");\n    }}\n}}\n");
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
