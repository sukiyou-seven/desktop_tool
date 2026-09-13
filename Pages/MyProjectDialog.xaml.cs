using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

public record MyProjectConfig(
    string MongoIp, string MongoPort, string MongoDb, string MongoPwd,
    string RedisIp, string RedisPort, string RedisPwd, string RedisDb, string RedisExpire,
    string AfProject, string AfToken, string AfFolder, string AfModel);

/// <summary>
/// 创建专属工程：uni-app 客户端 + Flask 服务端 + uni-app 后台。
/// </summary>
public sealed partial class MyProjectDialog : UserControl
{
    public MyProjectDialog()
    {
        InitializeComponent();

        // 默认值
        NameBox.Text = "myproject";
        MongoIpBox.Text = "localhost";
        MongoPortBox.Text = "27017";
        MongoDbBox.Text = "myproject";
        RedisIpBox.Text = "localhost";
        RedisPortBox.Text = "6379";
        RedisDbBox.Text = "6";
        RedisExpireBox.Text = "25920000";

        // 所有输入框获取焦点时全选
        foreach (var tb in new[] { NameBox, RootDirBox, MongoIpBox, MongoPortBox, MongoDbBox, MongoPwdBox,
            RedisIpBox, RedisPortBox, RedisPwdBox, RedisDbBox, RedisExpireBox,
            AfProjectBox, AfTokenBox, AfFolderBox, AfModelBox })
        {
            tb.GotFocus += (s, e) => tb.SelectAll();
        }
    }

