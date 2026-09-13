using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DesktopTool.Pages;

/// <summary>一个远端仓库的分支条目（点击复制克隆命令）。</summary>
public class GitBranchItem
{
    public string BranchName { get; set; } = "";
    public string CopyText { get; set; } = "";
}

/// <summary>一个远端仓库条目（UI 展示模型）。</summary>
public class GitRepoItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Description { get; set; } = "";
    public string Language { get; set; } = "";
    public bool IsPrivate { get; set; }
    public string MetaText { get; set; } = "";
    public string CloneUrl { get; set; } = "";

    public ObservableCollection<GitBranchItem> Branches { get; } = new();

    private bool _isLoadingBranches;
    public bool IsLoadingBranches
    {
        get => _isLoadingBranches;
        set
        {
            if (_isLoadingBranches != value)
            {
                _isLoadingBranches = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingBranches)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BranchStatusText)));
            }
        }
    }

    public string BranchStatusText =>
        IsLoadingBranches
            ? "加载中…"
            : Branches.Count > 0 ? $"{Branches.Count} 个分支" : "点击加载分支";

    public Visibility BranchesVisibility => Branches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void NotifyBranchUi()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BranchesVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BranchStatusText)));
    }

    public Visibility PrivateVisibility => IsPrivate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LangVisibility => string.IsNullOrEmpty(Language) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Git 设置页：登录 GitHub / Gitee，凭据全局保存到 Windows 凭据管理器（git 命令行可复用），
/// 登录后拉取并展示仓库列表与分支。
/// </summary>
public sealed partial class GitPage : Page
{
    private readonly ObservableCollection<GitRepoItem> _repos = new();
    private string _platform = "github";
    private string _serverUrl = "";
    private string _apiType = "gitea";
    private string? _userName;
    private string? _token;
    private bool _busy;

    public GitPage()
    {
        InitializeComponent();
        RepoList.ItemsSource = _repos;
        Loaded += GitPage_Loaded;
    }

    /// <summary>GitHub / Gitee 的固定主机名。</summary>
    private static string DefaultHost(string platform) => platform switch
    {
        "gitee" => "gitee.com",
        _ => "github.com",
    };

    /// <summary>自定义服务器地址（规范化，自动补 https://）。</summary>
    private string BaseUrl
    {
        get
        {
            var url = _serverUrl.Trim().TrimEnd('/');
            if (url.Length > 0 && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            return url;
        }
    }

    private string Host
    {
        get
        {
            if (_platform != "custom") return DefaultHost(_platform);
            var url = BaseUrl;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Authority;
            return url;
        }
    }

    private string CredentialTarget
    {
        get
        {
            if (_platform != "custom") return WindowsCredentialStore.GitTarget(Host);
            var url = BaseUrl;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return $"git:{uri.Scheme}://{uri.Authority}";
            return $"git:{url}";
        }
    }

    private void GitPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 恢复自定义服务器配置
        var cfg = GitSettings.Load();
        _serverUrl = cfg.ServerUrl;
        _apiType = string.IsNullOrEmpty(cfg.ApiType) ? "gitea" : cfg.ApiType;
        ServerUrlBox.Text = _serverUrl;
        foreach (var obj in ApiTypeCombo.Items)
        {
            if (obj is ComboBoxItem it && it.Tag is string tag && tag == _apiType)
            {
                ApiTypeCombo.SelectedItem = it;
                break;
            }
        }
        RefreshLoginState();
    }

