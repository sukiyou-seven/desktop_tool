using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace DesktopTool.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ServerUrlBox.Text = ApiHelper.ServerUrl;
        RefreshState();
        _ = RefreshAutoStartAsync();
    }

    private void RefreshState()
    {
        if (ApiHelper.IsLoggedIn)
        {
            NotLoggedInCard.Visibility = Visibility.Collapsed;
            LoggedInCard.Visibility = Visibility.Visible;
            LogoutItem.Visibility = Visibility.Visible;
            UsernameText.Text = ApiHelper.Username ?? "";
            UserIdText.Text = ApiHelper.UserId ?? "";
            _ = LoadAvatarAsync();
        }
        else
        {
            NotLoggedInCard.Visibility = Visibility.Visible;
            LoggedInCard.Visibility = Visibility.Collapsed;
            LogoutItem.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RefreshAutoStartAsync()
    {
        // 读取当前状态时先摘掉事件，避免赋值 IsOn 误触发写入
        AutoStartToggle.Toggled -= AutoStartToggle_Toggled;
        AutoStartToggle.IsOn = await AutoStartManager.IsEnabledAsync();
        AutoStartToggle.Toggled += AutoStartToggle_Toggled;
        UpdateAutoStartStatus(AutoStartToggle.IsOn);
    }

    private async void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            bool ok;
            if (AutoStartToggle.IsOn)
                ok = await AutoStartManager.EnableAsync();
            else
            {
                await AutoStartManager.DisableAsync();
                ok = true;
            }

            if (ok)
                UpdateAutoStartStatus(AutoStartToggle.IsOn);
            else
                AutoStartStatus.Text = "设置失败：已被系统策略或用户设置禁用";
        }
        catch (Exception ex)
        {
            AutoStartStatus.Text = "设置失败：" + ex.Message;
            AutoStartToggle.IsOn = !AutoStartToggle.IsOn;
        }
    }

    private void UpdateAutoStartStatus(bool enabled)
    {
        AutoStartStatus.Text = enabled
            ? "已开启：登录系统后自动启动"
            : "已关闭：仅手动启动";
    }

    private async Task LoadAvatarAsync()
    {
        var avatar = ApiHelper.Avatar;
        if (string.IsNullOrEmpty(avatar))
        {
            AvatarBrush.ImageSource = null;
            return;
        }
        try
        {
            var bytes = Convert.FromBase64String(avatar);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            AvatarBrush.ImageSource = bitmap;
        }
        catch
        {
            AvatarBrush.ImageSource = null;
        }
    }

    private void GoLoginBtn_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(LoginPage));
    }

    private void SaveServerBtn_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            ServerStatus.Text = "地址不能为空";
            return;
        }
        ApiHelper.ServerUrl = url;
        ApiHelper.SaveServerUrl();
        ServerStatus.Text = "已保存";
    }

    private void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        ApiHelper.ClearCredentials();
        ConfigSync.Reset();
        RefreshState();
    }

    private async void Avatar_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (!ApiHelper.IsLoggedIn) return;

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            // 读取并压缩（限制尺寸）
            var bitmap = new BitmapImage();
            using (var stream = await file.OpenAsync(FileAccessMode.Read))
            {
                await bitmap.SetSourceAsync(stream);
            }

            // 直接转 base64
            byte[] bytes;
            using (var stream = await file.OpenReadAsync())
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
            }

            var base64 = Convert.ToBase64String(bytes);

            // 上传
            var (success, message, data) = await ApiHelper.PostAsync("/desktop/avatar/update", new
            {
                user_id = ApiHelper.UserId,
                avatar = base64,
                tokens = new { token = ApiHelper.Token ?? "", refresh = ApiHelper.RefreshToken ?? "" },
            });

            if (success)
            {
                ApiHelper.Avatar = base64;
                await LoadAvatarAsync();
            }
            else
            {
                // 即使上传失败也本地显示
                ApiHelper.Avatar = base64;
                await LoadAvatarAsync();
            }
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = "头像设置失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot,
            };
            await dlg.ShowAsync();
        }
    }
}
