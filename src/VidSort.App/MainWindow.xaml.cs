using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VidSort.App.Services;
using VidSort.App.Views;
using VidSort.Core;

namespace VidSort.App;

public partial class MainWindow : Window
{
    private const string NewCategoryRowLabel = "＋ 新建分类…";
    private const string NewCategoryRowMarker = "\0new-category";
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Mover _mover = new();
    private VlcPlayerService? _player;
    private List<VideoItem> _queue = new();
    private List<OverlayRow> _overlayRows = new();
    private ArchiveJournal _journal = new();
    private ArchiveCoordinator? _archiveCoordinator;
    private LibraryIndex? _index;
    private CategoryTree? _categoryTree;
    private List<CategoryNode> _categoryNodes = new();
    private AppConfig? _config;
    private string? _journalPath;
    private string? _rootDir;
    private string _lastSelectedCategoryRelativePath = string.Empty;
    private int _queueIndex = -1;
    private bool _isClosing;
    private bool _isAdvancing;
    private bool _isFullscreen;
    private bool _isCategoryOverlayOpen;
    private bool _isBusy;
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
        _config = config;

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
            _journalPath = Path.Combine(
                Path.GetDirectoryName(AppConfig.ConfigPath) ?? AppContext.BaseDirectory,
                "journal.json");

            try
            {
                _journal = ArchiveJournal.Load(_journalPath);
            }
            catch
            {
                _journal = new ArchiveJournal();
            }

            _index = new LibraryIndex(_rootDir);
            ScanResult scan = _index.Scan();
            RescanCategories();
            _archiveCoordinator = new ArchiveCoordinator(_player, _mover, _index);
            _queue = PlayQueue.Build(scan.Items, null, false);
            RefreshOverlayRows();

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

    private async Task<bool> PlayCurrentAsync()
    {
        if (_player is null || _rootDir is null || _queueIndex < 0 || _queueIndex >= _queue.Count)
        {
            return false;
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
            return true;
        }
        catch (Exception ex)
        {
            _progressTimer.Stop();
            ProgressText.Text = "播放失败";
            ShowToast($"播放失败：{ex.Message}");
            return false;
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

    private VideoItem? CurrentItem =>
        _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null;

    private void RescanCategories()
    {
        if (_rootDir is null)
        {
            return;
        }

        _categoryTree = new CategoryTree(_rootDir, _config?.HiddenCategories);
        _categoryNodes = _categoryTree.Scan();
    }

    private void RefreshOverlayRows(string? preferredRelativePath = null)
    {
        string currentCategory = NormalizeCategoryPath(CurrentItem?.CategoryRelativePath);
        _overlayRows = CategoryOverlayModel.Build(_categoryNodes, currentCategory);

        if (_config?.ShowHidden == true && _categoryTree is not null)
        {
            var hiddenRows = _categoryTree.Hidden
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => CreateHiddenRow(path, currentCategory))
                .ToList();
            int refreshIndex = _overlayRows.FindIndex(row => row.IsRefresh);
            _overlayRows.InsertRange(refreshIndex >= 0 ? refreshIndex : _overlayRows.Count, hiddenRows);
        }

        _overlayRows.Add(new OverlayRow
        {
            DisplayText = NewCategoryRowLabel,
            RelativePath = NewCategoryRowMarker
        });

        CategoryList.ItemsSource = _overlayRows;

        int selectedIndex = preferredRelativePath is null
            ? -1
            : _overlayRows.FindIndex(row =>
                !row.IsRefresh &&
                !IsNewCategoryRow(row) &&
                string.Equals(row.RelativePath, preferredRelativePath, StringComparison.Ordinal));
        if (selectedIndex < 0)
        {
            selectedIndex = _overlayRows.FindIndex(row => row.IsSelected);
        }

        CategoryList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        if (CategoryList.SelectedIndex >= 0)
        {
            CategoryList.ScrollIntoView(_overlayRows[CategoryList.SelectedIndex]);
        }
    }

    private static OverlayRow CreateHiddenRow(string relativePath, string currentCategory)
    {
        string normalized = NormalizeCategoryPath(relativePath);
        int depth = normalized.Count(ch => ch == '/');
        string indent = string.Concat(Enumerable.Repeat("　　", depth));
        bool isSelected = string.Equals(normalized, currentCategory, StringComparison.Ordinal);

        return new OverlayRow
        {
            DisplayText = (isSelected ? "● " : string.Empty) + indent + "[隐藏] " + GetCategoryName(normalized),
            RelativePath = normalized,
            IsSelected = isSelected
        };
    }

    private static bool IsNewCategoryRow(OverlayRow row) =>
        string.Equals(row.RelativePath, NewCategoryRowMarker, StringComparison.Ordinal);

    private static string NormalizeCategoryPath(string? relativePath) =>
        (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');

    private static string GetCategoryName(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash >= 0 ? relativePath[(slash + 1)..] : relativePath;
    }

    private void CategoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isCategoryOverlayOpen || CategoryList.SelectedItem is not OverlayRow row)
        {
            return;
        }

        if (!row.IsRefresh && !IsNewCategoryRow(row))
        {
            _lastSelectedCategoryRelativePath = NormalizeCategoryPath(row.RelativePath);
        }
    }