    /// <summary>从系统凭据管理器读取已保存的登录信息并更新界面。</summary>
    private void RefreshLoginState()
    {
        if (WindowsCredentialStore.TryRead(CredentialTarget, out var user, out var secret))
        {
            _userName = user;
            _token = secret;
            StatusText.Text = $"已登录：{user}（凭据已全局保存，git 命令行可直接使用）";
            LoginBtn.Content = "更新登录";
            LogoutBtn.Visibility = Visibility.Visible;
        }
        else
        {
            _userName = null;
            _token = null;
            StatusText.Text = "未登录（输入 Token 后点击“登录并保存”）";
            LoginBtn.Content = "登录并保存";
            LogoutBtn.Visibility = Visibility.Collapsed;
        }
        UpdateLoginBtnEnabled();
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateLoginBtnEnabled();

    private void UpdateLoginBtnEnabled()
    {
        LoginBtn.IsEnabled = !_busy && TokenBox.Password.Length > 0;
    }

    private void PlatformBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item || item.Tag is not string tag) return;
        if (tag == _platform) return;
        _platform = tag;
        _repos.Clear();
        RepoCountText.Text = "";
        CustomPanel.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "custom")
        {
            _serverUrl = ServerUrlBox.Text;
            TokenBox.PlaceholderText = "输入访问令牌（如 Gitea Access Token / GitLab Personal Access Token）";
        }
        else
        {
            TokenBox.PlaceholderText = "输入 Personal Access Token（需 repo / projects 权限）";
        }
        RefreshLoginState();
    }

    private async void LoginBtn_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0) return;
        if (_platform == "custom" && ServerUrlBox.Text.Trim().Length == 0)
        {
            StatusText.Text = "请先填写服务器地址";
            return;
        }
        if (_platform == "custom")
        {
            // 保存自定义服务器配置
            _serverUrl = ServerUrlBox.Text;
            if (ApiTypeCombo.SelectedItem is ComboBoxItem apiItem && apiItem.Tag is string apiTag)
                _apiType = apiTag;
            GitSettings.Save(new GitCustomConfig { ServerUrl = _serverUrl, ApiType = _apiType });
        }
        SetBusy(true);
        try
        {
            var user = await VerifyAndGetUserAsync(token);
            _userName = user;
            _token = token;
            WindowsCredentialStore.Save(CredentialTarget, user, token);
            TokenBox.Password = "";
            StatusText.Text = $"登录成功：{user}（凭据已保存到 Windows 凭据管理器）";
            LoginBtn.Content = "更新登录";
            LogoutBtn.Visibility = Visibility.Visible;
            await LoadReposAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"登录失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "退出登录",
            Content = $"将删除保存在 Windows 凭据管理器中的 {Host} 凭据。\n删除后 git 命令行将无法再自动使用该凭据。",
            PrimaryButtonText = "退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        WindowsCredentialStore.Delete(CredentialTarget);
        _token = null;
        _userName = null;
        _repos.Clear();
        RepoCountText.Text = "";
        RefreshLoginState();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await LoadReposAsync();

    private void CopyUrlBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not GitRepoItem item) return;
        var package = new DataPackage();
        package.SetText(item.CloneUrl);
        Clipboard.SetContent(package);
        StatusText.Text = $"已复制克隆地址：{item.CloneUrl}";
    }

    /// <summary>点击“分支”按钮：已加载则收起，未加载则拉取分支列表。</summary>
    private async void BranchesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not GitRepoItem item) return;
        if (item.IsLoadingBranches) return;

        if (item.Branches.Count > 0)
        {
            item.Branches.Clear();
            item.NotifyBranchUi();
            return;
        }
        await LoadBranchesAsync(item);
    }

    /// <summary>点击某个分支：复制“带分支的克隆命令（目录重命名为 项目名_分支名）”。</summary>
    private void BranchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not GitBranchItem branch) return;
        var package = new DataPackage();
        package.SetText(branch.CopyText);
        Clipboard.SetContent(package);
        StatusText.Text = $"已复制克隆命令：{branch.CopyText}";
    }

    private async Task LoadBranchesAsync(GitRepoItem item)
    {
        if (_token is null) return;
        item.IsLoadingBranches = true;
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            string json;
            if (_platform == "github")
            {
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SeraphineAITool", "1.0"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                json = await client.GetStringAsync(
                    $"https://api.github.com/repos/{item.Owner}/{item.Name}/branches");
            }
            else if (_platform == "gitee")
            {
                json = await client.GetStringAsync(
                    $"https://gitee.com/api/v5/repos/{item.Owner}/{item.Name}/branches?access_token={Uri.EscapeDataString(_token)}");
            }
            else if (_apiType == "gitlab")
            {
                client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", _token);
                var path = Uri.EscapeDataString($"{item.Owner}/{item.Name}");
                json = await client.GetStringAsync($"{BaseUrl}/api/v4/projects/{path}/repository/branches");
            }
            else
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _token);
                json = await client.GetStringAsync($"{BaseUrl}/api/v1/repos/{item.Owner}/{item.Name}/branches");
            }

            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var branchName = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? ""
                    : "";
                if (branchName.Length == 0) continue;

                item.Branches.Add(new GitBranchItem
                {
                    BranchName = branchName,
                    CopyText = $"git clone -b {branchName} {item.CloneUrl} {item.Name}_{branchName}",
                });
            }

            if (item.Branches.Count == 0)
                StatusText.Text = $"仓库 {item.Name} 没有可显示的分支";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载分支失败：{ex.Message}";
        }
        finally
        {
            item.IsLoadingBranches = false;
            item.NotifyBranchUi();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        RefreshBtn.IsEnabled = !busy;
        LogoutBtn.IsEnabled = !busy;
        UpdateLoginBtnEnabled();
    }

    /// <summary>用 Token 调用平台 /user 接口验证并取回登录名。</summary>
    private async Task<string> VerifyAndGetUserAsync(string token)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        string json;
        if (_platform == "github")
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SeraphineAITool", "1.0"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            json = await client.GetStringAsync("https://api.github.com/user");
        }
        else if (_platform == "gitee")
        {
            json = await client.GetStringAsync(
                $"https://gitee.com/api/v5/user?access_token={Uri.EscapeDataString(token)}");
        }
        else if (_apiType == "gitlab")
        {
            client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
            json = await client.GetStringAsync($"{BaseUrl}/api/v4/user");
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
            json = await client.GetStringAsync($"{BaseUrl}/api/v1/user");
        }

        var loginProp = _platform == "custom" && _apiType == "gitlab" ? "username" : "login";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(loginProp, out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() ?? "?"
            : "?";
    }

    private async Task LoadReposAsync()
    {
        if (_token is null)
        {
            StatusText.Text = "未登录，请先登录后再拉取仓库";
            return;
        }
        SetBusy(true);
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            string json;
            if (_platform == "github")
            {
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SeraphineAITool", "1.0"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                json = await client.GetStringAsync("https://api.github.com/user/repos?per_page=100&sort=updated");
            }
            else if (_platform == "gitee")
            {
                json = await client.GetStringAsync(
                    $"https://gitee.com/api/v5/user/repos?access_token={Uri.EscapeDataString(_token)}&per_page=100&sort=updated");
            }
            else if (_apiType == "gitlab")
            {
                client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", _token);
                json = await client.GetStringAsync(
                    $"{BaseUrl}/api/v4/projects?membership=true&per_page=100&order_by=last_activity_at");
            }
            else
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _token);
                json = await client.GetStringAsync($"{BaseUrl}/api/v1/user/repos?limit=100");
            }

            var list = ParseRepos(json, isGitee: _platform == "gitee", isGitLab: _platform == "custom" && _apiType == "gitlab");
            _repos.Clear();
            foreach (var r in list) _repos.Add(r);
            RepoCountText.Text = $"共 {_repos.Count} 个仓库";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"拉取仓库失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static List<GitRepoItem> ParseRepos(string json, bool isGitee, bool isGitLab = false)
    {
        var list = new List<GitRepoItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array ? dataEl : default;

        if (arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var el in arr.EnumerateArray())
        {
            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            if (name.Length == 0) continue;

            // owner：GitLab 用 owner.username 或 path_with_namespace；其余用 owner.login / full_name
            var owner = "";
            if (el.TryGetProperty("owner", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.Object)
            {
                var ownerKey = isGitLab ? "username" : "login";
                if (ownerEl.TryGetProperty(ownerKey, out var ownerLogin) && ownerLogin.ValueKind == JsonValueKind.String)
                    owner = ownerLogin.GetString() ?? "";
            }
            if (owner.Length == 0)
            {
                var fullKey = isGitLab ? "path_with_namespace" : "full_name";
                if (el.TryGetProperty(fullKey, out var fn) && fn.ValueKind == JsonValueKind.String)
                {
                    var parts = (fn.GetString() ?? "").Split('/');
                    if (parts.Length == 2) owner = parts[0];
                }
            }

            var desc = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            var lang = "";
            if (!isGitLab && el.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String)
                lang = l.GetString() ?? "";

            var isPrivate = false;
            if (isGitLab)
            {
                if (el.TryGetProperty("visibility", out var vis) && vis.ValueKind == JsonValueKind.String)
                    isPrivate = string.Equals(vis.GetString(), "private", StringComparison.OrdinalIgnoreCase);
            }
            else if (el.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True)
            {
                isPrivate = true;
            }

            var updated = "";
            if (el.TryGetProperty(isGitLab ? "last_activity_at" : "updated_at", out var u) && u.ValueKind == JsonValueKind.String)
                updated = u.GetString() ?? "";

            string cloneUrl;
            if (isGitLab)
            {
                cloneUrl = el.TryGetProperty("http_url_to_repo", out var hr) && hr.ValueKind == JsonValueKind.String
                    ? hr.GetString() ?? "" : "";
            }
            else if (el.TryGetProperty("clone_url", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(c.GetString()))
            {
                cloneUrl = c.GetString()!;
            }
            else if (el.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String)
            {
                cloneUrl = (h.GetString() ?? "") + (isGitee ? ".git" : "");
            }
            else
            {
                cloneUrl = "";
            }

            var updatedText = DateTime.TryParse(updated, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : updated;
            var meta = string.IsNullOrEmpty(updatedText)
                ? cloneUrl
                : $"更新于 {updatedText}  ·  {cloneUrl}";

            list.Add(new GitRepoItem
            {
                Name = name,
                Owner = owner,
                Description = desc,
                Language = lang,
                IsPrivate = isPrivate,
                MetaText = meta,
                CloneUrl = cloneUrl,
            });
        }
        return list;
    }
}
