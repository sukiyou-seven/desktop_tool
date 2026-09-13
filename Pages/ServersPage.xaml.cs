using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DesktopTool.Pages;

/// <summary>
/// 服务器管理页：左侧服务器列表，右侧概览/终端/文件/数据库四个页签。
/// 通过 SSH 连接 Linux 服务器（Windows 启用 OpenSSH 后同样可用）。
/// </summary>
public sealed partial class ServersPage : Page
{
    private List<ServerInfo> _servers = new();
    private ServerInfo? _current;
    private SshClient? _ssh;
    private SftpClient? _sftp;
    private string _currentPath = "/";
    private DbType _currentDbType = DbType.MySql;
    private bool _connComboLoading;

    public ServersPage()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(ToolPaths.BaseDir, "servers_crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}");
            }
            catch { }
            throw;
        }
        Loaded += ServersPage_Loaded;
        Unloaded += ServersPage_Unloaded;
    }

    private void ServersPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _servers = ServerStore.Load();
            ServerListView.ItemsSource = _servers;
            // 默认选中 MongoDB
            SelectDbType(DbType.MongoDB);
        }
        catch (Exception ex)
        {
            ServerTitleText.Text = $"加载失败：{ex.Message}";
        }
    }

    private void ServersPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Disconnect();
    }

    private void Disconnect()
    {
        try { _ssh?.Disconnect(); } catch { }
        try { _ssh?.Dispose(); } catch { }
        try { _sftp?.Disconnect(); } catch { }
        try { _sftp?.Dispose(); } catch { }
        _ssh = null;
        _sftp = null;
    }

    // ---------- 服务器列表 ----------

    private async void AddServerBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "添加服务器",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };

        var panel = new StackPanel { Spacing = 8 };
        var nameBox = new TextBox { PlaceholderText = "名称（如：生产服务器）" };
        var hostBox = new TextBox { PlaceholderText = "主机地址（IP 或域名）" };
        var portBox = new TextBox { Text = "22", PlaceholderText = "端口" };
        var userBox = new TextBox { Text = "root", PlaceholderText = "用户名" };
        var passBox = new PasswordBox { PlaceholderText = "密码（或留空用密钥）" };
        var keyBox = new TextBox { PlaceholderText = "私钥文件路径（可选）" };
        var groupBox = new TextBox { Text = "默认", PlaceholderText = "分组" };
        var osCombo = new ComboBox { Header = "系统" };
        osCombo.Items.Add("Linux");
        osCombo.Items.Add("Windows");
        osCombo.SelectedIndex = 0;

        panel.Children.Add(nameBox);
        panel.Children.Add(hostBox);
        panel.Children.Add(portBox);
        panel.Children.Add(userBox);
        panel.Children.Add(passBox);
        panel.Children.Add(keyBox);
        panel.Children.Add(groupBox);
        panel.Children.Add(osCombo);
        dialog.Content = panel;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(hostBox.Text)) return;

        var server = new ServerInfo
        {
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? hostBox.Text.Trim() : nameBox.Text.Trim(),
            Host = hostBox.Text.Trim(),
            Port = int.TryParse(portBox.Text, out var p) ? p : 22,
            Username = string.IsNullOrWhiteSpace(userBox.Text) ? "root" : userBox.Text.Trim(),
            Password = passBox.Password,
            KeyFile = string.IsNullOrWhiteSpace(keyBox.Text) ? null : keyBox.Text.Trim(),
            Group = string.IsNullOrWhiteSpace(groupBox.Text) ? "默认" : groupBox.Text.Trim(),
            OsType = osCombo.SelectedIndex == 1 ? ServerOsType.Windows : ServerOsType.Linux,
        };
        _servers.Add(server);
        ServerStore.Save(_servers);
        ServerListView.ItemsSource = null;
        ServerListView.ItemsSource = _servers;
    }

    private async void DeleteServerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var confirm = new ContentDialog
        {
            Title = "删除服务器",
            Content = $"确定删除「{_current.Name}」？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        ServerStore.Delete(_current);
        _servers.Remove(_current);
        ServerStore.Save(_servers);
        Disconnect();
        _current = null;
        ServerListView.ItemsSource = null;
        ServerListView.ItemsSource = _servers;
        ResetDetail();
    }

    private async void ServerListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerListView.SelectedItem is not ServerInfo server) return;
        await ConnectServerAsync(server);
    }

    private async Task ConnectServerAsync(ServerInfo server)
    {
        Disconnect();
        _current = server;
        ServerTitleText.Text = server.Name;
        ConnStatusText.Text = "连接中…";
        DetailTabs.IsEnabled = false;
        RefreshBtn.IsEnabled = false;
        DeleteServerBtn.IsEnabled = true;

        try
        {
            await Task.Run(() =>
            {
                _ssh = SshService.Connect(server);
                _sftp = SshService.ConnectSftp(server);
            });
            ConnStatusText.Text = $"已连接 {server.Host}:{server.Port}";
            DetailTabs.IsEnabled = true;
            RefreshBtn.IsEnabled = true;
            DetailTabs.SelectedItem = DetailTabs.Items[0];
            RestoreDbConnection();
            await LoadOverviewAsync();
        }
        catch (Exception ex)
        {
            ConnStatusText.Text = $"连接失败：{ex.Message}";
            Disconnect();
        }
    }

    private void ResetDetail()
    {
        ServerTitleText.Text = "未选择服务器";
        ConnStatusText.Text = "";
        DetailTabs.IsEnabled = false;
        RefreshBtn.IsEnabled = false;
        DeleteServerBtn.IsEnabled = false;
        OverviewPanel.Visibility = Visibility.Collapsed;
        TerminalPanel.Visibility = Visibility.Collapsed;
        FilesPanel.Visibility = Visibility.Collapsed;
        DatabasePanel.Visibility = Visibility.Collapsed;
    }

    // ---------- 页签切换 ----------

    private void DetailTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanel.Visibility = tag == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = tag == "files" ? Visibility.Visible : Visibility.Collapsed;
        DatabasePanel.Visibility = tag == "database" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "files" && _sftp is not null)
            LoadFiles(_currentPath);
        if (tag == "database" && _ssh is not null)
            _ = DetectDatabaseAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        var tag = (DetailTabs.SelectedItem as SelectorBarItem)?.Tag as string;
        if (tag == "overview")
            await LoadOverviewAsync();
        else if (tag == "files" && _sftp is not null)
            LoadFiles(_currentPath);
        else if (tag == "database" && _ssh is not null)
            await DetectDatabaseAsync();
    }

    // ---------- 概览 ----------

    private async Task LoadOverviewAsync()
    {
        if (_ssh is null) return;
        try
        {
            var stats = await Task.Run(() => SshService.GetStats(_ssh));
            StatHostname.Text = stats.Hostname;
            StatUptime.Text = stats.Uptime;
            StatOs.Text = stats.OsName;
            StatKernel.Text = stats.Kernel;
            StatCpuModel.Text = stats.CpuModel;
            StatCpuCores.Text = $"{stats.CpuCores} 核";
            StatCpuUsage.Text = $"{stats.CpuUsagePercent}%";
            StatMemTotal.Text = $"{stats.MemoryTotalMb} MB";
            StatMemUsed.Text = $"已用 {stats.MemoryUsedMb} MB / 空闲 {stats.MemoryFreeMb} MB";
            StatMemUsage.Text = $"{stats.MemoryUsagePercent}%";
            DiskListView.ItemsSource = stats.Disks;
            NetListView.ItemsSource = stats.Networks;
            ProcListView.ItemsSource = stats.TopProcesses;
        }
        catch (Exception ex)
        {
            StatHostname.Text = $"采集失败：{ex.Message}";
        }
    }

    // ---------- 终端 ----------

    private void CommandInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            RunCommandBtn_Click(sender, e);
    }

    private async void RunCommandBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null || string.IsNullOrWhiteSpace(CommandInput.Text)) return;
        var cmd = CommandInput.Text.Trim();
        CommandInput.Text = "";
        TerminalOutput.Text += $"$ {cmd}\n";

        try
        {
            var (code, output, error) = await Task.Run(() => SshService.Execute(_ssh, cmd));
            if (!string.IsNullOrEmpty(output))
                TerminalOutput.Text += output + (output.EndsWith("\n") ? "" : "\n");
            if (!string.IsNullOrEmpty(error))
                TerminalOutput.Text += $"[stderr] {error}\n";
            TerminalOutput.Text += $"[exit={code}]\n\n";
        }
        catch (Exception ex)
        {
            TerminalOutput.Text += $"[error] {ex.Message}\n\n";
        }
        TerminalOutput.SelectionStart = TerminalOutput.Text.Length;
        TerminalOutput.SelectionLength = 0;
    }

    private void ClearTerminalBtn_Click(object sender, RoutedEventArgs e)
    {
        TerminalOutput.Text = "";
    }

    // ---------- 文件管理 ----------

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            GoPathBtn_Click(sender, e);
    }

    private void GoPathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(PathBox.Text))
            LoadFiles(PathBox.Text.Trim());
    }

    private void ParentDirBtn_Click(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_currentPath.TrimEnd('/'));
        if (string.IsNullOrEmpty(parent)) parent = "/";
        LoadFiles(parent);
    }

    private void LoadFiles(string path)
    {
        if (_sftp is null) return;
        try
        {
            var files = SshService.ListDirectory(_sftp, path);
            FileListView.ItemsSource = files;
            _currentPath = path;
            PathBox.Text = path;
        }
        catch (Exception ex)
        {
            TerminalOutput.Text += $"[文件] 读取失败：{ex.Message}\n";
        }
    }

    private void FileListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not RemoteFile file) return;
        if (file.IsDirectory)
            LoadFiles(file.FullPath);
        else
            _ = DownloadFileAsync(file);
    }

    private async void UploadFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp is null) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var remotePath = _currentPath.TrimEnd('/') + "/" + file.Name;
            await Task.Run(() => SshService.UploadFile(_sftp, file.Path, remotePath));
            LoadFiles(_currentPath);
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog { Title = "上传失败", Content = ex.Message, CloseButtonText = "OK", XamlRoot = XamlRoot };
            await dlg.ShowAsync();
        }
    }

    private async Task DownloadFileAsync(RemoteFile file)
    {
        if (_sftp is null) return;
        var picker = new FileSavePicker();
        picker.SuggestedFileName = file.Name;
        picker.FileTypeChoices.Add("所有文件", new List<string> { "." });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var storageFile = await picker.PickSaveFileAsync();
        if (storageFile is null) return;

        try
        {
            await Task.Run(() => SshService.DownloadFile(_sftp, file.FullPath, storageFile.Path));
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog { Title = "下载失败", Content = ex.Message, CloseButtonText = "OK", XamlRoot = XamlRoot };
            await dlg.ShowAsync();
        }
    }

    private async void NewFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp is null) return;
        var input = new TextBox { PlaceholderText = "文件夹名称" };
        var dlg = new ContentDialog
        {
            Title = "新建文件夹",
            Content = input,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(input.Text)) return;

        try
        {
            var newPath = _currentPath.TrimEnd('/') + "/" + input.Text.Trim();
            await Task.Run(() => SshService.CreateDirectory(_sftp, newPath));
            LoadFiles(_currentPath);
        }
        catch (Exception ex)
        {
            var err = new ContentDialog { Title = "创建失败", Content = ex.Message, CloseButtonText = "OK", XamlRoot = XamlRoot };
            await err.ShowAsync();
        }
    }

    // ---------- 数据库管理 ----------

    private DbConnection GetDbConnection()
    {
        return new DbConnection
        {
            Host = string.IsNullOrWhiteSpace(DbHostBox.Text) ? "127.0.0.1" : DbHostBox.Text.Trim(),
            Port = int.TryParse(DbPortBox.Text, out var p) ? p : DatabaseManager.DefaultPort(_currentDbType),
            Username = string.IsNullOrWhiteSpace(DbUserBox.Text) ? DatabaseManager.DefaultUser(_currentDbType) : DbUserBox.Text.Trim(),
            Password = DbPassBox.Password,
            AuthSource = string.IsNullOrWhiteSpace(DbAuthSourceBox.Text) ? "admin" : DbAuthSourceBox.Text.Trim(),
        };
    }

    /// <summary>加载已保存的连接配置到下拉框，并填入第一份（如果有）。</summary>
    private void RestoreDbConnection()
    {
        if (_current is null || DbConnCombo is null || DbHostBox is null) return;
        _connComboLoading = true;
        try
        {
            var list = DbConnectionStore.ListAll(_current.Id, _currentDbType);
            DbConnCombo.Items.Clear();
            foreach (var c in list)
                DbConnCombo.Items.Add(c);
            if (list.Count > 0)
            {
                DbConnCombo.SelectedIndex = 0;
                ApplyConnection(list[0]);
            }
            else
            {
                DbHostBox.Text = "127.0.0.1";
                DbPortBox.Text = "";
                DbUserBox.Text = "";
                DbPassBox.Password = "";
                DbAuthSourceBox.Text = "admin";
            }
        }
        finally { _connComboLoading = false; }
    }

    private void ApplyConnection(DbSavedConnection c)
    {
        DbHostBox.Text = c.Host;
        DbPortBox.Text = c.Port > 0 ? c.Port.ToString() : "";
        DbUserBox.Text = c.Username;
        DbPassBox.Password = c.Password ?? "";
        DbAuthSourceBox.Text = string.IsNullOrEmpty(c.AuthSource) ? "admin" : c.AuthSource;
    }

    private void DbConnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbConnCombo.SelectedItem is DbSavedConnection c)
        {
            ApplyConnection(c);
            // 用户手动选择配置时自动连接刷新
            if (!_connComboLoading && _ssh is not null)
                _ = RefreshDbListAsync();
        }
    }

    private async void DbConnSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var nameBox = new TextBox { PlaceholderText = "配置名称（如：root@admin）" };
        var dialog = new ContentDialog
        {
            Title = "保存连接配置",
            Content = nameBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var conn = GetDbConnection();
        DbConnectionStore.Save(_current.Id, _currentDbType, new DbSavedConnection
        {
            Name = name,
            Host = conn.Host,
            Port = conn.Port,
            Username = conn.Username,
            Password = conn.Password,
            AuthSource = conn.AuthSource,
        });
        RestoreDbConnection();
        // 选中刚保存的
        foreach (var item in DbConnCombo.Items)
            if (item is DbSavedConnection c && c.Name == name)
            {
                DbConnCombo.SelectedItem = item;
                break;
            }
    }

    /// <summary>自动保存当前连接参数，名称为 ip+用户名。</summary>
    private void AutoSaveConnection()
    {
        if (_current is null) return;
        var conn = GetDbConnection();
        var name = $"{conn.Host}+{conn.Username}";
        DbConnectionStore.Save(_current.Id, _currentDbType, new DbSavedConnection
        {
            Name = name,
            Host = conn.Host,
            Port = conn.Port,
            Username = conn.Username,
            Password = conn.Password,
            AuthSource = conn.AuthSource,
        });
        // 刷新下拉框并选中刚保存的（加锁防止触发 SelectionChanged 导致循环）
        _connComboLoading = true;
        try
        {
            DbConnCombo.Items.Clear();
            foreach (var c in DbConnectionStore.ListAll(_current.Id, _currentDbType))
                DbConnCombo.Items.Add(c);
            foreach (var item in DbConnCombo.Items)
                if (item is DbSavedConnection c && c.Name == name)
                {
                    DbConnCombo.SelectedItem = item;
                    break;
                }
        }
        finally { _connComboLoading = false; }
    }

    private void DbConnDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || DbConnCombo.SelectedItem is not DbSavedConnection c) return;
        DbConnectionStore.Delete(_current.Id, _currentDbType, c.Name);
        RestoreDbConnection();
    }

    private void DbTypeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn || btn.Tag is not string tag) return;
        if (Enum.TryParse<DbType>(tag, out var t))
            SelectDbType(t);
    }

    private void SelectDbType(DbType t)
    {
        _currentDbType = t;
        // 同步按钮选中状态
        if (DbTypePanel is not null)
        {
            foreach (var child in DbTypePanel.Children)
                if (child is ToggleButton tb)
                    tb.IsChecked = (tb.Tag as string == t.ToString());
        }
        if (DbPortBox is not null) DbPortBox.PlaceholderText = DatabaseManager.DefaultPort(t).ToString();
        if (DbUserBox is not null) DbUserBox.PlaceholderText = DatabaseManager.DefaultUser(t);
        UpdateConnectionParamVisibility(t);
        RestoreDbConnection();
        if (DbResultText is not null) DbResultText.Text = "";
        if (DbListView is not null) DbListView.ItemsSource = null;
    }

    private void UpdateConnectionParamVisibility(DbType t)
    {
        if (DbUserBox is null || DbAuthSourceBox is null) return;
        // MongoDB: 显示全部；MySQL/PG: 隐藏认证库；Redis: 隐藏用户名和认证库；SQLite: 隐藏用户/密码/认证库
        DbUserBox.Visibility = (t == DbType.Redis || t == DbType.SQLite) ? Visibility.Collapsed : Visibility.Visible;
        DbAuthSourceBox.Visibility = (t == DbType.MongoDB) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DetectDatabaseAsync()
    {
        if (_ssh is null) return;
        DbStatusText.Text = "检测中…";
        try
        {
            var installed = await Task.Run(() => DatabaseManager.IsInstalled(_ssh, _currentDbType));
            var portOpen = await Task.Run(() => DatabaseManager.IsPortListening(_ssh, DatabaseManager.DefaultPort(_currentDbType)));

            if (!installed && !portOpen)
            {
                DbStatusText.Text = "未检测到安装";
                SetDbButtons(installed: false, running: false);
                return;
            }

            var running = portOpen || await Task.Run(() => DatabaseManager.IsRunning(_ssh, _currentDbType));
            if (installed && running)
                DbStatusText.Text = "已安装 · 运行中";
            else if (installed)
                DbStatusText.Text = "已安装 · 已停止";
            else if (portOpen)
                DbStatusText.Text = "端口在监听（非标准安装）";
            SetDbButtons(installed: installed || portOpen, running: running);
            await RefreshDbListAsync();
        }
        catch (Exception ex)
        {
            DbStatusText.Text = $"检测失败：{ex.Message}";
        }
    }

    private void SetDbButtons(bool installed, bool running)
    {
        DbInstallBtn.IsEnabled = !installed;
        DbUninstallBtn.IsEnabled = installed;
        DbStartBtn.IsEnabled = installed && !running;
        DbStopBtn.IsEnabled = installed && running;
        DbRestartBtn.IsEnabled = installed && running;
    }

    private async void DbDetectBtn_Click(object sender, RoutedEventArgs e)
    {
        await DetectDatabaseAsync();
    }

    private async void DbDetectHostBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        try
        {
            var ip = await Task.Run(() => DatabaseManager.DetectHostIp(_ssh));
            DbHostBox.Text = ip;
            DbResultText.Text = ip == "127.0.0.1"
                ? "已填入 127.0.0.1（非 WSL 环境）"
                : $"已填入 Windows 主机 IP：{ip}（WSL2 环境，用这个 IP 连 Windows 端服务）";
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"探测失败：{ex.Message}";
        }
    }

    private async void DbTestConnBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        DbResultText.Text = "测试中…";
        try
        {
            var conn = GetDbConnection();
            var result = await Task.Run(() => DatabaseManager.TestConnection(_ssh, _currentDbType, conn));
            DbResultText.Text = result;
            // 连接成功后自动保存（结果包含"成功"且不含"失败"）
            if (result.Contains("成功") && !result.Contains("失败"))
            {
                AutoSaveConnection();
                DbResultText.Text = result + "（已自动保存配置）";
            }
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"测试失败：{ex.GetType().Name}: {ex.Message?.Replace('\r', ' ').Replace('\n', ' ')}";
        }
    }

    private async void DbInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        DbResultText.Text = "安装中（可能需要几分钟）…";
        DbInstallBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => DatabaseManager.Install(_ssh, _currentDbType));
            DbResultText.Text = result;
            await DetectDatabaseAsync();
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"安装失败：{ex.Message}";
            DbInstallBtn.IsEnabled = true;
        }
    }

    private async void DbUninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        var confirm = new ContentDialog
        {
            Title = "卸载数据库",
            Content = $"确定卸载 {_currentDbType}？数据可能丢失。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        DbResultText.Text = "卸载中…";
        try
        {
            var result = await Task.Run(() => DatabaseManager.Uninstall(_ssh, _currentDbType));
            DbResultText.Text = result;
            await DetectDatabaseAsync();
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"卸载失败：{ex.Message}";
        }
    }

    private async void DbStartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        DbResultText.Text = await Task.Run(() => DatabaseManager.Start(_ssh, _currentDbType));
        await DetectDatabaseAsync();
    }

    private async void DbStopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        DbResultText.Text = await Task.Run(() => DatabaseManager.Stop(_ssh, _currentDbType));
        await DetectDatabaseAsync();
    }

    private async void DbRestartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        DbResultText.Text = await Task.Run(() => DatabaseManager.Restart(_ssh, _currentDbType));
        await DetectDatabaseAsync();
    }

    private async void DbRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDbListAsync();
    }

    private static readonly HashSet<string> SystemDbs = new(StringComparer.OrdinalIgnoreCase)
    {
        // MySQL
        "information_schema", "mysql", "performance_schema", "sys",
        // PostgreSQL
        "postgres", "template0", "template1",
        // MongoDB
        "admin", "local", "config",
    };

    private async Task RefreshDbListAsync()
    {
        if (_ssh is null) return;
        try
        {
            var conn = GetDbConnection();
            var list = await Task.Run(() => DatabaseManager.ListDatabases(_ssh, _currentDbType, conn));
            // 屏蔽系统库
            list = list.Where(d => !SystemDbs.Contains(d.Name)).ToList();
            DbListView.ItemsSource = list;
            DbResultText.Text = $"共 {list.Count} 个数据库";
            // 列表读取成功说明连接可用，自动保存（排除查询失败的情况）
            if (list.Count > 0 && list[0].Name != "查询失败" && list[0].Name != "读取失败")
                AutoSaveConnection();
            // 自动选中第一个数据库，联动加载该库下的用户
            if (list.Count > 0 && DbListView.SelectedItem is null)
            {
                DbListView.SelectedIndex = 0;
            }
            else if (DbListView.SelectedItem is DbEntry sel && !string.IsNullOrEmpty(sel.Name))
            {
                await LoadDbUsersAsync(conn, sel.Name);
            }
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"读取列表失败：{ex.GetType().Name}: {ex.Message?.Replace('\r', ' ').Replace('\n', ' ')}";
        }
    }

    private async Task LoadDbUsersAsync(DbConnection? conn = null, string? database = null)
    {
        if (_ssh is null) return;
        try
        {
            conn ??= GetDbConnection();
            var users = await Task.Run(() => DatabaseManager.ListUsers(_ssh, _currentDbType, conn, database));
            DbChangeUserBox.Items.Clear();
            foreach (var u in users)
                DbChangeUserBox.Items.Add(u);
            // 自动填入第一个用户
            if (users.Count > 0)
                DbChangeUserBox.Text = users[0];
            else
                DbChangeUserBox.Text = "";
        }
        catch { }
    }

    private void DbListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbListView.SelectedItem is DbEntry entry && !string.IsNullOrEmpty(entry.Name))
        {
            DbUserTargetText.Text = $"当前库：{entry.Name}";
            _ = LoadDbUsersAsync(database: entry.Name);
        }
        else
        {
            DbUserTargetText.Text = "当前库：未选择";
        }
    }

    private async void DbCreateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null || string.IsNullOrWhiteSpace(DbNewNameBox.Text)) return;
        var name = DbNewNameBox.Text.Trim();
        DbResultText.Text = "创建中…";
        try
        {
            var conn = GetDbConnection();
            var result = await Task.Run(() => DatabaseManager.CreateDatabase(_ssh, _currentDbType, name, conn));
            DbResultText.Text = string.IsNullOrEmpty(result) ? $"已创建：{name}" : result;
            DbNewNameBox.Text = "";
            await RefreshDbListAsync();
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"创建失败：{ex.GetType().Name}: {ex.Message?.Replace('\r', ' ').Replace('\n', ' ')}";
        }
    }

    private async void DbDropBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null || DbListView.SelectedItem is not DbEntry entry) return;
        var confirm = new ContentDialog
        {
            Title = "删除数据库",
            Content = $"确定删除「{entry.Name}」？数据将丢失。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        DbResultText.Text = "删除中…";
        try
        {
            var conn = GetDbConnection();
            var result = await Task.Run(() => DatabaseManager.DropDatabase(_ssh, _currentDbType, entry.Name, conn));
            DbResultText.Text = string.IsNullOrEmpty(result) ? $"已删除：{entry.Name}" : result;
            await RefreshDbListAsync();
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"删除失败：{ex.GetType().Name}: {ex.Message?.Replace('\r', ' ').Replace('\n', ' ')}";
        }
    }

    private async void DbChangePassBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        var user = string.IsNullOrWhiteSpace(DbChangeUserBox.Text) ? DatabaseManager.DefaultUser(_currentDbType) : DbChangeUserBox.Text.Trim();
        var newPass = DbNewPassBox.Password;
        if (string.IsNullOrEmpty(newPass))
        {
            DbResultText.Text = "请输入新密码";
            return;
        }
        DbResultText.Text = "修改中…";
        try
        {
            var conn = GetDbConnection();
            var selectedDb = DbListView.SelectedItem as DbEntry;
            var targetDb = selectedDb is not null && !string.IsNullOrEmpty(selectedDb.Name) ? selectedDb.Name : null;
            if (_currentDbType == DbType.MongoDB && string.IsNullOrEmpty(targetDb))
            {
                DbResultText.Text = "请先在左侧数据库列表中选中一个数据库";
                return;
            }
            var result = await Task.Run(() => DatabaseManager.ChangePassword(_ssh, _currentDbType, user, newPass, conn, targetDb));
            DbResultText.Text = string.IsNullOrEmpty(result) ? "密码已修改" : result;
            DbNewPassBox.Password = "";
        }
        catch (Exception ex)
        {
            DbResultText.Text = $"改密失败：{ex.GetType().Name}: {ex.Message?.Replace('\r', ' ').Replace('\n', ' ')}";
        }
    }
}
