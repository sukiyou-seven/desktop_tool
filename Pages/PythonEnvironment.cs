using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace DesktopTool.Pages;

/// <summary>
/// 一个可管理的 Python 解释器环境（系统检测 + 用户自定义合并为同一列表）。
/// </summary>
public class PythonEnvironment : INotifyPropertyChanged
{
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsCustom { get; set; }
    public bool IsVenv { get; set; }
    public bool IsUv { get; set; }
    public DateTime? AddedAt { get; set; }

    /// <summary>徽标文字：默认 / venv / 自定义 / uv / 系统</summary>
    public string BadgeText => IsDefault ? "默认" : (IsCustom ? (IsVenv ? "venv" : "自定义") : (IsUv ? "uv" : "系统"));

    public string AddedAtText => AddedAt is null ? "" : $"添加于 {AddedAt:yyyy-MM-dd HH:mm}";

    /// <summary>删除按钮仅对自定义环境可见。</summary>
    public Visibility DeleteButtonVisibility => IsCustom ? Visibility.Visible : Visibility.Collapsed;

    private string _version = "";
    public string Version
    {
        get => _version;
        set
        {
            if (_version != value)
            {
                _version = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
