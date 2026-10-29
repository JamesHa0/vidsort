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

        ReleaseCore();

        Media? media = null;
        bool attached = false;
        try
        {
            media = new Media(_libVlc, absolutePath, FromType.FromPath, Array.Empty<string>());

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

            if (!_mediaPlayer.Play(media))
            {
                throw new InvalidOperationException($"无法播放视频：{absolutePath}");
            }

            _ending = false;
        }
        catch
        {
            if (attached)
            {
                ReleaseCore();
            }
            else
            {
                media?.Dispose();
            }

            throw;
        }
    }

    public Task ReleaseAsync()
    {
        ReleaseCore();
        return Task.CompletedTask;
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

    private void ReleaseCore()
    {
        Media? media;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _releasing = true;
            _pendingSeekMs = -1;
            _currentPath = null;
            media = _media;
            _media = null;
        }

        Exception? failure = null;
        try
        {
            _mediaPlayer.Stop();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            _mediaPlayer.Media = null;
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        media?.Dispose();
        lock (_gate)
        {
            _releasing = false;
            _ending = false;
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("释放视频资源失败。", failure);
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
