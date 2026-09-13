using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DesktopTool.Pages;

/// <summary>
/// 工程卡片视图模型：承载该工程独立的 npm 执行 / 上传状态
/// （工作目录、命令下拉、输出、服务器、上传进度），供卡片内联控件绑定。
/// </summary>
public class ProjectCardVm : INotifyPropertyChanged
{
    private string _npmDir = "";
    private string _selectedNpmCommand = "";
    private string _npmStatus = "选择目录后自动获取命令";
    private string _output = "";
    private bool _busy;
    private ServerInfo? _selectedServerLink;
    private bool _uploadBusy;
    private string _uploadStatus = "选择服务器后可上传";
    private string _localUploadPath = "";
    private string _remoteUploadDir = "";
    private bool _autoExclude = true;
    private string _customExcludesText = "";
    private string _uploadLog = "";
    private double _uploadPercent;

    public ProjectCardVm(Project project)
    {
        Project = project;
        // 恢复上次的工作目录（未设置过则用工程路径）
        NpmDir = string.IsNullOrWhiteSpace(project.NpmDir) ? project.Path : project.NpmDir;
        SelectedNpmCommand = project.NpmCommand ?? "";
        // 恢复上传配置
        LocalUploadPath = string.IsNullOrWhiteSpace(project.UploadLocalPath) ? project.Path : project.UploadLocalPath;
        RemoteUploadDir = project.UploadRemoteDir ?? "";
        CustomExcludesText = project.UploadCustomExcludes ?? "";
    }

    public Project Project { get; }

    public ObservableCollection<string> NpmCommands { get; } = new();

    public ObservableCollection<ServerInfo> ServerLinks { get; } = new();

    public string NpmDir
    {
        get => _npmDir;
        set => Set(ref _npmDir, value);
    }

    public string SelectedNpmCommand
    {
        get => _selectedNpmCommand;
        set => Set(ref _selectedNpmCommand, value);
    }

    public string NpmStatus
    {
        get => _npmStatus;
        set => Set(ref _npmStatus, value);
    }

    public string Output
    {
        get => _output;
        set => Set(ref _output, value);
    }

    public bool Busy
    {
        get => _busy;
        set => Set(ref _busy, value);
    }

    public ServerInfo? SelectedServerLink
    {
        get => _selectedServerLink;
        set => Set(ref _selectedServerLink, value);
    }

    public bool UploadBusy
    {
        get => _uploadBusy;
        set => Set(ref _uploadBusy, value);
    }

    public string UploadStatus
    {
        get => _uploadStatus;
        set => Set(ref _uploadStatus, value);
    }

    public string LocalUploadPath
    {
        get => _localUploadPath;
        set => Set(ref _localUploadPath, value);
    }

    public string RemoteUploadDir
    {
        get => _remoteUploadDir;
        set => Set(ref _remoteUploadDir, value);
    }

    public bool AutoExclude
    {
        get => _autoExclude;
        set => Set(ref _autoExclude, value);
    }

    public string CustomExcludesText
    {
        get => _customExcludesText;
        set => Set(ref _customExcludesText, value);
    }

    public string UploadLog
    {
        get => _uploadLog;
        set => Set(ref _uploadLog, value);
    }

    public double UploadPercent
    {
        get => _uploadPercent;
        set => Set(ref _uploadPercent, value);
    }

    /// <summary>从服务器管理的数据中恢复该工程上次选择的服务器。</summary>
    public void RestoreSelectedServer()
    {
        SelectedServerLink = ServerLinks.FirstOrDefault(s => s.Id == Project.ServerLinkId);
    }

    // ---- 转发 Project 字段供卡片绑定 ----

    public string Name => Project.Name;
    public string Type => Project.Type;
    public string Framework => Project.Framework;
    public string Path => Project.Path;
    public string TypeIcon => Project.TypeIcon;
    public string CreatedAtText => Project.CreatedAtText;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
