using System.Drawing;
using System.Drawing.Drawing2D;
using DrawingColor = System.Drawing.Color;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace PhoneShareReceiver;

public partial class MainWindow : Window
{
    private ReceiverSettings _settings = SettingsStore.Load();
    private readonly UploadServer _server;
    private readonly System.Drawing.Icon _appIcon;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        StabilizeWindowIcon();

        _server = new UploadServer(
            () => _settings,
            AppendLog,
            OnFilesReceived,
            OnDevicePaired
        );

        _appIcon = GetAppIcon();
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "PhoneShare Receiver - 正在后台接收文件",
            Icon = _appIcon,
            Visible = false,
            ContextMenuStrip = BuildTrayMenu()
        };
        _notifyIcon.MouseDoubleClick += (_, _) => ShowMainWindow();

        Loaded += async (_, _) =>
        {
            LoadSettingsToUi();
            RenderPairedPhones();
            UpdateQr();
            InitializeTrayIcon();
            await StartServerSafeAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        StabilizeWindowIcon();
        InitializeTrayIcon();
    }

    private void StabilizeWindowIcon()
    {
        // 开发调试时 dotnet run 会频繁重建 exe，Windows 图标缓存有时第一次启动显示不稳定。
        // 这里在窗口创建后再次显式设置 WPF 图标，保证标题栏/任务栏尽快刷新。
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/phoneshare.ico", UriKind.Absolute));
        }
        catch
        {
            // 图标加载失败不影响主功能
        }
    }

    private void InitializeTrayIcon()
    {
        // NotifyIcon 在 WPF 启动早期设置 Visible=true 时，Windows 11 偶尔会先显示默认/残缺图标。
        // 这里延后到窗口 SourceInitialized/Loaded 后再重置一次，强制刷新托盘缓存。
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon = _appIcon;
            _notifyIcon.Text = "PhoneShare Receiver - 正在后台接收文件";
            _notifyIcon.Visible = true;
        }
        catch
        {
            // 托盘刷新失败不影响接收服务
        }
    }

    private static System.Drawing.Icon GetAppIcon()
    {
        // 优先使用输出目录里的正式图标；如果没有，再读取 WPF Resource。
        // 这样可以减少 dotnet run 首次启动时 Windows Shell 图标缓存不稳定的问题。
        try
        {
            var fileIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "phoneshare.ico");
            if (File.Exists(fileIcon))
            {
                using var icon = new System.Drawing.Icon(fileIcon);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch
        {
            // 忽略文件读取失败，继续尝试 Resource
        }

        try
        {
            var resourceInfo = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/phoneshare.ico", UriKind.Absolute));

            if (resourceInfo?.Stream != null)
            {
                using var icon = new System.Drawing.Icon(resourceInfo.Stream);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch
        {
            // 忽略资源读取失败，走运行时绘制兜底图标
        }

        try
        {
            return CreateBlockTrayIcon();
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private static System.Drawing.Icon CreateBlockTrayIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(DrawingColor.Transparent);

        // 兜底图标：仍保持“深灰方块”设计，但加浅色描边，避免在 Windows 11 深色托盘弹窗里太暗。
        using var outlineBrush = new SolidBrush(DrawingColor.FromArgb(210, 214, 220));
        using var mainBrush = new SolidBrush(DrawingColor.FromArgb(58, 62, 68));

        void Block(int x, int y, int w, int h)
        {
            g.FillRectangle(outlineBrush, x - 2, y - 2, w + 4, h + 4);
            g.FillRectangle(mainBrush, x, y, w, h);
        }

        Block(18, 28, 13, 18);
        Block(34, 16, 22, 22);
        Block(34, 44, 16, 10);
        g.FillRectangle(mainBrush, 31, 34, 4, 4);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("打开接收文件夹", null, (_, _) => OpenFolder());
        menu.Items.Add("显示配对二维码", null, (_, _) => { ShowMainWindow(); UpdateQr(); });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        return menu;
    }

    private void LoadSettingsToUi()
    {
        DeviceNameBox.Text = _settings.DeviceName;
        FolderBox.Text = _settings.SaveFolder;
        PortBox.Text = _settings.Port.ToString();
        AutoStartCheck.IsChecked = AutoStartUtil.IsEnabled();
    }

    private bool PullUiToSettings()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1024 || port > 65535)
        {
            MessageBox.Show(this, "监听端口必须是 1024 - 65535 之间的数字。", "端口无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.DeviceName = string.IsNullOrWhiteSpace(DeviceNameBox.Text)
            ? Environment.MachineName
            : DeviceNameBox.Text.Trim();

        _settings.SaveFolder = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PhoneShare")
            : FolderBox.Text.Trim();

        _settings.Port = port;
        _settings.AutoStart = AutoStartCheck.IsChecked == true;
        SettingsStore.Save(_settings);
        AutoStartUtil.SetEnabled(_settings.AutoStart);
        return true;
    }

    private void SaveCurrentSettingsSilently()
    {
        // 退出、隐藏窗口或仅修改目录时使用：尽量保存当前 UI，不弹出额外提示。
        if (int.TryParse(PortBox.Text.Trim(), out var port) && port >= 1024 && port <= 65535)
        {
            _settings.Port = port;
        }

        _settings.DeviceName = string.IsNullOrWhiteSpace(DeviceNameBox.Text)
            ? Environment.MachineName
            : DeviceNameBox.Text.Trim();

        _settings.SaveFolder = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PhoneShare")
            : FolderBox.Text.Trim();

        _settings.AutoStart = AutoStartCheck.IsChecked == true;
        SettingsStore.Save(_settings);
        AutoStartUtil.SetEnabled(_settings.AutoStart);
    }

    private async Task StartServerSafeAsync()
    {
        try
        {
            await _server.StartAsync();
            StatusText.Text = "正在接收";
            StatusText.Foreground = (SolidColorBrush)FindResource("Green");
            StatusDot.Fill = (SolidColorBrush)FindResource("Green");
            StartStopButton.Content = "停止服务";
            _notifyIcon.Visible = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "启动失败";
            StatusText.Foreground = Brushes.Firebrick;
            StatusDot.Fill = Brushes.Firebrick;
            AppendLog("启动失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "服务启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveAndRestartAsync()
    {
        if (!PullUiToSettings()) return;
        UpdateQr();
        await _server.StopAsync();
        await StartServerSafeAsync();
    }

    private async Task ToggleServerAsync()
    {
        if (_server.IsRunning)
        {
            await _server.StopAsync();
            StatusText.Text = "已停止";
            StatusText.Foreground = Brushes.Gray;
            StatusDot.Fill = Brushes.Gray;
            StartStopButton.Content = "启动服务";
        }
        else
        {
            if (!PullUiToSettings()) return;
            UpdateQr();
            await StartServerSafeAsync();
        }
    }

    private void UpdateQr()
    {
        if (!PullUiToSettings()) return;
        var json = UploadServer.BuildPairingJson(_settings);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(json, QRCodeGenerator.ECCLevel.Q);
        using var qr = new QRCode(data);
        using var bitmap = qr.GetGraphic(8, DrawingColor.Black, DrawingColor.White, drawQuietZones: true);
        QrImage.Source = BitmapToImageSource(bitmap);

        var urls = NetworkUtil.GetLocalUrls(_settings.Port);
        UrlText.Text = string.Join("\n", urls.Take(3));
    }

    private static BitmapSource BitmapToImageSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void OnFilesReceived(IReadOnlyList<string> files)
    {
        Dispatcher.Invoke(() =>
        {
            var text = files.Count == 1
                ? $"已接收：{Path.GetFileName(files[0])}"
                : $"已接收 {files.Count} 个文件";

            _notifyIcon.ShowBalloonTip(2000, "PhoneShare", text, WinForms.ToolTipIcon.Info);
        });
    }


    private void OnDevicePaired(PairedPhoneDevice phone)
    {
        Dispatcher.Invoke(() =>
        {
            UpsertPairedPhone(phone);
            RenderPairedPhones();
            _notifyIcon.ShowBalloonTip(1600, "PhoneShare", $"手机已配对：{phone.DisplayName}", WinForms.ToolTipIcon.Info);
        });
    }

    private void UpsertPairedPhone(PairedPhoneDevice phone)
    {
        _settings.PairedPhones ??= new List<PairedPhoneDevice>();
        var existing = _settings.PairedPhones.FirstOrDefault(p => p.DeviceId == phone.DeviceId);
        if (existing == null)
        {
            _settings.PairedPhones.Insert(0, phone);
        }
        else
        {
            existing.PhoneName = phone.PhoneName;
            existing.Manufacturer = phone.Manufacturer;
            existing.AndroidVersion = phone.AndroidVersion;
            existing.LastPairedAt = DateTimeOffset.Now;
        }

        _settings.PairedPhones = _settings.PairedPhones
            .OrderByDescending(p => p.LastPairedAt)
            .ToList();
        SettingsStore.Save(_settings);
    }

    private void RenderPairedPhones()
    {
        PairedPhonesPanel.Children.Clear();
        var phones = (_settings.PairedPhones ?? new List<PairedPhoneDevice>())
            .OrderByDescending(p => p.LastPairedAt)
            .ToList();

        PairedPhoneCountText.Text = $"{phones.Count} 台";
        ClearPairedPhonesButton.IsEnabled = phones.Count > 0;
        EmptyPhonesBox.Visibility = phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (phones.Count == 0)
        {
            return;
        }

        foreach (var phone in phones)
        {
            PairedPhonesPanel.Children.Add(BuildPairedPhoneRow(phone));
        }
    }

    private System.Windows.Controls.Border BuildPairedPhoneRow(PairedPhoneDevice phone)
    {
        var rowBorder = new System.Windows.Controls.Border
        {
            Background = BrushFrom("#F9FAFB"),
            BorderBrush = BrushFrom("#E5E7EB"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        rowBorder.Child = grid;

        var icon = new System.Windows.Controls.Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = BrushFrom("#EEF2F7"),
            BorderBrush = BrushFrom("#E5E7EB"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new System.Windows.Controls.TextBlock
            {
                Text = "▦",
                Foreground = BrushFrom("#374151"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };
        System.Windows.Controls.Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Margin = new Thickness(2, 0, 10, 0)
        };
        info.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = phone.DisplayName,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#111827"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var subtitle = IsCustomNamed(phone)
            ? $"设备型号：{phone.OriginalName} · 最近配对：{phone.LastPairedAt:MM-dd HH:mm}"
            : $"最近配对：{phone.LastPairedAt:MM-dd HH:mm}";

        info.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = BrushFrom("#6B7280"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        System.Windows.Controls.Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        var rename = new System.Windows.Controls.Button
        {
            Content = "改名",
            Tag = phone.DeviceId,
            FontSize = 12,
            Height = 28,
            MinWidth = 50,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        rename.Click += RenamePairedPhone_Click;
        actions.Children.Add(rename);

        var remove = new System.Windows.Controls.Button
        {
            Content = "删除",
            Tag = phone.DeviceId,
            FontSize = 12,
            Height = 28,
            MinWidth = 50,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        remove.Click += RemovePairedPhone_Click;
        actions.Children.Add(remove);

        System.Windows.Controls.Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return rowBorder;
    }

    private static bool IsCustomNamed(PairedPhoneDevice phone) =>
        !string.IsNullOrWhiteSpace(phone.CustomName) &&
        !string.Equals(phone.CustomName.Trim(), phone.OriginalName, StringComparison.OrdinalIgnoreCase);

    private static SolidColorBrush BrushFrom(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private void RenamePairedPhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string deviceId }) return;
        var phone = _settings.PairedPhones.FirstOrDefault(p => p.DeviceId == deviceId);
        if (phone == null) return;

        var newName = ShowRenameDialog(phone);
        if (newName == null) return;

        newName = newName.Trim();
        if (newName.Length == 0)
        {
            MessageBox.Show(this, "名称不能为空。", "修改手机名称", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (newName.Length > 32)
        {
            MessageBox.Show(this, "名称建议不超过 32 个字符。", "修改手机名称", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        phone.CustomName = string.Equals(newName, phone.OriginalName, StringComparison.OrdinalIgnoreCase)
            ? ""
            : newName;

        SettingsStore.Save(_settings);
        RenderPairedPhones();
        AppendLog($"已修改手机显示名：{phone.DisplayName}");
    }

    private string? ShowRenameDialog(PairedPhoneDevice phone)
    {
        var dialog = new Window
        {
            Title = "修改手机名称",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Width = 380,
            Height = 210,
            Background = BrushFrom("#F6F8FB"),
            ShowInTaskbar = false
        };

        var root = new System.Windows.Controls.Grid
        {
            Margin = new Thickness(18)
        };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        dialog.Content = root;

        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "给这台手机起一个好辨认的名称",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#111827"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var hint = new System.Windows.Controls.TextBlock
        {
            Text = $"原始设备名：{phone.OriginalName}",
            FontSize = 12.5,
            Foreground = BrushFrom("#6B7280"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        var input = new System.Windows.Controls.TextBox
        {
            Text = phone.DisplayName,
            FontSize = 14,
            Height = 34,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 0, 14)
        };
        System.Windows.Controls.Grid.SetRow(input, 2);
        root.Children.Add(input);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        System.Windows.Controls.Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        string? result = null;

        var reset = new System.Windows.Controls.Button
        {
            Content = "恢复原名",
            Width = 78,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0)
        };
        reset.Click += (_, _) =>
        {
            result = phone.OriginalName;
            dialog.DialogResult = true;
            dialog.Close();
        };
        buttons.Children.Add(reset);

        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 66,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };
        buttons.Children.Add(cancel);

        var ok = new System.Windows.Controls.Button
        {
            Content = "保存",
            Width = 66,
            Height = 30
        };
        ok.Click += (_, _) =>
        {
            result = input.Text;
            dialog.DialogResult = true;
            dialog.Close();
        };
        buttons.Children.Add(ok);

        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? result : null;
    }

    private void RemovePairedPhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string deviceId }) return;
        var phone = _settings.PairedPhones.FirstOrDefault(p => p.DeviceId == deviceId);
        if (phone == null) return;

        var result = MessageBox.Show(
            this,
            $"确定从电脑端删除“{phone.DisplayName}”的配对记录吗？\n\n删除后，手机端仍可能保留这台电脑，需要在手机 App 里也删除或重新扫码。",
            "删除配对手机",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;
        _settings.PairedPhones.RemoveAll(p => p.DeviceId == deviceId);
        SettingsStore.Save(_settings);
        RenderPairedPhones();
        AppendLog($"已删除配对手机：{phone.DisplayName}");
    }

    private void ClearPairedPhones_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.PairedPhones.Count == 0)
        {
            MessageBox.Show(this, "当前没有已配对手机。", "配对设备", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            "确定清空电脑端保存的所有手机配对记录吗？\n\n这不会影响手机端已保存的电脑记录；如果想彻底解除，需要手机端也删除对应电脑。",
            "清空配对设备",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;
        _settings.PairedPhones.Clear();
        SettingsStore.Save(_settings);
        RenderPairedPhones();
        AppendLog("已清空电脑端配对手机记录。 ");
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择手机文件接收目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(FolderBox.Text)
                ? FolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            FolderBox.Text = dialog.SelectedPath;

            // 选择目录后立即持久化，不需要再点“保存并重启”。
            // 接收服务通过 _settings 实时读取 SaveFolder，改目录不需要重启服务。
            SaveCurrentSettingsSilently();
            AppendLog($"接收目录已保存：{dialog.SelectedPath}");
        }
    }

    private async void SaveRestart_Click(object sender, RoutedEventArgs e) => await SaveAndRestartAsync();

    private async void StartStop_Click(object sender, RoutedEventArgs e) => await ToggleServerAsync();

    private void RefreshQr_Click(object sender, RoutedEventArgs e) => UpdateQr();

    private void CopyPairing_Click(object sender, RoutedEventArgs e)
    {
        if (!PullUiToSettings()) return;
        System.Windows.Clipboard.SetText(UploadServer.BuildPairingJson(_settings));
        AppendLog("配对信息已复制到剪贴板。 ");
    }

    private void ResetToken_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "重置 Token 后，已经配对的手机需要重新扫码绑定。确定继续吗？",
            "重置 Token",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;
        _settings.Token = SecurityUtil.CreateToken();
        if (!PullUiToSettings()) return;
        UpdateQr();
        AppendLog("Token 已重置，请重新扫码绑定手机。 ");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.AutoStart = AutoStartCheck.IsChecked == true;
        SettingsStore.Save(_settings);
        AutoStartUtil.SetEnabled(_settings.AutoStart);
    }

    private void OpenFolder()
    {
        if (!PullUiToSettings()) return;
        Directory.CreateDirectory(_settings.SaveFolder);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _settings.SaveFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _notifyIcon.ShowBalloonTip(1200, "PhoneShare", "已最小化到托盘，仍会继续接收文件。", WinForms.ToolTipIcon.Info);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentSettingsSilently();

        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon.ShowBalloonTip(1200, "PhoneShare", "已隐藏到托盘。右键托盘图标可退出。", WinForms.ToolTipIcon.Info);
            return;
        }

        base.OnClosing(e);
    }

    private async Task ExitAsync()
    {
        SaveCurrentSettingsSilently();
        _exitRequested = true;
        await _server.StopAsync();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void ShowMainWindowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowMainWindowFromTray);
            return;
        }

        Show();

        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Normal;
        }

        Activate();

        // 让窗口从其他窗口后面弹到前面
        Topmost = true;
        Topmost = false;

        Focus();
    }
}