    public void ShowProgress(string text)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = text;
    }

    public void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>在 UI 线程读取所有输入值，避免后台线程访问控件。</summary>
    public MyProjectConfig GetConfig() => new(
        MongoIpBox.Text.Trim(), MongoPortBox.Text.Trim(), MongoDbBox.Text.Trim(), MongoPwdBox.Text.Trim(),
        RedisIpBox.Text.Trim(), RedisPortBox.Text.Trim(), RedisPwdBox.Text.Trim(), RedisDbBox.Text.Trim(), RedisExpireBox.Text.Trim(),
        AfProjectBox.Text.Trim(), AfTokenBox.Text.Trim(), AfFolderBox.Text.Trim(), AfModelBox.Text.Trim());

    private async void DirBtn_Click(object sender, RoutedEventArgs e)
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
        if (folder is not null) RootDirBox.Text = folder.Path;
    }

    /// <summary>校验并返回工程路径，失败返回 null 并设置 error。</summary>
    public string? Validate(out string error)
    {
        error = "";
        var name = NameBox.Text.Trim();
        var root = RootDirBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { error = "请填写项目名称"; return null; }
        if (string.IsNullOrEmpty(root)) { error = "请选择根目录"; return null; }
        var path = Path.Combine(root, name);
        if (Directory.Exists(path)) { error = $"目录已存在：{path}"; return null; }
        return path;
    }

    /// <summary>执行创建，返回错误信息（空表示成功）。同步方法，应在后台线程调用。</summary>
    public string CreateAsync(string projectPath, MyProjectConfig cfg, Action<string>? onProgress = null)
    {
        try
        {
            var name = Path.GetFileName(projectPath);
            onProgress?.Invoke("创建目录结构...");
            Directory.CreateDirectory(projectPath);

            // 1. 服务端：复制模板
            onProgress?.Invoke("复制 Flask 服务端模板...");
            var flaskDir = Path.Combine(projectPath, $"flask_{name}");
            var templateDir = FindTemplateDir("python_project_template");
            if (string.IsNullOrEmpty(templateDir))
                return $"未找到 Flask 模板目录（Assets/python_project_template）";
            CopyDirectory(templateDir, flaskDir);

            // 2. 替换 app.yml 和 apifox.yml
            onProgress?.Invoke("写入配置文件...");
            ReplaceAppYml(Path.Combine(flaskDir, "config", "app.yml"), cfg);
            ReplaceApifoxYml(Path.Combine(flaskDir, "config", "apifox.yml"), cfg);

            // 3. 创建虚拟环境 .SeraphineVenv（不捕获输出）
            onProgress?.Invoke("创建 Python 虚拟环境...");
            var venvDir = Path.Combine(flaskDir, ".SeraphineVenv");
            var venvCode = ProcessHelper.RunNoCapture("python", $"-m venv \"{venvDir}\"", flaskDir, 120000);
            var venvPython = Path.Combine(venvDir, "Scripts", "python.exe");
            if (!File.Exists(venvPython))
                return $"创建虚拟环境失败（退出码={venvCode}）：未找到 {venvPython}";

            // 4. 安装依赖（输出写到日志文件，失败时可查看原因）
            var reqFile = Path.Combine(flaskDir, "requirements.txt");
            if (File.Exists(reqFile))
            {
                onProgress?.Invoke("安装 Python 依赖（清华源，可能需要几分钟）...");
                var logFile = Path.Combine(flaskDir, "pip_install.log");
                var pipCode = RunWithLog(venvPython,
                    "-m pip install -r requirements.txt -i https://pypi.tuna.tsinghua.edu.cn/simple",
                    flaskDir, logFile, 600000);
                var flaskPkg = Path.Combine(venvDir, "Lib", "site-packages", "flask");
                if (!Directory.Exists(flaskPkg))
                {
                    var tail = "";
                    if (File.Exists(logFile))
                    {
                        var lines = File.ReadAllLines(logFile);
                        tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 15)));
                    }
                    return $"pip 安装失败（退出码={pipCode}）：\n{tail}";
                }
            }

            // 5. 客户端 uni-app
            onProgress?.Invoke("创建 uni-app 客户端...");
            var clientDir = Path.Combine(projectPath, $"uni_app_{name}");
            var uniTemplate = FindTemplateDir("uni-preset-vue-vite-ts");
            if (string.IsNullOrEmpty(uniTemplate))
                return $"未找到 uni-app 模板目录（Assets/uni-preset-vue-vite-ts）";
            CopyDirectory(uniTemplate, clientDir);
            if (!File.Exists(Path.Combine(clientDir, "package.json")))
                return $"uni-app 客户端创建失败：未找到 package.json";

            // 6. 后台 uni-app
            onProgress?.Invoke("创建 uni-app 后台...");
            var adminDir = Path.Combine(projectPath, $"uni_app_{name}_后台");
            CopyDirectory(uniTemplate, adminDir);
            if (!File.Exists(Path.Combine(adminDir, "package.json")))
                return $"uni-app 后台创建失败：未找到 package.json";

            onProgress?.Invoke("完成！");
            return "";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string FindTemplateDir(string name)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", name),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", name),
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        // 向上搜索（开发环境下 Assets 可能在项目根目录）
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var p = Path.Combine(dir, "Assets", name);
            if (Directory.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return "";
    }

    // =====================================================================
    // YAML 替换
    // =====================================================================

    private void ReplaceAppYml(string path, MyProjectConfig cfg)
    {
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var section = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length > 0 && !char.IsWhiteSpace(line[0]) && trimmed.EndsWith(":"))
            {
                section = trimmed.TrimEnd(':').ToLower();
            }
            if (section == "mongo")
            {
                if (Regex.IsMatch(trimmed, @"^host\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"host\s*:.*", $"host: {cfg.MongoIp}");
                else if (Regex.IsMatch(trimmed, @"^database\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"database\s*:.*", $"database: {cfg.MongoDb}");
                else if (Regex.IsMatch(trimmed, @"^username\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"username\s*:.*", $"username: {cfg.MongoDb}");
                else if (Regex.IsMatch(trimmed, @"^password\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"password\s*:.*", $"password: {cfg.MongoPwd}");
                else if (Regex.IsMatch(trimmed, @"^auth_source\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"auth_source\s*:.*", $"auth_source: {cfg.MongoDb}");
                else if (Regex.IsMatch(trimmed, @"^port\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"port\s*:.*", $"port: {cfg.MongoPort}");
            }
            else if (section == "redis")
            {
                if (Regex.IsMatch(trimmed, @"^REDIS_HOST\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"REDIS_HOST\s*:.*", $"REDIS_HOST: {cfg.RedisIp}");
                else if (Regex.IsMatch(trimmed, @"^REDIS_PORT\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"REDIS_PORT\s*:.*", $"REDIS_PORT: {cfg.RedisPort}");
                else if (Regex.IsMatch(trimmed, @"^REDIS_PASSWORD\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"REDIS_PASSWORD\s*:.*", $"REDIS_PASSWORD: {cfg.RedisPwd}");
                else if (Regex.IsMatch(trimmed, @"^REDIS_DB\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"REDIS_DB\s*:.*", $"REDIS_DB: {cfg.RedisDb}");
                else if (Regex.IsMatch(trimmed, @"^REDIS_CACHE_TIMEOUT\s*:", RegexOptions.IgnoreCase))
                    lines[i] = Regex.Replace(line, @"REDIS_CACHE_TIMEOUT\s*:.*", $"REDIS_CACHE_TIMEOUT: {cfg.RedisExpire}");
            }
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private void ReplaceApifoxYml(string path, MyProjectConfig cfg)
    {
        if (!File.Exists(path)) return;
        var content = File.ReadAllText(path, Encoding.UTF8);
        content = Regex.Replace(content, @"^project_id\s*:.*$", $"project_id: {cfg.AfProject}", RegexOptions.Multiline);
        content = Regex.Replace(content, @"^api_token\s*:.*$", $"api_token: {cfg.AfToken}", RegexOptions.Multiline);
        content = Regex.Replace(content, @"^folder_id\s*:.*$", $"folder_id: {cfg.AfFolder}", RegexOptions.Multiline);
        content = Regex.Replace(content, @"^model_id\s*:.*$", $"model_id: {cfg.AfModel}", RegexOptions.Multiline);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    // =====================================================================
    // 辅助
    // =====================================================================

    /// <summary>执行命令，stdout/stderr 写入日志文件，返回退出码。</summary>
    private static int RunWithLog(string fileName, string args, string workingDir, string logFile, int timeoutMs)
    {
        try
        {
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var combined = string.Join(';', userPath, machinePath).Trim(';');

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!string.IsNullOrEmpty(combined))
                psi.EnvironmentVariables["PATH"] = combined;

            using var writer = new StreamWriter(logFile, false, new UTF8Encoding(false));
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return -1;

            // 异步读取并写入日志
            var outTask = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) is not null)
                    lock (writer) writer.WriteLine(line);
            });
            var errTask = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardError.ReadLine()) is not null)
                    lock (writer) writer.WriteLine(line);
            });

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                outTask.Wait(5000);
                errTask.Wait(5000);
                return -2;
            }
            outTask.Wait(5000);
            errTask.Wait(5000);
            return proc.ExitCode;
        }
        catch { return -1; }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            // 跳过 __pycache__
            if (file.Contains("__pycache__")) continue;
            var target = Path.Combine(dst, Path.GetFileName(file));
            File.Copy(file, target, true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            if (dir.Contains("__pycache__")) continue;
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(dst, name));
        }
    }

}
