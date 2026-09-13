using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopTool.Pages;

/// <summary>
/// 域名管理页：支持阿里云云解析与 Cloudflare，列出域名、管理 DNS 解析记录（增删改查）。
/// 凭证分别存入本机 Windows 凭据管理器（安全存储，不上云）。
/// </summary>
public sealed partial class AlidnsPage : Page
{
    private const string AkCredTarget = "seraphine:alidns";
    private const string CfCredTarget = "seraphine:cloudflare";

    private static readonly string[] RecordTypes = { "A", "AAAA", "CNAME", "MX", "TXT", "SRV", "NS", "CAA", "PTR" };

    public AlidnsPage()
    {
        InitializeComponent();
        Loaded += AlidnsPage_Loaded;
    }

    private void AlidnsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 恢复上次保存的凭证
        if (WindowsCredentialStore.TryRead(AkCredTarget, out var akId, out var akSecret))
        {
            AkIdBox.Text = akId ?? "";
            AkSecretBox.Password = akSecret ?? "";
        }
        if (WindowsCredentialStore.TryRead(CfCredTarget, out var cfId, out var cfSecret))
        {
            // Cloudflare 用 Token 认证，userName 存 "token"，secret 存 Token 本体
            if (cfSecret is { Length: > 0 })
                CfTokenBox.Password = cfSecret;
        }
    }

    private string SelectedProvider
        => (DnsProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "alidns";

    private bool IsCloudflare => SelectedProvider == "cloudflare";

    // ---------------- 服务商切换 ----------------

    private void DnsProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 解析期 ComboBoxItem 的 IsSelected 会提前触发本事件，此时后续元素尚未创建，判空跳过
        if (AkCredPanel is null || CfCredPanel is null) return;

        var cf = IsCloudflare;
        AkCredPanel.Visibility = cf ? Visibility.Collapsed : Visibility.Visible;
        CfCredPanel.Visibility = cf ? Visibility.Visible : Visibility.Collapsed;

        // 清空旧服务商的域名与记录，避免串数据
        DomainCombo.ItemsSource = null;
        RecordList.ItemsSource = null;
        RecordEmptyText.Text = "暂无记录。";
        SetStatus("", false);
    }

    // ---------------- 凭证保存 ----------------

    private void SaveAkBtn_Click(object sender, RoutedEventArgs e)
    {
        var id = AkIdBox.Text.Trim();
        var secret = AkSecretBox.Password;
        if (id.Length == 0 || secret.Length == 0)
        {
            SetStatus("请填写 AccessKey ID 和 Secret", true);
            return;
        }
        WindowsCredentialStore.Save(AkCredTarget, id, secret);
        SetStatus("AccessKey 已保存到本机凭据管理器", false, true);
    }

    private void SaveTokenBtn_Click(object sender, RoutedEventArgs e)
    {
        var token = CfTokenBox.Password;
        if (token.Length == 0)
        {
            SetStatus("请填写 Cloudflare API Token", true);
            return;
        }
        WindowsCredentialStore.Save(CfCredTarget, "token", token);
        SetStatus("API Token 已保存到本机凭据管理器", false, true);
    }

    // ---------------- 域名 ----------------

    private async void LoadDomainsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (IsCloudflare)
        {
            var token = CfTokenBox.Password;
            if (token.Length == 0)
            {
                SetStatus("请填写 Cloudflare API Token", true);
                return;
            }
            WindowsCredentialStore.Save(CfCredTarget, "token", token);

            SetBusy(true);
            try
            {
                var client = new CloudflareDnsClient(token);
                var domains = await Task.Run(() => client.ListZonesAsync());
                if (domains.Count == 0)
                {
                    DomainCombo.ItemsSource = null;
                    DomainCombo.PlaceholderText = "未查询到域名";
                    SetStatus("账号下没有域名（zone）", true);
                    return;
                }
                DomainCombo.ItemsSource = domains;
                DomainCombo.DisplayMemberPath = "DomainName";
                DomainCombo.SelectedIndex = 0;
                SetStatus($"已加载 {domains.Count} 个域名（zone）", false, true);
            }
            catch (Exception ex)
            {
                SetStatus("加载失败：" + ex.Message, true);
            }
            finally
            {
                SetBusy(false);
            }
            return;
        }

        var id = AkIdBox.Text.Trim();
        var secret = AkSecretBox.Password;
        if (id.Length == 0 || secret.Length == 0)
        {
            SetStatus("请填写 AccessKey ID 和 Secret", true);
            return;
        }

        // 顺手保存，方便下次使用
        WindowsCredentialStore.Save(AkCredTarget, id, secret);

        SetBusy(true);
        try
        {
            var client = new AlidnsClient(id, secret);
            var domains = await Task.Run(() => client.DescribeDomainsAsync());
            if (domains.Count == 0)
            {
                DomainCombo.ItemsSource = null;
                DomainCombo.PlaceholderText = "未查询到域名";
                SetStatus("账号下没有域名", true);
                return;
            }
            DomainCombo.ItemsSource = domains;
            DomainCombo.DisplayMemberPath = "DomainName";
            DomainCombo.SelectedIndex = 0;
            SetStatus($"已加载 {domains.Count} 个域名", false, true);
        }
        catch (Exception ex)
        {
            SetStatus("加载失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------------- 记录列表 ----------------

    private void DomainCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DomainCombo.SelectedItem is AlidnsDomain domain)
        {
            _ = LoadRecordsAsync(domain);
        }
    }

    private void RefreshRecordsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DomainCombo.SelectedItem is AlidnsDomain domain)
        {
            _ = LoadRecordsAsync(domain);
        }
        else
        {
            SetStatus("请先加载并选择域名", true);
        }
    }

    private async Task LoadRecordsAsync(AlidnsDomain domain)
    {
        SetBusy(true);
        try
        {
            if (IsCloudflare)
            {
                var token = CfTokenBox.Password;
                if (token.Length == 0)
                {
                    SetStatus("请先填写 API Token", true);
                    return;
                }
                var client = new CloudflareDnsClient(token);
                var records = await Task.Run(() => client.ListRecordsAsync(domain.DomainId));
                RecordList.ItemsSource = records;
                RecordEmptyText.Text = records.Count == 0 ? "暂无记录。" : $"共 {records.Count} 条记录";
            }
            else
            {
                var id = AkIdBox.Text.Trim();
                var secret = AkSecretBox.Password;
                if (id.Length == 0 || secret.Length == 0)
                {
                    SetStatus("请先填写 AccessKey", true);
                    return;
                }
                var client = new AlidnsClient(id, secret);
                var records = await Task.Run(() => client.DescribeRecordsAsync(domain.DomainName));
                RecordList.ItemsSource = records;
                RecordEmptyText.Text = records.Count == 0 ? "暂无记录。" : $"共 {records.Count} 条记录";
            }
            SetStatus("", false);
        }
        catch (Exception ex)
        {
            SetStatus("加载记录失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------------- 增删改 ----------------

    private void AddRecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DomainCombo.SelectedItem is not AlidnsDomain domain)
        {
            SetStatus("请先加载并选择域名", true);
            return;
        }
        _ = ShowRecordDialogAsync(domain, null);
    }

    private void EditRecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DomainCombo.SelectedItem is not AlidnsDomain domain)
        {
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is not AlidnsRecord record)
        {
            return;
        }
        _ = ShowRecordDialogAsync(domain, record);
    }

    private async void DeleteRecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlidnsRecord record)
            return;

        var confirm = new ContentDialog
        {
            Title = "删除解析记录",
            Content = $"确定删除 {record.Summary} 吗？此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        SetBusy(true);
        try
        {
            if (IsCloudflare)
            {
                var client = new CloudflareDnsClient(CfTokenBox.Password);
                await Task.Run(() => client.DeleteRecordAsync(((AlidnsDomain)DomainCombo.SelectedItem).DomainId, record.RecordId));
            }
            else
            {
                var client = new AlidnsClient(AkIdBox.Text.Trim(), AkSecretBox.Password);
                await Task.Run(() => client.DeleteRecordAsync(record.RecordId));
            }
            SetStatus("已删除：" + record.Summary, false, true);
            if (DomainCombo.SelectedItem is AlidnsDomain domain)
                await LoadRecordsAsync(domain);
        }
        catch (Exception ex)
        {
            SetStatus("删除失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>弹出添加/编辑对话框。existing 为 null 时是添加，否则编辑。</summary>
    private async Task ShowRecordDialogAsync(AlidnsDomain domain, AlidnsRecord? existing)
    {
        var isCf = IsCloudflare;

        var rrBox = new TextBox { Header = "主机记录 RR", PlaceholderText = "如 www / @ / * （不带域名）" };
        var typeCombo = new ComboBox
        {
            Header = "记录类型",
            ItemsSource = RecordTypes,
            SelectedIndex = 0,
        };
        var valueBox = new TextBox { Header = "记录值 Value", PlaceholderText = "如 1.2.3.4 / 目标域名 / TXT 文本" };
        var ttlBox = new TextBox { Header = isCf ? "TTL（1 = 自动，Cloudflare 默认）" : "TTL（秒）", Text = isCf ? "1" : "600" };
        var priorityBox = new TextBox { Header = "优先级（MX 必填，其他留空）", PlaceholderText = "如 10" };
        ToggleSwitch? proxyToggle = null;
        if (isCf)
        {
            proxyToggle = new ToggleSwitch
            {
                Header = "代理（橙色云）",
                OnContent = "已代理：流量经 Cloudflare CDN",
                OffContent = "仅 DNS：不代理",
                IsOn = false,
            };
        }

        if (existing is not null)
        {
            rrBox.Text = existing.Rr;
            var idx = Array.IndexOf(RecordTypes, existing.Type);
            typeCombo.SelectedIndex = idx >= 0 ? idx : 0;
            valueBox.Text = existing.Value;
            if (int.TryParse(existing.Ttl, out var ttl)) ttlBox.Text = ttl.ToString();
            priorityBox.Text = existing.Priority;
            if (proxyToggle is not null)
            {
                proxyToggle.IsOn = existing.Line == "已代理";
                // 已代理记录 TTL 必为自动
                if (proxyToggle.IsOn) ttlBox.Text = "1";
            }
        }

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(rrBox);
        form.Children.Add(typeCombo);
        form.Children.Add(valueBox);
        form.Children.Add(ttlBox);
        form.Children.Add(priorityBox);
        if (proxyToggle is not null)
            form.Children.Add(proxyToggle);

        var dialog = new ContentDialog
        {
            Title = existing is null ? $"添加记录 · {domain.DomainName}" : $"编辑记录 · {domain.DomainName}",
            Content = new ScrollViewer
            {
                Content = form,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var rr = rrBox.Text.Trim();
        var type = (typeCombo.SelectedItem as string) ?? "A";
        var value = valueBox.Text.Trim();
        if (rr.Length == 0 || value.Length == 0)
        {
            SetStatus("主机记录和记录值不能为空", true);
            return;
        }
        if (!int.TryParse(ttlBox.Text.Trim(), out var ttlValue) || ttlValue <= 0)
        {
            SetStatus("TTL 必须是正整数", true);
            return;
        }
        int? priority = int.TryParse(priorityBox.Text.Trim(), out var p) ? p : null;
        var proxied = proxyToggle?.IsOn == true;

        SetBusy(true);
        try
        {
            if (isCf)
            {
                // Cloudflare 记录名 = 完整主机名
                var name = rr == "@" ? domain.DomainName : rr.EndsWith("." + domain.DomainName)
                    ? rr
                    : rr + "." + domain.DomainName;
                var client = new CloudflareDnsClient(CfTokenBox.Password);
                if (existing is null)
                {
                    var recordId = await Task.Run(() => client.AddRecordAsync(domain.DomainId, type, name, value, ttlValue, priority, proxied));
                    SetStatus(recordId.Length > 0 ? $"已添加记录：{name} {type} {value}" : "已提交添加（未返回 RecordId）", false, true);
                }
                else
                {
                    await Task.Run(() => client.UpdateRecordAsync(domain.DomainId, existing.RecordId, type, name, value, ttlValue, priority, proxied));
                    SetStatus($"已更新记录：{name} {type} {value}", false, true);
                }
            }
            else
            {
                var client = new AlidnsClient(AkIdBox.Text.Trim(), AkSecretBox.Password);
                if (existing is null)
                {
                    var recordId = await Task.Run(() => client.AddRecordAsync(domain.DomainName, rr, type, value, ttlValue, priority, ""));
                    SetStatus(recordId.Length > 0 ? $"已添加记录：{rr} {type} {value}" : "已提交添加（未返回 RecordId）", false, true);
                }
                else
                {
                    await Task.Run(() => client.UpdateRecordAsync(existing.RecordId, rr, type, value, ttlValue, priority, ""));
                    SetStatus($"已更新记录：{rr} {type} {value}", false, true);
                }
            }
            await LoadRecordsAsync(domain);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------------- 辅助 ----------------

    private void SetStatus(string text, bool isError, bool ok = false)
    {
        StatusText.Text = text;
        try
        {
            StatusText.Foreground = isError
                ? (Microsoft.UI.Xaml.Media.Brush)Resources["AlidnsErrorBrush"]
                : ok ? (Microsoft.UI.Xaml.Media.Brush)Resources["AlidnsOkBrush"]
                     : (Microsoft.UI.Xaml.Media.Brush)Resources["AlidnsInfoBrush"];
        }
        catch
        {
            StatusText.Foreground = null;
        }
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
    }
}
