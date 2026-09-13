using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace DesktopTool;

/// <summary>一个可下载的工具条目。</summary>
public class ToolCatalogTool : INotifyPropertyChanged
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; Notify(nameof(Name)); }
    }

    public string Description { get; set; } = "";

    /// <summary>winget 包 ID（如 Git.Git）；为空则用官网链接。</summary>
    public string? WingetId { get; set; }

    private string _url = "";
    /// <summary>下载地址（直链）或官网地址。</summary>
    public string Url
    {
        get => _url;
        set { _url = value; Notify(nameof(Url), nameof(HomepageUrl)); }
    }

    private string _type = "page";
    /// <summary>download=本工具直接下载；page=用浏览器打开官网页面。</summary>
    [JsonPropertyName("type")]
    public string Type
    {
        get => _type;
        set { _type = value; Notify(nameof(Type), nameof(PrimaryButtonText), nameof(DownloadButtonVisibility), nameof(PageButtonVisibility)); }
    }

    /// <summary>官网地址（用于“打开官网”超链接）；为空时回退到 Url。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Homepage { get; set; }

    /// <summary>“打开官网”超链接使用的地址。</summary>
    public string HomepageUrl => string.IsNullOrEmpty(Homepage) ? Url : Homepage;

    private string? _icon;
    /// <summary>可选：图标图片 URL。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon
    {
        get => _icon;
        set { _icon = value; Notify(nameof(Icon), nameof(IconUrl), nameof(IconVisibility), nameof(DefaultIconVisibility)); }
    }

    /// <summary>可选：截图图片 URL。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Screenshot { get; set; }

    /// <summary>可选：保存的文件名，缺省从 Url 推导。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; set; }

    public string? IconUrl => string.IsNullOrEmpty(Icon) ? null : Icon;
    public Visibility IconVisibility => string.IsNullOrEmpty(Icon) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DefaultIconVisibility => string.IsNullOrEmpty(Icon) ? Visibility.Visible : Visibility.Collapsed;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                Notify(nameof(IsBusy), nameof(NotBusy), nameof(ProgressVisibility));
            }
        }
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText != value)
            {
                _progressText = value;
                Notify(nameof(ProgressText));
            }
        }
    }

    public bool NotBusy => !IsBusy;

    public string PrimaryButtonText => string.Equals(Type, "download", StringComparison.OrdinalIgnoreCase) ? "下载" : "打开官网";
    public Visibility DownloadButtonVisibility => string.Equals(Type, "download", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PageButtonVisibility => string.Equals(Type, "download", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    private void Notify(params string[] names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>一组工具。</summary>
public class ToolCatalogGroup
{
    public string Name { get; set; } = "";
    public List<ToolCatalogTool> Tools { get; set; } = new();
}

/// <summary>工具目录（分组 + 工具列表）。</summary>
public class ToolCatalog
{
    public List<ToolCatalogGroup> Groups { get; set; } = new();
}

/// <summary>
/// 工具目录的 JSON 加载/保存。
/// 配置文件位于软件运行目录 tools_catalog.json，可直接编辑新增工具。
/// </summary>
public static class ToolsCatalogStore
{
    public static string FilePath => Path.Combine(ToolPaths.BaseDir, "tools_catalog.json");

    public static ToolCatalog Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                File.WriteAllText(FilePath, DefaultJson());
                return Parse(DefaultJson());
            }
            return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception)
        {
            try { return Parse(DefaultJson()); }
            catch { return new ToolCatalog(); }
        }
    }

    /// <summary>保存配置到 JSON 文件（保留中文，缩进格式化）。</summary>
    public static void Save(ToolCatalog catalog)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(catalog, options));
    }

    private static ToolCatalog Parse(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<ToolCatalog>(json, options) ?? new ToolCatalog();
    }

    private static string DefaultJson() => @"{
  ""groups"": [
    {
      ""name"": ""JetBrains 系列"",
      ""tools"": [
        { ""name"": ""IntelliJ IDEA Ultimate"", ""description"": ""Java / Kotlin 旗舰 IDE"", ""wingetId"": ""JetBrains.IntelliJIDEA.Ultimate"", ""url"": ""https://www.jetbrains.com/idea/download/"", ""type"": ""page"" },
        { ""name"": ""PyCharm Professional"", ""description"": ""Python IDE"", ""wingetId"": ""JetBrains.PyCharm.Professional"", ""url"": ""https://www.jetbrains.com/pycharm/download/"", ""type"": ""page"" },
        { ""name"": ""WebStorm"", ""description"": ""前端 JavaScript / TypeScript IDE"", ""wingetId"": ""JetBrains.WebStorm"", ""url"": ""https://www.jetbrains.com/webstorm/download/"", ""type"": ""page"" },
        { ""name"": ""GoLand"", ""description"": ""Go IDE"", ""wingetId"": ""JetBrains.GoLand"", ""url"": ""https://www.jetbrains.com/go/download/"", ""type"": ""page"" },
        { ""name"": ""Rider"", ""description"": ""C# / .NET IDE"", ""wingetId"": ""JetBrains.Rider"", ""url"": ""https://www.jetbrains.com/rider/download/"", ""type"": ""page"" },
        { ""name"": ""CLion"", ""description"": ""C / C++ IDE"", ""wingetId"": ""JetBrains.CLion"", ""url"": ""https://www.jetbrains.com/clion/download/"", ""type"": ""page"" },
        { ""name"": ""DataGrip"", ""description"": ""数据库 IDE"", ""wingetId"": ""JetBrains.DataGrip"", ""url"": ""https://www.jetbrains.com/datagrip/download/"", ""type"": ""page"" }
      ]
    },
    {
      ""name"": ""微软系列"",
      ""tools"": [
        { ""name"": ""Visual Studio 2022 Community"", ""description"": ""微软主力 IDE（社区版）"", ""wingetId"": ""Microsoft.VisualStudio.2022.Community"", ""url"": ""https://visualstudio.microsoft.com/downloads/"", ""type"": ""page"" },
        { ""name"": ""VS Code"", ""description"": ""轻量跨平台代码编辑器"", ""wingetId"": ""Microsoft.VisualStudioCode"", ""url"": ""https://code.visualstudio.com/download"", ""type"": ""page"" },
        { ""name"": "".NET SDK"", ""description"": "".NET 运行时与 SDK"", ""wingetId"": ""Microsoft.DotNet.SDK.8"", ""url"": ""https://dotnet.microsoft.com/download"", ""type"": ""page"" },
        { ""name"": ""PowerShell 7"", ""description"": ""跨平台 PowerShell"", ""wingetId"": ""Microsoft.PowerShell"", ""url"": ""https://github.com/PowerShell/PowerShell/releases"", ""type"": ""page"" }
      ]
    },
    {
      ""name"": ""Git 工具"",
      ""tools"": [
        { ""name"": ""Git for Windows"", ""description"": ""Windows 版 Git"", ""wingetId"": ""Git.Git"", ""url"": ""https://git-scm.com/download/win"", ""type"": ""page"" },
        { ""name"": ""GitHub Desktop"", ""description"": ""GitHub 官方桌面客户端"", ""wingetId"": ""GitHub.GitHubDesktop"", ""url"": ""https://desktop.github.com/"", ""type"": ""page"" },
        { ""name"": ""Sourcetree"", ""description"": ""Atlassian 图形化 Git 客户端"", ""wingetId"": ""Atlassian.Sourcetree"", ""url"": ""https://www.sourcetreeapp.com/"", ""type"": ""page"" }
      ]
    },
    {
      ""name"": ""运行时 / 语言"",
      ""tools"": [
        { ""name"": ""Node.js LTS"", ""description"": ""JavaScript 运行时"", ""wingetId"": ""OpenJS.NodeJS.LTS"", ""url"": ""https://nodejs.org/"", ""type"": ""page"" },
        { ""name"": ""Python 3.12"", ""description"": ""Python 解释器"", ""wingetId"": ""Python.Python.3.12"", ""url"": ""https://www.python.org/downloads/"", ""type"": ""page"" },
        { ""name"": ""Go"", ""description"": ""Go 语言"", ""wingetId"": ""GoLang.Go"", ""url"": ""https://go.dev/dl/"", ""type"": ""page"" }
      ]
    },
    {
      ""name"": ""常用工具"",
      ""tools"": [
        { ""name"": ""7-Zip"", ""description"": ""压缩解压工具"", ""wingetId"": ""7zip.7zip"", ""url"": ""https://www.7-zip.org/download.html"", ""type"": ""page"" },
        { ""name"": ""Everything"", ""description"": ""极速文件搜索"", ""wingetId"": ""voidtools.Everything"", ""url"": ""https://www.voidtools.com/downloads/"", ""type"": ""page"" },
        { ""name"": ""DBeaver"", ""description"": ""通用数据库客户端"", ""wingetId"": ""DBeaver.DBeaver"", ""url"": ""https://dbeaver.io/download/"", ""type"": ""page"" },
        { ""name"": ""Postman"", ""description"": ""API 调试工具"", ""wingetId"": ""Postman.Postman"", ""url"": ""https://www.postman.com/downloads/"", ""type"": ""page"" }
      ]
    }
  ]
}";
}