    private void OpenCategoryOverlay()
    {
        if (_index is null || _rootDir is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        _lastSelectedCategoryRelativePath = NormalizeCategoryPath(CurrentItem?.CategoryRelativePath);
        _isCategoryOverlayOpen = true;
        RefreshOverlayRows();
        CategoryOverlay.Visibility = Visibility.Visible;
        CategoryList.Focus();
        Dispatcher.InvokeAsync(() =>
        {
            if (_isCategoryOverlayOpen)
            {
                CategoryList.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void CloseCategoryOverlay()
    {
        _isCategoryOverlayOpen = false;
        CategoryOverlay.Visibility = Visibility.Collapsed;
        if (!PlayerView.Focus())
        {
            Focus();
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (!_isCategoryOverlayOpen && !PlayerView.Focus())
            {
                Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void MoveOverlaySelection(int delta)
    {
        if (_overlayRows.Count == 0)
        {
            return;
        }

        int index = CategoryList.SelectedIndex;
        index = index < 0 ? 0 : Math.Clamp(index + delta, 0, _overlayRows.Count - 1);
        CategoryList.SelectedIndex = index;
        CategoryList.ScrollIntoView(_overlayRows[index]);
        CategoryList.Focus();
    }

    private async Task ArchiveSelectedCategoryAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (CategoryList.SelectedItem is not OverlayRow row)
        {
            ShowToast("请选择分类");
            return;
        }

        if (row.IsRefresh)
        {
            RefreshLibrary();
            return;
        }

        if (IsNewCategoryRow(row))
        {
            CreateCategory();
            return;
        }

        if (_categoryTree is not null &&
            row.RelativePath.Length > 0 &&
            _categoryTree.IsHidden(row.RelativePath))
        {
            UnhideCategory(row.RelativePath);
            return;
        }

        if (_archiveCoordinator is null || _categoryTree is null || _rootDir is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        if (CurrentItem is null)
        {
            ShowToast("当前没有可归档的视频");
            return;
        }

        string targetDirectory;
        try
        {
            targetDirectory = _categoryTree.ResolveTarget(row.RelativePath);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            return;
        }

        await ArchiveCurrentAsync(targetDirectory, row.RelativePath);
    }

    private void CreateCategory()
    {
        if (_categoryTree is null || _config is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        string parentRelativePath = _lastSelectedCategoryRelativePath;
        string parentLabel = parentRelativePath.Length == 0
            ? "库根"
            : GetCategoryName(parentRelativePath);
        string? name = TextInputDialog.Show(
            this,
            $"在 {parentLabel} 下新建分类",
            string.Empty,
            CategoryName.Validate);
        if (name is null)
        {
            CategoryList.Focus();
            return;
        }

        try
        {
            string relativePath = _categoryTree.Register(parentRelativePath, name);
            string target = _categoryTree.ResolveTarget(relativePath);
            if (Directory.Exists(target) || File.Exists(target))
            {
                ShowToast($"分类已存在：{name}");
                CategoryList.Focus();
                return;
            }

            Directory.CreateDirectory(target);
            _config.Save();
            RescanCategories();
            RefreshOverlayRows(relativePath);
            ShowToast($"已新建分类 {name}");
        }
        catch (Exception ex)
        {
            ShowToast($"新建分类失败：{ex.Message}");
        }

        CategoryList.Focus();
    }

    private void RenameSelectedCategory()
    {
        if (_categoryTree is null || _config is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        if (CategoryList.SelectedItem is not OverlayRow row)
        {
            ShowToast("请选择分类");
            return;
        }

        if (row.IsRefresh || IsNewCategoryRow(row) || row.RelativePath.Length == 0)
        {
            ShowToast("这一行不能改名");
            return;
        }

        string oldName = GetCategoryName(row.RelativePath);
        string? newName = TextInputDialog.Show(
            this,
            "重命名分类",
            oldName,
            CategoryName.Validate);
        if (newName is null)
        {
            CategoryList.Focus();
            return;
        }

        try
        {
            string newRelativePath = _categoryTree.Rename(row.RelativePath, newName);
            _config.HiddenCategories = _categoryTree.ExportHidden().ToArray();
            _config.Save();
            RescanCategories();
            RefreshOverlayRows(newRelativePath);
            ShowToast($"已重命名为 {newName}");
        }
        catch (Exception ex)
        {
            ShowToast($"重命名失败：{ex.Message}");
        }

        CategoryList.Focus();
    }

    private void HideSelectedCategory()
    {
        if (_categoryTree is null || _config is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        if (CategoryList.SelectedItem is not OverlayRow row)
        {
            ShowToast("请选择分类");
            return;
        }

        if (row.IsRefresh || IsNewCategoryRow(row) || row.RelativePath.Length == 0)
        {
            ShowToast("这一行不能隐藏");
            return;
        }

        try
        {
            _categoryTree.Hide(row.RelativePath);
            _config.HiddenCategories = _categoryTree.ExportHidden().ToArray();
            _config.Save();
            RescanCategories();
            RefreshOverlayRows(row.RelativePath);
            ShowToast("已从菜单隐藏（磁盘目录未动）");
        }
        catch (Exception ex)
        {
            ShowToast($"隐藏失败：{ex.Message}");
        }

        CategoryList.Focus();
    }

    private void UnhideCategory(string relativePath)
    {
        if (_categoryTree is null || _config is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        string categoryName = GetCategoryName(NormalizeCategoryPath(relativePath));
        try
        {
            _categoryTree.Unhide(relativePath);
            _config.HiddenCategories = _categoryTree.ExportHidden().ToArray();
            _config.Save();
            RescanCategories();
            RefreshOverlayRows(relativePath);
            ShowToast($"已恢复分类 {categoryName}");
        }
        catch (Exception ex)
        {
            ShowToast($"恢复分类失败：{ex.Message}");
        }

        CategoryList.Focus();
    }

    private void ToggleShowHidden()
    {
        if (_config is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        bool previous = _config.ShowHidden;
        _config.ShowHidden = !previous;
        try
        {
            _config.Save();
            RescanCategories();
            RefreshOverlayRows();
            ShowToast(_config.ShowHidden ? "已显示隐藏分类" : "已收起隐藏分类");
        }
        catch (Exception ex)
        {
            _config.ShowHidden = previous;
            ShowToast($"保存配置失败：{ex.Message}");
        }

        CategoryList.Focus();
    }

    private async Task ArchiveCurrentAsync(string targetDirectory, string categoryRelativePath)
    {
        if (_archiveCoordinator is null || _rootDir is null || CurrentItem is null)
        {
            ShowToast("当前没有可归档的视频");
            return;
        }

        VideoItem item = CurrentItem;
        string sourcePath = Path.Combine(
            _rootDir,
            item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        _isBusy = true;
        try
        {
            MoveOutcome outcome = await _archiveCoordinator.ArchiveAsync(item, targetDirectory);
            if (!outcome.Ok)
            {
                ShowToast(outcome.Message ?? Mover.Human(outcome.Error));
                return;
            }

            if (!outcome.NoOp && outcome.DestPath is not null)
            {
                _journal.Record(sourcePath, outcome.DestPath);
                SaveJournal();
            }

            _queue.RemoveAt(_queueIndex);

            if (_queue.Count == 0)
            {
                _queueIndex = -1;
                _progressTimer.Stop();
                FileNameText.Text = "已全部归档";
                ProgressText.Text = string.Empty;
                if (_player is not null)
                {
                    await _player.ReleaseAsync();
                }

                RefreshOverlayRows();
                ShowToast("已全部归档");
                return;
            }

            if (_queueIndex >= _queue.Count)
            {
                _queueIndex = _queue.Count;
                _progressTimer.Stop();
                FileNameText.Text = "已归档";
                ProgressText.Text = "没有下一条视频";
                if (_player is not null)
                {
                    await _player.ReleaseAsync();
                }

                RefreshOverlayRows();
                ShowToast("已归档");
                return;
            }

            await PlayCurrentAsync();
            RefreshOverlayRows(categoryRelativePath);
        }
        catch (Exception ex)
        {
            ShowToast($"归档失败：{ex.Message}");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void RefreshLibrary()
    {
        if (_index is null || _rootDir is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        try
        {
            ScanResult scan = _index.Scan(_index.Items);
            RescanCategories();
            RebindQueue(scan.Items);
            RefreshOverlayRows();
            ShowToast($"已刷新，共 {scan.Items.Count} 个视频");
        }
        catch (Exception ex)
        {
            ShowToast($"刷新失败：{ex.Message}");
        }
    }

    private void RebindQueue(IReadOnlyList<VideoItem> scannedItems)
    {
        string? currentRelativePath = CurrentItem?.RelativePath;
        var scannedByPath = new Dictionary<string, VideoItem>(StringComparer.Ordinal);
        foreach (VideoItem item in scannedItems)
        {
            scannedByPath[item.RelativePath] = item;
        }

        _queue = _queue
            .Where(item => scannedByPath.ContainsKey(item.RelativePath))
            .Select(item => scannedByPath[item.RelativePath])
            .ToList();

        if (_queue.Count == 0)
        {
            _queueIndex = -1;
            return;
        }

        int currentIndex = _queue.FindIndex(item =>
            currentRelativePath is not null &&
            string.Equals(item.RelativePath, currentRelativePath, StringComparison.Ordinal));
        if (currentIndex >= 0)
        {
            _queueIndex = currentIndex;
        }
        else if (_queueIndex < 0)
        {
            _queueIndex = 0;
        }
        else
        {
            _queueIndex = Math.Clamp(_queueIndex, 0, _queue.Count - 1);
        }
    }

    private int InsertQueueItem(VideoItem item)
    {
        int insertAt = _queue.FindIndex(existing =>
            string.CompareOrdinal(existing.RelativePath, item.RelativePath) > 0);
        if (insertAt < 0)
        {
            insertAt = _queue.Count;
        }

        _queue.Insert(insertAt, item);
        if (_queueIndex >= insertAt)
        {
            _queueIndex++;
        }

        return insertAt;
    }

    private async Task UndoLastArchiveAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (_journal.Count == 0)
        {
            ShowToast("没有可撤销的归档");
            return;
        }

        if (_index is null || _rootDir is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        bool hadCurrentItem = CurrentItem is not null;

        _isBusy = true;
        try
        {
            MoveOutcome outcome = await ArchiveUndo.UndoLastAsync(_journal, _mover);
            SaveJournal();
            if (!outcome.Ok)
            {
                ShowToast(outcome.Message ?? Mover.Human(outcome.Error));
                return;
            }

            ScanResult scan = _index.Scan(_index.Items);
            RescanCategories();
            RebindQueue(scan.Items);
            int restoredIndex = -1;
            VideoItem? restored = FindRestoredItem(scan.Items, outcome.DestPath);
            if (restored is not null &&
                !_queue.Any(item => string.Equals(item.RelativePath, restored.RelativePath, StringComparison.Ordinal)))
            {
                restoredIndex = InsertQueueItem(restored);
            }

            RefreshOverlayRows();
            ShowToast("已撤销");

            if (!hadCurrentItem && _queue.Count > 0)
            {
                _queueIndex = restoredIndex >= 0 ? restoredIndex : 0;
                await PlayCurrentAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"撤销失败：{ex.Message}");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private VideoItem? FindRestoredItem(IEnumerable<VideoItem> items, string? restoredPath)
    {
        if (_rootDir is null || string.IsNullOrWhiteSpace(restoredPath))
        {
            return null;
        }

        string relativePath = Path.GetRelativePath(_rootDir, Path.GetFullPath(restoredPath))
            .Replace(Path.DirectorySeparatorChar, '/');
        return items.FirstOrDefault(item =>
            string.Equals(item.RelativePath, relativePath, StringComparison.Ordinal));
    }

    private void SaveJournal()
    {
        if (_journalPath is null)
        {
            return;
        }

        try
        {
            _journal.Save(_journalPath);
        }
        catch (Exception ex)
        {
            ShowToast($"保存撤销记录失败：{ex.Message}");
        }
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        if (_isClosing || _isBusy)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => _ = AdvanceFromEndAsync()));
    }

    private async Task AdvanceFromEndAsync()
    {
        if (_isAdvancing)
        {
            return;
        }

        _isAdvancing = true;
        try
        {
            if (_isClosing)
            {
                return;
            }

            if (_queue.Count == 0)
            {
                ShowToast("库里没有找到视频");
                return;
            }

            if (_queueIndex + 1 >= _queue.Count)
            {
                _progressTimer.Stop();
                ProgressText.Text = "已播放完";
                ShowToast("视频播放完毕");
                return;
            }

            _queueIndex++;
            bool started = await PlayCurrentAsync();
            if (!started || _isClosing)
            {
                if (!_isClosing)
                {
                    ShowToast("自动续播失败，按 N 手动切换");
                }

                return;
            }

            await Task.Delay(3000);
            if (!_isClosing && _player is not null &&
                !_player.IsPlaying && !string.IsNullOrWhiteSpace(_player.CurrentPath))
            {
                ShowToast("自动续播失败，按 N 手动切换");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"自动续播失败：{ex.Message}");
        }
        finally
        {
            _isAdvancing = false;
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (control && e.Key == Key.Z)
        {
            e.Handled = true;
            await UndoLastArchiveAsync();
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            RefreshLibrary();
            return;
        }

        if (_isCategoryOverlayOpen)
        {
            switch (e.Key)
            {
                case Key.Tab:
                case Key.Escape:
                    CloseCategoryOverlay();
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveOverlaySelection(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    MoveOverlaySelection(1);
                    e.Handled = true;
                    return;
                case Key.F2:
                    RenameSelectedCategory();
                    e.Handled = true;
                    return;
                case Key.Delete:
                    HideSelectedCategory();
                    e.Handled = true;
                    return;
                case Key.H when shift:
                    ToggleShowHidden();
                    e.Handled = true;
                    return;
                case Key.Enter:
                    e.Handled = true;
                    await ArchiveSelectedCategoryAsync();
                    return;
            }
        }

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
                OpenCategoryOverlay();
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
