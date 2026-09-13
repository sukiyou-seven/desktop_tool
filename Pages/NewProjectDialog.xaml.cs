using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

/// <summary>
/// 新建工程表单 UserControl。由调用方包在 ContentDialog 中使用。
/// </summary>
public sealed partial class NewProjectDialog : UserControl
{
    public NewProjectDialog()
    {
        InitializeComponent();
        TypeCombo.SelectedIndex = 0;
        PyTemplateCombo.SelectedIndex = 0;
        VenvModeCombo.SelectedIndex = 1;
        NodeFrameworkCombo.SelectedIndex = 0;
        PhpFrameworkCombo.SelectedIndex = 0;
        GoTemplateCombo.SelectedIndex = 0;
        try { InitPythonInterpreters(); } catch { }
    }

    private void InitPythonInterpreters()
    {
        BasePyCombo.Items.Add("系统默认 python");
        ExistingPyCombo.Items.Add("系统默认 python");
        foreach (var entry in PythonEnvStorage.Load())
        {
            if (!string.IsNullOrEmpty(entry.Path))
            {
                BasePyCombo.Items.Add(entry.Path);
                ExistingPyCombo.Items.Add(entry.Path);
            }
        }
        BasePyCombo.SelectedIndex = 0;
        ExistingPyCombo.SelectedIndex = 0;
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PythonPanel is null) return;
        var idx = TypeCombo.SelectedIndex;
        PythonPanel.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        NodePanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        PhpPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        GoPanel.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VenvMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BasePyCombo is null) return;
        var idx = VenvModeCombo.SelectedIndex;
        BasePyCombo.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        ExistingPyCombo.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

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
        if (folder is not null) DirBox.Text = folder.Path;
    }

    /// <summary>
    /// 校验输入并返回工程信息；失败返回 null，error 输出错误信息。
    /// </summary>
    public Project? TryGetResult(out string error)
    {
        error = "";
        var name = NameBox.Text.Trim();
        var parentDir = DirBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { error = "请填写工程名称"; return null; }
        if (string.IsNullOrEmpty(parentDir)) { error = "请选择存储目录"; return null; }

        var projectPath = Path.Combine(parentDir, name);
        if (Directory.Exists(projectPath)) { error = $"目录已存在：{projectPath}"; return null; }

        var type = ((ComboBoxItem)TypeCombo.SelectedItem).Content.ToString();
        var framework = "";
        var envPath = "";

        if (type == "Python")
        {
            framework = ((ComboBoxItem)PyTemplateCombo.SelectedItem).Content.ToString();
            var venvIdx = VenvModeCombo.SelectedIndex;
            if (venvIdx == 1) // 新建 venv
            {
                envPath = Path.Combine(projectPath, ".venv");
            }
            else if (venvIdx == 2) // 使用已有
            {
                envPath = ExistingPyCombo.SelectedIndex == 0
                    ? "python"
                    : (string)ExistingPyCombo.SelectedItem;
            }
        }
        else if (type == "Node")
        {
            framework = ((ComboBoxItem)NodeFrameworkCombo.SelectedItem).Content.ToString();
        }
        else if (type == "PHP")
        {
            framework = ((ComboBoxItem)PhpFrameworkCombo.SelectedItem).Content.ToString();
        }
        else if (type == "Go")
        {
            framework = ((ComboBoxItem)GoTemplateCombo.SelectedItem).Content.ToString();
        }

        return new Project
        {
            Name = name,
            Type = type,
            Framework = framework,
            Path = projectPath,
            EnvPath = envPath,
        };
    }

    /// <summary>执行工程创建（建目录、初始化模板、创建 venv 等）。</summary>
    public async Task<string> CreateAsync(Project project)
    {
        try
        {
            Directory.CreateDirectory(project.Path);
            var parentDir = Path.GetDirectoryName(project.Path)!;

            if (project.Type == "Python")
            {
                var venvIdx = VenvModeCombo.SelectedIndex;
                var basePy = BasePyCombo.SelectedIndex == 0 ? "python" : (string)BasePyCombo.SelectedItem;

                if (venvIdx == 1) // 新建 venv
                    RunCommand(basePy, $"-m venv \"{project.EnvPath}\"", parentDir);

                var tmpl = PyTemplateCombo.SelectedIndex;
                if (tmpl == 1) // Flask
                {
                    File.WriteAllText(Path.Combine(project.Path, "app.py"),
                        "from flask import Flask\napp = Flask(__name__)\n\n@app.route('/')\ndef hello():\n    return 'Hello, World!'\n\nif __name__ == '__main__':\n    app.run(debug=True)\n");
                    File.WriteAllText(Path.Combine(project.Path, "requirements.txt"), "flask\n");
                }
                else if (tmpl == 2) // Django
                {
                    RunCommand(basePy, $"-m django startproject {project.Name} \"{project.Path}\"", parentDir);
                }
                else if (tmpl == 3) // FastAPI
                {
                    File.WriteAllText(Path.Combine(project.Path, "main.py"),
                        "from fastapi import FastAPI\n\napp = FastAPI()\n\n@app.get('/')\ndef read_root():\n    return {'Hello': 'World'}\n");
                    File.WriteAllText(Path.Combine(project.Path, "requirements.txt"), "fastapi\nuvicorn\n");
                }
            }
            else if (project.Type == "Node")
            {
                var idx = NodeFrameworkCombo.SelectedIndex;
                var tmpl = idx switch { 0 => "vue", 1 => "react", 2 => "vanilla", _ => "" };
                if (!string.IsNullOrEmpty(tmpl))
                    RunCommand("npm", $"create vite@latest . -- --template {tmpl}", project.Path);
                else if (idx == 3) // Express
                {
                    File.WriteAllText(Path.Combine(project.Path, "package.json"),
                        "{\"name\":\"" + project.Name + "\",\"version\":\"1.0.0\",\"main\":\"index.js\",\"scripts\":{\"start\":\"node index.js\"},\"dependencies\":{\"express\":\"^4.18.0\"}}");
                    File.WriteAllText(Path.Combine(project.Path, "index.js"),
                        "const express=require('express');const app=express();const port=3000;app.get('/',(req,res)=>res.send('Hello World!'));app.listen(port,()=>console.log(`Server running at http://localhost:${port}`));");
                }
            }
            else if (project.Type == "PHP")
            {
                var idx = PhpFrameworkCombo.SelectedIndex;
                if (idx == 0)
                    RunCommand("composer", $"create-project laravel/laravel \"{project.Path}\"", parentDir);
                else if (idx == 1)
                    RunCommand("composer", $"create-project topthink/think \"{project.Path}\"", parentDir);
            }
            else if (project.Type == "Go")
            {
                var module = string.IsNullOrEmpty(GoModuleBox.Text.Trim()) ? project.Name : GoModuleBox.Text.Trim();
                RunCommand("go", $"mod init {module}", project.Path);
                var idx = GoTemplateCombo.SelectedIndex;
                if (idx == 1) // Gin
                {
                    File.WriteAllText(Path.Combine(project.Path, "main.go"),
                        "package main\n\nimport \"github.com/gin-gonic/gin\"\n\nfunc main() {\n\tr := gin.Default()\n\tr.GET(\"/\", func(c *gin.Context) {\n\t\tc.JSON(200, gin.H{\"message\": \"hello\"})\n\t})\n\tr.Run(\":8080\")\n}\n");
                    RunCommand("go", "get github.com/gin-gonic/gin", project.Path);
                }
                else if (idx == 2) // CLI
                {
                    File.WriteAllText(Path.Combine(project.Path, "main.go"),
                        "package main\n\nimport (\n\t\"fmt\"\n\t\"os\"\n)\n\nfunc main() {\n\tfmt.Println(\"Args:\", os.Args)\n}\n");
                }
                else if (idx == 3) // gRPC
                {
                    File.WriteAllText(Path.Combine(project.Path, "main.go"),
                        "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"gRPC server\")\n}\n");
                    RunCommand("go", "get google.golang.org/grpc", project.Path);
                }
                else // empty
                {
                    File.WriteAllText(Path.Combine(project.Path, "main.go"),
                        "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"Hello, World!\")\n}\n");
                }
            }
            return "";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? RunCommand(string fileName, string args, string workingDir)
    {
        try
        {
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
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            proc.WaitForExit(60000);
            return (proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()).Trim();
        }
        catch { return null; }
    }
}
