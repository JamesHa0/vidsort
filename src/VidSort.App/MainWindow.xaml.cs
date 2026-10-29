using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VidSort.App.Services;
using VidSort.Core;

namespace VidSort.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private VlcPlayerService? _player;
    private List<VideoItem> _queue = new();
    private string? _rootDir;
    private int _queueIndex = -1;
    private bool _isClosing;
    private bool _isFullscreen;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private bool _previousTopmost;

    public MainWindow()
    {
        InitializeComponent();
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppConfig config = AppConfig.Load();

        try
        {
            _player = new VlcPlayerService();
            _player.PlaybackEnded += OnPlaybackEnded;
            _player.Volume = 80;
            PlayerView.MediaPlayer = _player.MediaPlayer;
        }
        catch (Exception ex)
        {
            FileNameText.Text = "libVLC 初始化失败";
            ProgressText.Text = ex.Message;
            ShowToast(ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.RootDir) || !Directory.Exists(config.RootDir))
        {
            const string message = "请在 config.json 里把 rootDir 设成你的视频库根目录";
            FileNameText.Text = message;
            ProgressText.Text = AppConfig.ConfigPath;
            ShowToast(message);
            return;
        }

        try
        {
            _rootDir = Path.GetFullPath(config.RootDir);

            var categoryTree = new CategoryTree(_rootDir, config.HiddenCategories);
            categoryTree.Scan();

            ScanResult scan = new LibraryIndex(_rootDir).Scan();
            _queue = PlayQueue.Build(scan.Items, null, false);

            if (_queue.Count == 0)
            {
                FileNameText.Text = "库里没有找到视频";
                ProgressText.Text = _rootDir;
                ShowToast("库里没有找到视频");
                return;
            }

            _queueIndex = 0;
            await PlayCurrentAsync();
        }
        catch (Exception ex)
        {
            FileNameText.Text = "加载视频库失败";
            ProgressText.Text = ex.Message;
            ShowToast($"加载视频库失败：{ex.Message}");
        }
    }

    private async Task PlayCurrentAsync()
    {
        if (_player is null || _rootDir is null || _queueIndex < 0 || _queueIndex >= _queue.Count)
        {
            return;
        }

        VideoItem item = _queue[_queueIndex];
        string path = Path.Combine(_rootDir, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        FileNameText.Text = item.FileName;
        ProgressText.Text = "正在打开…";

        try
        {
            await _player.PlayAsync(path, 0);
            _progressTimer.Start();
            UpdateProgress();
        }
        catch (Exception ex)
        {
            _progressTimer.Stop();
            ProgressText.Text = "播放失败";
            ShowToast($"播放失败：{ex.Message}");
        }
    }

    private async Task PlayNextAsync(bool fromPlaybackEnd = false)
    {
        if (_queue.Count == 0)
        {
            ShowToast("库里没有找到视频");
            return;
        }

        if (_queueIndex + 1 >= _queue.Count)
        {
            _progressTimer.Stop();
            ProgressText.Text = fromPlaybackEnd ? "已播放完" : ProgressText.Text;
            ShowToast(fromPlaybackEnd ? "视频播放完毕" : "已经是最后一条");
            return;
        }

        _queueIndex++;
        await PlayCurrentAsync();
    }

    private async Task PlayPreviousAsync()
    {
        if (_queue.Count == 0)
        {
            ShowToast("库里没有找到视频");
            return;
        }

        if (_queueIndex <= 0)
        {
            ShowToast("已经是第一条");
            return;
        }

        _queueIndex--;
        await PlayCurrentAsync();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_isClosing)
            {
                _ = PlayNextAsync(true);
            }
        });
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Space:
                _player?.TogglePause();
                e.Handled = true;
                break;
            case Key.Left:
                _player?.SeekRelative(shift ? -60_000 : -5_000);
                e.Handled = true;
                break;
            case Key.Right:
                _player?.SeekRelative(shift ? 60_000 : 5_000);
                e.Handled = true;
                break;
            case Key.Up:
                if (_player is not null)
                {
                    _player.Volume += 5;
                    ShowToast($"音量 {_player.Volume}%");
                }
                e.Handled = true;
                break;
            case Key.Down:
                if (_player is not null)
                {
                    _player.Volume -= 5;
                    ShowToast($"音量 {_player.Volume}%");
                }
                e.Handled = true;
                break;
            case Key.N:
            case Key.PageDown:
                e.Handled = true;
                await PlayNextAsync();
                break;
            case Key.P:
            case Key.PageUp:
                e.Handled = true;
                await PlayPreviousAsync();
                break;
            case Key.F:
                EnterFullscreen();
                e.Handled = true;
                break;
            case Key.M:
                if (_player is not null)
                {
                    _player.Mute = !_player.Mute;
                    ShowToast(_player.Mute ? "已静音" : "已取消静音");
                }
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullscreen)
                {
                    ExitFullscreen();
                    e.Handled = true;
                }
                break;
            case Key.Tab:
                ShowToast("分类浮层将在下一步接入");
                e.Handled = true;
                break;
        }
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        _previousWindowState = WindowState;
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousTopmost = Topmost;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
        _isFullscreen = true;
        ShowToast("已进入全屏，按 Esc 退出");
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        Topmost = _previousTopmost;
        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        WindowState = _previousWindowState;
        _isFullscreen = false;
        ShowToast("已退出全屏");
    }

    private void UpdateProgress()
    {
        if (_player is null)
        {
            return;
        }

        long position = _player.PositionMs;
        long duration = _player.DurationMs;
        string progress = duration > 0
            ? $"{FormatTime(position)} / {FormatTime(duration)}"
            : FormatTime(position);

        ProgressText.Text = $"{progress} · 音量 {_player.Volume}%";
    }

    private static string FormatTime(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"mm\:ss");
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _progressTimer.Stop();
        _toastTimer.Stop();

        if (_player is not null)
        {
            _player.PlaybackEnded -= OnPlaybackEnded;
            try
            {
                _player.ReleaseAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _player.Dispose();
                _player = null;
            }
        }

        PlayerView.Dispose();
    }
}
