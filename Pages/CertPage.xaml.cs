using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DesktopTool.Pages;

/// <summary>
/// 证书申请页：原生 ACME v2（DNS-01 手动验证）申请 Let's Encrypt 免费证书。
/// 第一步创建订单并获取 TXT 记录 → 用户添加 DNS → 第二步验证并签发。
/// </summary>
public sealed partial class CertPage : Page
{
    private bool _busy;
    private CertSession? _session;

    public CertPage()
    {
        InitializeComponent();
        Loaded += CertPage_Loaded;
    }

    private const string CfCredTarget = "seraphine:cloudflare";
    private const string AkCredTarget = "seraphine:alidns";

    private void CertPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 恢复上次输入
        DomainBox.Text = ConfigSync.Get<string>("cert_domain", "") ?? "";
        EmailBox.Text = ConfigSync.Get<string>("cert_email", "") ?? "";

        // 复用「域名管理」页已保存的 DNS 凭证，避免重复填写
        try
        {
            if (WindowsCredentialStore.TryRead(CfCredTarget, out _, out var cfSecret) && !string.IsNullOrEmpty(cfSecret))
                CfTokenBox.Password = cfSecret;
            if (WindowsCredentialStore.TryRead(AkCredTarget, out var akId, out var akSecret))
            {
                if (AkIdBox.Text.Length == 0) AkIdBox.Text = akId ?? "";
                if (AkSecretBox.Password.Length == 0) AkSecretBox.Password = akSecret ?? "";
            }
        }
        catch { }

        // 恢复上次选择的 DNS 服务商
        var provider = ConfigSync.Get<string>("cert_dns_provider", "") ?? "";
        if (provider.Length > 0)
        {
            foreach (var item in DnsProviderCombo.Items)
            {
                if (item is ComboBoxItem cbi && (cbi.Tag as string) == provider)
                {
                    DnsProviderCombo.SelectedItem = item;
                    break;
                }
            }
        }

