using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DesktopTool.Pages;

public sealed partial class LoginPage : Page
{
    public LoginPage()
    {
        this.InitializeComponent();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private async void LoginBtn_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            StatusText.Text = "请输入用户名和密码";
            return;
        }

        StatusText.Text = "登录中...";
        var (success, message, data) = await ApiHelper.PostAsync("/desktop/login", new
        {
            username,
            password,
        });

        if (success && data.HasValue)
        {
            try
            {
                var d = data.Value;
                ApiHelper.UserId = d.TryGetProperty("user_id", out var uid) ? uid.GetString() : "";
                ApiHelper.Username = d.TryGetProperty("username", out var un) ? un.GetString() : "";
                if (d.TryGetProperty("avatar", out var av) && av.ValueKind == System.Text.Json.JsonValueKind.String)
                    ApiHelper.Avatar = av.GetString();
                StatusText.Text = "";
                // 从服务端同步配置
                await ConfigSync.SyncFromServerAsync();
                // 保存登录态到本机凭据库，下次启动免登录
                ApiHelper.SaveLoginState();
                if (Frame.CanGoBack)
                    Frame.GoBack();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"解析失败：{ex.Message}";
            }
        }
        else
        {
            StatusText.Text = $"登录失败：{message}";
        }
    }

    private async void RegisterBtn_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            StatusText.Text = "请输入用户名和密码";
            return;
        }

        StatusText.Text = "注册中...";
        var (success, message, _) = await ApiHelper.PostAsync("/desktop/register", new
        {
            username,
            password,
        });

        StatusText.Text = success ? "注册成功，请登录" : $"注册失败：{message}";
    }
}
