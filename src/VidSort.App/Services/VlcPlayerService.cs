using System.IO;
using LibVLCSharp.Shared;
using VidSort.Core;

namespace VidSort.App.Services;

public sealed class VlcPlayerService : IPlayer, IDisposable
{
    private readonly object _gate = new();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _media;
    private long _pendingSeekMs;
    private string? _currentPath;
    private bool _releasing;
    private bool _disposed;
    private volatile bool _ending;

    public event EventHandler? PlaybackEnded;

    public VlcPlayerService()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception defaultPathError)
        {
            try
            {
                LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
            }
            catch (Exception localPathError)
            {
                throw new InvalidOperationException(
                    $"libVLC 初始化失败：{localPathError.Message}",
                    new AggregateException(defaultPathError, localPathError));
            }
        }

        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.EndReached += OnEndReached;
    }

    public MediaPlayer MediaPlayer => _mediaPlayer;

    public long PositionMs
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? 0 : Math.Max(0, _mediaPlayer.Time);
            }
        }
    }

    public long DurationMs
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? 0 : Math.Max(0, _mediaPlayer.Length);
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _mediaPlayer.IsPlaying;
            }
        }
    }

    public int Volume
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? 0 : Math.Clamp(_mediaPlayer.Volume, 0, 100);
            }
        }
        set
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _mediaPlayer.Volume = Math.Clamp(value, 0, 100);
                }
            }
        }
    }

    public bool Mute
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _mediaPlayer.Mute;
            }
        }
        set
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _mediaPlayer.Mute = value;
                }
            }
        }
    }

    public string? CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _currentPath;
            }
        }
    }

    public async Task PlayAsync(string absolutePath, long startMs)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            throw new ArgumentException("视频路径不能为空", nameof(absolutePath));
        }

        if (_ending)
        {
            await Task.Delay(120).ConfigureAwait(false);
        }

        await ReleaseAsync().ConfigureAwait(false);

        Media? media = null;
        bool attached = false;
        try
        {
            media = new Media(_libVlc, absolutePath, FromType.FromPath, Array.Empty<string>());
            Log("PlayBegin");

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(VlcPlayerService));
                }

                _media = media;
                _pendingSeekMs = startMs > 0 ? startMs : -1;
                _currentPath = absolutePath;
                attached = true;
            }

            bool started = await Task.Run(() => _mediaPlayer.Play(media)).ConfigureAwait(false);
            if (!started)
            {
                throw new InvalidOperationException($"无法播放视频：{absolutePath}");
            }

            Log("PlayEnd");
            _ending = false;
        }
        catch
        {
            Log("PlayFailed");
            if (attached)
            {
                try
                {
                    await ReleaseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 保留原始播放异常。
                }
            }
            else
            {
                media?.Dispose();
            }

            throw;
        }
    }

    public async Task ReleaseAsync()
    {
        Log("ReleaseBegin");
        Media? media;
        lock (_gate)
        {
            if (_disposed || (_media is null && _currentPath is null))
            {
                return;
            }

            _releasing = true;
            _pendingSeekMs = -1;
            _currentPath = null;
            media = _media;
            _media = null;
        }

        Task<Exception?> releaseTask = Task.Run(() => ReleasePlayer(media));
        Task completed = await Task.WhenAny(releaseTask, Task.Delay(3000)).ConfigureAwait(false);
        if (completed != releaseTask)
        {
            lock (_gate)
            {
                _releasing = false;
                _ending = false;
            }

            WriteReleaseTimeoutLog();
            return;
        }

        Exception? failure = await releaseTask.ConfigureAwait(false);
        lock (_gate)
        {
            _releasing = false;
            _ending = false;
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("释放视频资源失败。", failure);
        }

        Log("ReleaseEnd");
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_disposed && _currentPath is not null)
            {
                _mediaPlayer.Pause();
            }
        }
    }

    public void TogglePause()
    {
        lock (_gate)
        {
            if (!_disposed && _currentPath is not null)
            {
                _mediaPlayer.Pause();
            }
        }
    }

    public void SeekRelative(long deltaMs)
    {
        lock (_gate)
        {
            if (_disposed || _currentPath is null)
            {
                return;
            }

            long target = Math.Max(0, _mediaPlayer.Time + deltaMs);
            long length = _mediaPlayer.Length;
            if (length > 0 && target > length)
            {
                target = length;
            }

            _mediaPlayer.Time = target;
        }
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        long seekMs;
        lock (_gate)
        {
            seekMs = _pendingSeekMs;
            _pendingSeekMs = -1;
        }

        if (seekMs > 0)
        {
            try
            {
                _mediaPlayer.Time = seekMs;
            }
            catch
            {
                // 播放器已经切换媒体时，忽略迟到的 seek。
            }
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        bool notify;
        lock (_gate)
        {
            notify = !_releasing && !_disposed;
            if (notify)
            {
                _ending = true;
            }
        }

        if (!notify)
        {
            return;
        }

        Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            EndReachedSafe();
        });
    }

    private void EndReachedSafe()
    {
        bool notify;
        lock (_gate)
        {
            notify = !_releasing && !_disposed && _ending;
        }

        if (notify)
        {
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private Exception? ReleasePlayer(Media? media)
    {
        Exception? failure = null;
        try
        {
            Log("StopBegin");
            _mediaPlayer.Stop();
            Log("StopEnd");
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool clearMedia;
        lock (_gate)
        {
            clearMedia = _media is null && _currentPath is null;
        }

        if (clearMedia)
        {
            try
            {
                _mediaPlayer.Media = null;
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        try
        {
            media?.Dispose();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        return failure;
    }

    private static void WriteReleaseTimeoutLog()
    {
        try
        {
            string directory = Path.GetDirectoryName(AppConfig.ConfigPath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ReleaseAsync timeout{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "logs.txt"), line);
        }
        catch
        {
            // 诊断日志失败不应影响释放流程。
        }
    }

    private static void Log(string step)
    {
        try
        {
            string directory = Path.GetDirectoryName(AppConfig.ConfigPath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            string line = $"{DateTime.Now:HH:mm:ss.fff} {step}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "logs.txt"), line);
        }
        catch
        {
            // 诊断日志失败不应影响流程。
        }
    }

    public void Dispose()
    {
        Media? media;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _releasing = true;
            _pendingSeekMs = -1;
            _currentPath = null;
            media = _media;
            _media = null;
        }

        _mediaPlayer.Playing -= OnPlaying;
        _mediaPlayer.EndReached -= OnEndReached;

        try
        {
            _mediaPlayer.Stop();
        }
        catch
        {
            // Dispose 期间继续释放其余原生资源。
        }

        try
        {
            _mediaPlayer.Media = null;
        }
        catch
        {
            // MediaPlayer 可能已进入释放流程。
        }

        media?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