        // 若存在未完成的会话（软件重启后），恢复 TXT 卡片
        var domain = DomainBox.Text.Trim();
        if (domain.Length > 0)
        {
            var session = CertStore.LoadSession(domain);
            if (session is not null)
            {
                _session = session;
                ShowChallengeCard();
                Log("检测到未完成的申请会话，已恢复。可继续第二步或点击「重置」。");
            }
        }
        RefreshInstalledCerts();
        UpdateButtons();
    }

    private static string SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private string SelectedCaName => SelectedTag(CaCombo) is { Length: > 0 } s ? s : "letsencrypt";

    private string SelectedDnsProvider
        => (DnsProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "alidns";

    private bool IsCloudflareDns => SelectedDnsProvider == "cloudflare";

    private void DnsProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 解析期 ComboBoxItem 的 IsSelected 会提前触发本事件，此时后续元素尚未创建，判空跳过
        if (AkIdBox is null || AkSecretBox is null || CfTokenBox is null) return;

        var cf = IsCloudflareDns;
        CfTokenBox.Visibility = cf ? Visibility.Visible : Visibility.Collapsed;
        AkIdBox.Visibility = cf ? Visibility.Collapsed : Visibility.Visible;
        AkSecretBox.Visibility = cf ? Visibility.Collapsed : Visibility.Visible;

        ConfigSync.Set("cert_dns_provider", SelectedDnsProvider);
    }

    // ---------------- 模式切换（SelectorBar） ----------------

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var auto = ReferenceEquals(sender.SelectedItem, sender.Items.Count > 0 ? sender.Items[0] : null);
        AutoPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
    }

    private static AcmeDirectoryInfo DirectoryInfo(string caName)
    {
        var url = caName == "letsencrypt_staging"
            ? "https://acme-staging-v02.api.letsencrypt.org/directory"
            : "https://acme-v02.api.letsencrypt.org/directory";
        return new AcmeDirectoryInfo(url, CertStore.AccountKeyPath(caName));
    }

    // ---------------- DNS 自动申请 / 续期（共享核心） ----------------

    private async void AutoRunBtn_Click(object sender, RoutedEventArgs e)
    {
        var domain = DomainBox.Text.Trim();
        var email = EmailBox.Text.Trim();
        var akId = AkIdBox.Text.Trim();
        var akSecret = AkSecretBox.Password;
        if (domain.Length == 0 || email.Length == 0)
        {
            SetStatus("请填写域名和邮箱", isError: true);
            return;
        }
        var isCf = IsCloudflareDns;
        if (isCf)
        {
            if (CfTokenBox.Password.Length == 0)
            {
                SetStatus("请填写 Cloudflare API Token", isError: true);
                return;
            }
        }
        else if (akId.Length == 0 || akSecret.Length == 0)
        {
            SetStatus("请填写阿里云 AccessKey ID 和 Secret", isError: true);
            return;
        }
        if (!IsValidDomain(domain))
        {
            SetStatus("域名格式不正确，示例：server.example.com 或 *.example.com", isError: true);
            return;
        }

        SetBusy(true);
        var pendingCleanup = new List<(string Provider, string ZoneId, string RecordId, string Rr)>();
        try
        {
            Log("======== 一键申请（" + (isCf ? "Cloudflare" : "阿里云") + " DNS 自动验证）========");
            var algorithm = SelectedTag(KeyCombo) is { Length: > 0 } a ? a : "ecdsa";
            var certKeyPath = CertStore.GenerateCertKey(domain, algorithm);
            Log($"已生成证书私钥：{(algorithm == "rsa" ? "RSA 2048" : "ECDSA P-256")}");

            await ApplyCertificateAsync(domain, email, SelectedCaName, isCf,
                CfTokenBox.Password, akId, akSecret, algorithm, certKeyPath, pendingCleanup);
            SetStatus("证书签发成功", isError: false);
        }
        catch (Exception ex)
        {
            Log("发生错误：" + ex.Message);
            SetStatus("一键申请失败：" + ex.Message, isError: true);
        }
        finally
        {
            await CleanupTxtRecordsAsync(pendingCleanup);
            SetBusy(false);
            RefreshInstalledCerts();
        }
    }

    /// <summary>共享核心：注册 ACME 账户 → 创建订单 → 添加 TXT 并等生效 → 验证 → finalize → 下载保存。</summary>
    private async Task ApplyCertificateAsync(
        string domain, string email, string caName, bool isCf,
        string cfToken, string akId, string akSecret,
        string keyAlgorithm, string certKeyPath,
        List<(string Provider, string ZoneId, string RecordId, string Rr)> pendingCleanup)
    {
        var dir = DirectoryInfo(caName);
        var accountKey = CertStore.LoadOrCreateAccountKey(caName);
        var client = new AcmeClient(dir.DirectoryUrl, accountKey);

        // 清理旧会话
        CertStore.DeleteSession(domain);
        _session = null;
        ResultCard.Visibility = Visibility.Collapsed;

        Log($"CA：{(caName == "letsencrypt_staging" ? "Let's Encrypt 暂存环境" : "Let's Encrypt 正式环境")}");
        Log($"注册 ACME 账户（{email}）...");
        await client.RegisterAsync(email);
        Log($"账户就绪，KID：{client.Kid}");

        Log($"为域名 {domain} 创建订单...");
        var order = await client.CreateOrderAsync(domain);
        Log($"订单创建成功，含 {order.Challenges.Count} 个 DNS-01 验证挑战");

        // 为每个挑战：按服务商添加 TXT → 等待 DNS 生效
        foreach (var ch in order.Challenges)
        {
            var (root, rr) = AlidnsClient.SplitRecordName(ch.RecordName);
            if (isCf)
            {
                var cf = new CloudflareDnsClient(cfToken);
                Log($"Cloudflare：根域 {root}");
                var zone = await cf.FindZoneAsync(root)
                    ?? throw new AcmeException($"Cloudflare 未找到域名 {root} 的 zone，请确认该域已添加到 Cloudflare 且 Token 有 Zone Read 权限");

                // 清理同名旧 TXT 记录，避免累积
                try
                {
                    var olds = await cf.ListTxtRecordsAsync(zone.DomainId, ch.RecordName);
                    foreach (var rec in olds)
                    {
                        Log($"清理旧 TXT 记录 {rec.Rr}（id={rec.RecordId}）");
                        await cf.DeleteTxtRecordAsync(zone.DomainId, rec.RecordId);
                    }
                }
                catch (Exception ex)
                {
                    Log("清理旧记录失败（忽略）：" + ex.Message);
                }

                Log($"添加 TXT 记录：{ch.RecordName} = {ch.TxtValue}");
                var cfRecordId = await cf.AddTxtRecordAsync(zone.DomainId, ch.RecordName, ch.TxtValue);
                pendingCleanup.Add(("cloudflare", zone.DomainId, cfRecordId, ch.RecordName));
                Log($"已添加（RecordId={cfRecordId}），等待 DNS 生效（最多 3 分钟）...");
            }
            else
            {
                var alidns = new AlidnsClient(akId, akSecret);
                Log($"阿里云解析：{root}  主机记录：{rr}");

                // 清理同名旧 TXT 记录，避免累积
                try
                {
                    var olds = await alidns.DescribeTxtRecordsAsync(root);
                    foreach (var rec in olds.Where(x => x.Rr == rr))
                    {
                        Log($"清理旧 TXT 记录 {rr}（RecordId={rec.RecordId}）");
                        await alidns.DeleteRecordAsync(rec.RecordId);
                    }
                }
                catch (Exception ex)
                {
                    Log("清理旧记录失败（忽略）：" + ex.Message);
                }

                Log($"添加 TXT 记录：{ch.RecordName} = {ch.TxtValue}");
                var recordId = await alidns.AddTxtRecordAsync(root, rr, ch.TxtValue);
                pendingCleanup.Add(("alidns", "", recordId, rr));
                Log($"已添加（RecordId={recordId}），等待 DNS 生效（最多 3 分钟）...");
            }

            if (!await WaitTxtVisibleAsync(ch.RecordName, ch.TxtValue, TimeSpan.FromSeconds(180)))
                throw new AcmeException($"等待 DNS 记录生效超时：{ch.RecordName}");
            Log($"{ch.RecordName} 已生效，记录值匹配。");
        }

        // 提交挑战并等待各域名验证
        foreach (var ch in order.Challenges)
        {
            var status = await client.GetAuthorizationStatusAsync(ch.AuthorizationUrl);
            if (status is "pending" or "unknown")
            {
                Log($"提交验证：{ch.Domain} ...");
                await client.TriggerChallengeAsync(ch);
            }
            else
            {
                Log($"{ch.Domain} 当前授权状态：{status}");
            }
        }
        foreach (var ch in order.Challenges)
        {
            Log($"等待验证：{ch.Domain} ...");
            await client.PollAuthorizationAsync(ch);
            Log($"域名 {ch.Domain} 验证通过");
        }

        // 生成 CSR 并 finalize
        Log("生成证书签名请求（CSR）...");
        var certKey = CertStore.LoadCertKey(certKeyPath);
        var csr = CertStore.CreateCsr(certKey, domain);
        Log("提交订单 finalize...");
        await client.FinalizeOrderAsync(order.FinalizeUrl, csr);

        Log("等待证书签发...");
        var certUrl = await client.PollOrderAsync(order.OrderUrl);

        Log("下载证书...");
        var certPem = await client.DownloadCertificateAsync(certUrl);
        CertStore.SaveCertificateBundle(domain, certPem);

        var certDir = CertStore.DomainDir(domain);
        ResultTitle.Text = "证书申请成功！";
        ResultPathText.Text = certDir;
        ResultCard.Visibility = Visibility.Visible;
        Log($"证书已保存到：{certDir}");
        Log("文件：privkey.pem（私钥）、cert.pem（证书）、chain.pem（中间链）、fullchain.pem（完整链）");
        Log("部署时 Web 服务器通常使用 fullchain.pem + privkey.pem。");

        CertStore.DeleteSession(domain);
        _session = null;
    }

    /// <summary>清理本次自动添加的 TXT 验证记录（尽力而为，失败则提示手动删除）。</summary>
    private async Task CleanupTxtRecordsAsync(List<(string Provider, string ZoneId, string RecordId, string Rr)> list)
    {
        foreach (var (provider, zoneId, recordId, rr) in list)
        {
            try
            {
                if (provider == "cloudflare")
                    await new CloudflareDnsClient(CfTokenBox.Password).DeleteTxtRecordAsync(zoneId, recordId);
                else
                    await new AlidnsClient(AkIdBox.Text.Trim(), AkSecretBox.Password).DeleteRecordAsync(recordId);
                Log($"已删除验证记录：{rr}");
            }
            catch (Exception ex)
            {
                Log($"删除验证记录失败（{rr}，请手动清理）：{ex.Message}");
            }
        }
    }

    // ---------------- 已存在的证书（续期 / 管理） ----------------

    private void RefreshInstalledCerts()
    {
        var certs = CertStore.ListInstalledCerts();
        InstalledList.ItemsSource = certs;
        InstalledEmptyText.Visibility = certs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledCard.Visibility = Visibility.Visible;
    }

    private async void RenewCertBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CertInstalledInfo info) return;
        if (info.ExpiresAt is { } exp && exp > DateTime.Now && (exp - DateTime.Now).TotalDays > 30)
        {
            var dlg = new ContentDialog
            {
                Title = "续期确认",
                Content = $"证书 {info.Domain} 还有 {(int)(exp - DateTime.Now).TotalDays} 天才过期，确定现在续期吗？",
                PrimaryButtonText = "续期",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var email = EmailBox.Text.Trim();
        if (email.Length == 0)
            email = ConfigSync.Get<string>("cert_email", "") ?? "";
        if (email.Length == 0)
        {
            SetStatus("请先在上方填写邮箱（用于 ACME 账户）", isError: true);
            return;
        }
        if (!info.HasPrivKey)
        {
            SetStatus("该证书缺少 privkey.pem，无法续期（请走全新申请）", isError: true);
            return;
        }
        var isCf = IsCloudflareDns;
        if (isCf && CfTokenBox.Password.Length == 0)
        {
            SetStatus("请填写 Cloudflare API Token", isError: true);
            return;
        }
        if (!isCf && (AkIdBox.Text.Trim().Length == 0 || AkSecretBox.Password.Length == 0))
        {
            SetStatus("请填写阿里云 AccessKey ID 和 Secret", isError: true);
            return;
        }

        SetBusy(true);
        var pendingCleanup = new List<(string Provider, string ZoneId, string RecordId, string Rr)>();
        try
        {
            Log($"======== 续期证书：{info.Domain}（复用原私钥）========");
            await ApplyCertificateAsync(info.Domain, email, SelectedCaName, isCf,
                CfTokenBox.Password, AkIdBox.Text.Trim(), AkSecretBox.Password,
                info.KeyAlgorithm, Path.Combine(info.Dir, "privkey.pem"), pendingCleanup);
            SetStatus("续期成功", isError: false);
        }
        catch (Exception ex)
        {
            Log("续期发生错误：" + ex.Message);
            SetStatus("续期失败：" + ex.Message, isError: true);
        }
        finally
        {
            await CleanupTxtRecordsAsync(pendingCleanup);
            SetBusy(false);
            RefreshInstalledCerts();
        }
    }

    private void OpenInstalledDirBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CertInstalledInfo info) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{info.Dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private async void DeleteCertBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CertInstalledInfo info) return;
        var dlg = new ContentDialog
        {
            Title = "删除证书",
            Content = $"确定删除证书 {info.Domain} 吗？将删除其目录下的全部文件（私钥、证书）。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            Directory.Delete(info.Dir, recursive: true);
            Log($"已删除证书：{info.Domain}");
            RefreshInstalledCerts();
        }
        catch (Exception ex)
        {
            Log("删除证书失败：" + ex.Message);
            SetStatus("删除证书失败：" + ex.Message, isError: true);
        }
    }

    /// <summary>轮询公网 DNS，直到目标 TXT 值可见或超时。</summary>
    private static async Task<bool> WaitTxtVisibleAsync(string recordName, string txtValue, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var txts = await DnsTxtResolver.QueryTxtAsync(recordName);
                if (txts.Contains(txtValue)) return true;
            }
            catch { }
            await Task.Delay(8000);
        }
        return false;
    }

    // ---------------- 第一步 ----------------

    private async void Step1Btn_Click(object sender, RoutedEventArgs e)
    {
        var domain = DomainBox.Text.Trim();
        var email = EmailBox.Text.Trim();
        if (domain.Length == 0 || email.Length == 0)
        {
            SetStatus("请填写域名和邮箱", isError: true);
            return;
        }
        if (!IsValidDomain(domain))
        {
            SetStatus("域名格式不正确，示例：server.example.com 或 *.example.com", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            Log("======== 第一步：创建订单并获取 DNS 验证记录 ========");
            var caName = SelectedCaName;
            var dir = DirectoryInfo(caName);
            var accountKey = CertStore.LoadOrCreateAccountKey(caName);
            var client = new AcmeClient(dir.DirectoryUrl, accountKey);

            // 清理旧会话
            CertStore.DeleteSession(domain);
            _session = null;
            ResultCard.Visibility = Visibility.Collapsed;

            Log($"CA：{(caName == "letsencrypt_staging" ? "Let's Encrypt 暂存环境" : "Let's Encrypt 正式环境")}");
            Log($"注册 ACME 账户（{email}）...");
            await client.RegisterAsync(email);
            Log($"账户就绪，KID：{client.Kid}");

            Log($"为域名 {domain} 创建订单...");
            var order = await client.CreateOrderAsync(domain);
            Log($"订单创建成功，含 {order.Challenges.Count} 个 DNS-01 验证挑战");

            // 生成证书私钥（与证书一起保存）
            var algorithm = SelectedTag(KeyCombo) is { Length: > 0 } a ? a : "ecdsa";
            var certKeyPath = CertStore.GenerateCertKey(domain, algorithm);
            Log($"已生成证书私钥：{(algorithm == "rsa" ? "RSA 2048" : "ECDSA P-256")}");

            _session = new CertSession
            {
                CaName = caName,
                Domain = domain,
                Email = email,
                Kid = client.Kid,
                OrderUrl = order.OrderUrl,
                FinalizeUrl = order.FinalizeUrl,
                CertKeyPath = certKeyPath,
                KeyAlgorithm = algorithm,
                Challenges = order.Challenges,
            };
            CertStore.SaveSession(_session);

            ConfigSync.Set("cert_domain", domain);
            ConfigSync.Set("cert_email", email);

            ShowChallengeCard();
            foreach (var ch in order.Challenges)
            {
                Log($"  域名 {ch.Domain}");
                Log($"    主机记录：{ch.RecordName}");
                Log($"    记录值  ：{ch.TxtValue}");
            }
            Log("请到 DNS 服务商添加上述 TXT 记录，生效后执行第二步。");
            Log("提示：正式环境有配额限制，首次使用建议先用「暂存 / 测试」环境验证流程。");
            SetStatus("已获取验证记录，请添加 TXT 记录后继续", isError: false);
        }
        catch (Exception ex)
        {
            Log("发生错误：" + ex.Message);
            SetStatus("第一步失败：" + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------------- 检查 DNS ----------------

    private async void CheckDnsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.Challenges.Count == 0) return;
        var ch = _session.Challenges[0];
        SetBusy(true);
        try
        {
            Log($"查询 {ch.RecordName} 的 TXT 记录...");
            var txts = await DnsTxtResolver.QueryTxtAsync(ch.RecordName);
            if (txts.Count == 0)
            {
                Log("未返回任何 TXT 记录，记录可能尚未生效。");
                SetStatus("未查询到 TXT 记录，请确认已添加", isError: true);
            }
            else if (txts.Contains(ch.TxtValue))
            {
                Log($"DNS 生效确认：{ch.RecordName} 的值已匹配。");
                SetStatus("DNS 记录已生效", isError: false);
            }
            else
            {
                Log($"DNS 生效确认：找到 {txts.Count} 条 TXT 记录，但值不匹配。当前值：{string.Join(" / ", txts)}");
                SetStatus("TXT 记录值不匹配，请核对后重试", isError: true);
            }
        }
        catch (Exception ex)
        {
            Log("DNS 查询失败：" + ex.Message);
            SetStatus("DNS 查询失败（可能网络受限）", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------------- 第二步 ----------------

    private async void Step2Btn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            SetStatus("未找到待验证的会话，请先执行第一步", isError: true);
            return;
        }

        SetBusy(true);
        try
        {
            Log("======== 第二步：提交验证并签发证书 ========");
            var session = _session;
            var dir = DirectoryInfo(session.CaName);
            var accountKey = CertStore.LoadOrCreateAccountKey(session.CaName);
            var client = new AcmeClient(dir.DirectoryUrl, accountKey);
            client.SetKid(session.Kid);

            // 前置检查：TXT 记录是否已生效
            if (session.Challenges.Count > 0)
            {
                var ch = session.Challenges[0];
                Log($"检查 DNS 记录 {ch.RecordName} 是否已生效...");
                try
                {
                    var txts = await DnsTxtResolver.QueryTxtAsync(ch.RecordName);
                    if (txts.Contains(ch.TxtValue))
                        Log("DNS 记录已生效，记录值匹配。");
                    else
                        Log("警告：未匹配到记录值（可能尚未生效或本地缓存）。仍将提交验证，若失败请确认记录已生效。");
                }
                catch (Exception ex)
                {
                    Log("警告：DNS 前置检查失败（" + ex.Message + "），将直接提交验证。");
                }
            }

            // 依次提交并等待各域名验证
            foreach (var ch in session.Challenges)
            {
                // 先查授权状态：可能之前已处理过（valid/invalid/processing），不能盲目重复提交
                string status;
                try
                {
                    status = await client.GetAuthorizationStatusAsync(ch.AuthorizationUrl);
                    Log($"{ch.Domain} 当前授权状态：{status}");
                }
                catch (Exception ex)
                {
                    status = "unknown";
                    Log($"查询授权状态失败（{ex.Message}），将直接提交验证。");
                }

                switch (status)
                {
                    case "pending":
                    case "unknown":
                        Log($"提交验证：{ch.Domain} ...");
                        await client.TriggerChallengeAsync(ch);
                        break;
                    case "valid":
                        Log($"{ch.Domain} 已验证通过，跳过提交。");
                        break;
                    case "processing":
                        Log($"{ch.Domain} 正在验证中，等待结果...");
                        break;
                    case "invalid":
                        throw new AcmeException($"域名 {ch.Domain} 验证已失败。请回到第一步重新生成订单（会生成新的 TXT 值，需同步更新 DNS 记录后再执行第二步）。");
                    default:
                        throw new AcmeException($"域名 {ch.Domain} 授权状态异常：{status}");
                }
            }
            foreach (var ch in session.Challenges)
            {
                Log($"等待验证：{ch.Domain} ...");
                await client.PollAuthorizationAsync(ch);
                Log($"域名 {ch.Domain} 验证通过");
            }

            // 生成 CSR 并 finalize
            Log("生成证书签名请求（CSR）...");
            var certKey = CertStore.LoadCertKey(session.CertKeyPath);
            var csr = CertStore.CreateCsr(certKey, session.Domain);
            Log("提交订单 finalize...");
            await client.FinalizeOrderAsync(session.FinalizeUrl, csr);

            Log("等待证书签发...");
            var certUrl = await client.PollOrderAsync(session.OrderUrl);

            Log("下载证书...");
            var certPem = await client.DownloadCertificateAsync(certUrl);
            CertStore.SaveCertificateBundle(session.Domain, certPem);

            var certDir = CertStore.DomainDir(session.Domain);
            ResultTitle.Text = "证书申请成功！";
            ResultPathText.Text = certDir;
            ResultCard.Visibility = Visibility.Visible;
            Log($"证书已保存到：{certDir}");
            Log("文件：privkey.pem（私钥）、cert.pem（证书）、chain.pem（中间链）、fullchain.pem（完整链）");
            Log("部署时 Web 服务器通常使用 fullchain.pem + privkey.pem。");

            // 清理会话（不再需要续作）
            CertStore.DeleteSession(session.Domain);
            _session = null;
            TxtCard.Visibility = Visibility.Collapsed;
            SetStatus("证书签发成功", isError: false);
        }
        catch (Exception ex)
        {
            Log("发生错误：" + ex.Message);
            SetStatus("第二步失败：" + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------------- 其他 ----------------

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            CertStore.DeleteSession(_session.Domain);
            _session = null;
        }
        TxtCard.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;
        Log("已重置（删除中间会话状态，证书产物保留）。");
        SetStatus("", isError: false);
        UpdateButtons();
    }

    private void CopyNameBtn_Click(object sender, RoutedEventArgs e)
        => CopyText(RecordNameBox.Text, "已复制主机记录");

    private void CopyValueBtn_Click(object sender, RoutedEventArgs e)
        => CopyText(RecordValueBox.Text, "已复制记录值");

    private void OpenDirBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null && string.IsNullOrEmpty(ResultPathText.Text)) return;
        var dir = _session is not null
            ? CertStore.DomainDir(_session.Domain)
            : ResultPathText.Text.Trim();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void CopyText(string text, string tip)
    {
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        SetStatus(tip, isError: false);
    }

    // ---------------- 界面辅助 ----------------

    private void ShowChallengeCard()
    {
        if (_session is null || _session.Challenges.Count == 0) return;
        var ch = _session.Challenges[0];
        RecordNameBox.Text = ch.RecordName;
        RecordValueBox.Text = ch.TxtValue;
        TxtCard.Visibility = Visibility.Visible;
    }

    private static bool IsValidDomain(string d)
    {
        if (d.StartsWith("*.", StringComparison.Ordinal)) d = d.Substring(2);
        if (d.Length == 0 || d.Length > 253) return false;
        return Uri.CheckHostName(d) == UriHostNameType.Dns;
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        try
        {
            StatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources[isError ? "StatusErrorBrush" : "StatusInfoBrush"];
        }
        catch
        {
            StatusText.Foreground = null;
        }
    }

    private void Log(string line)
    {
        LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        // 滚动到底部（用当前可滚动高度，避免 Infinity 偏移触发异常）
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            try { LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null); }
            catch { }
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasSession = _session is not null;
        Step1Btn.IsEnabled = !_busy;
        CheckDnsBtn.IsEnabled = !_busy && hasSession;
        Step2Btn.IsEnabled = !_busy && hasSession;
        ResetBtn.IsEnabled = !_busy && hasSession;
        AutoRunBtn.IsEnabled = !_busy;
    }
}
