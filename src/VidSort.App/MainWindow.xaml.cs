using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private string _sidebarFilter = string.Empty;
    private List<SidebarVideoRow> _sidebarRows = new();
    private int _queueIndex = -1;
    private bool _sidebarVisible = true;
    private bool _syncingSidebar;
    private bool _isClosing;
    private bool _isAdvancing;
    private bool _isFullscreen;
    private bool _isCategoryOverlayOpen;
    private bool _isBusy;
    private bool _isProgressDragging;
    private bool _syncingProgressSlider;
    private bool _syncingVolumeSlider;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private bool _previousTopmost;

    public MainWindow()
    {
        InitializeComponent();
        ProgressSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(ProgressSlider_DragStarted));
        ProgressSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(ProgressSlider_DragCompleted));
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _player = new VlcPlayerService();
            _player.PlaybackEnded += OnPlaybackEnded;
            _player.Volume = 80;
            SyncVolumeUi(_player.Volume);
            PlayerView.MediaPlayer = _player.MediaPlayer;
        }
        catch (Exception ex)
        {
            FileNameText.Text = "libVLC 初始化失败";
            ProgressText.Text = ex.Message;
            ShowToast(ex.Message);
            return;
        }

        await LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        if (_player is null)
        {
            ShowToast("播放器尚未初始化");
            return;
        }

        AppConfig config = AppConfig.Load();
        _config = config;
        UpdateQuickArchiveButton();

        if (string.IsNullOrWhiteSpace(config.RootDir) || !Directory.Exists(config.RootDir))
        {
            RootPickerPanel.Visibility = Visibility.Visible;
            FileNameText.Text = "请选择视频库文件夹";
            ProgressText.Text = string.Empty;
            return;
        }

        RootPickerPanel.Visibility = Visibility.Collapsed;
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
            await RefreshSidebarAsync();

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

    private async void ChooseRootFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await PickRootFolderAsync();
    }

    private async void ChangeRootButton_Click(object sender, RoutedEventArgs e)
    {
        await PickRootFolderAsync();
    }

    private async Task PickRootFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择你的视频库文件夹",
            Multiselect = false
        };

        string? currentRoot = _config?.RootDir;
        if (!string.IsNullOrWhiteSpace(currentRoot) && Directory.Exists(currentRoot))
        {
            dialog.InitialDirectory = currentRoot;
        }

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        if (_config is null)
        {
            _config = AppConfig.Load();
        }

        try
        {
            _config.RootDir = Path.GetFullPath(dialog.FolderName);
            _config.Save();
        }
        catch (Exception ex)
        {
            ShowToast($"保存配置失败：{ex.Message}");
            return;
        }

        await LoadLibraryAsync();
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
        ResetProgressUi();

        try
        {
            await _player.PlayAsync(path, 0);
            _progressTimer.Start();
            UpdateProgress();
            UpdateSidebarPlayingMarker();
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

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await PlayPreviousAsync();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        _player?.TogglePause();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await PlayNextAsync();
    }

    private async void QuickArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
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

        string lastCategory = NormalizeCategoryPath(_config?.LastCategory);
        if (lastCategory.Length > 0)
        {
            string? targetDirectory = null;
            try
            {
                targetDirectory = _categoryTree.ResolveTarget(lastCategory);
            }
            catch
            {
                // 配置里的旧分类无效时退回分类浮层。
            }

            if (targetDirectory is not null && Directory.Exists(targetDirectory))
            {
                await ArchiveCurrentAsync(targetDirectory, lastCategory);
                return;
            }
        }

        OpenCategoryOverlay();
    }

    private void OpenCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCategoryOverlayOpen)
        {
            CloseCategoryOverlay();
        }
        else
        {
            OpenCategoryOverlay();
        }
    }

    private void UpdateQuickArchiveButton()
    {
        string lastCategory = NormalizeCategoryPath(_config?.LastCategory);
        QuickArchiveButton.Content = lastCategory.Length == 0
            ? "快速归档"
            : $"归档到「{lastCategory}」";
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        EnterFullscreen();
    }

    private void SidebarCloseButton_Click(object sender, RoutedEventArgs e) => SetSidebarVisible(false);

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar() => SetSidebarVisible(!_sidebarVisible);

    private void SetSidebarVisible(bool visible)
    {
        _sidebarVisible = visible;
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        CloseCategoryOverlay();
    }

    private void NewCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        CreateCategory();
    }

    private void RenameCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        RenameSelectedCategory();
    }

    private void HideCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is OverlayRow row &&
            row.RelativePath.Length > 0 &&
            _categoryTree?.IsHidden(row.RelativePath) == true)
        {
            UnhideCategory(row.RelativePath);
            return;
        }

        HideSelectedCategory();
    }

    private void RefreshCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLibrary();
    }

    private void ControlBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void CategoryOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinControlArea(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        _player?.TogglePause();
        e.Handled = true;
    }

    private bool IsWithinControlArea(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ControlBar) ||
                ReferenceEquals(source, TopCategoryButton) ||
                ReferenceEquals(source, SidebarToggleButton) ||
                ReferenceEquals(source, ChangeRootButton) ||
                ReferenceEquals(source, RootPickerPanel) ||
                ReferenceEquals(source, CategoryOverlay) ||
                ReferenceEquals(source, ToastBorder))
            {
                return true;
            }

            source = GetVisualOrLogicalParent(source);
        }

        return false;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void ProgressSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isProgressDragging = true;
    }

    private void ProgressSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isProgressDragging = false;
        SeekToProgressSlider();
        UpdateProgress();
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingProgressSlider || _player is null)
        {
            return;
        }

        SeekToProgressSlider();

        long duration = _player.DurationMs;
        if (duration > 0)
        {
            long target = (long)Math.Round(duration * e.NewValue / 1000.0);
            PlaybackTimeText.Text = $"{FormatTime(target)} / {FormatTime(duration)}";
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingVolumeSlider || _player is null)
        {
            return;
        }

        _player.Volume = (int)Math.Round(e.NewValue);
        VolumePercentText.Text = $"{_player.Volume}%";
    }

    private void SeekToProgressSlider()
    {
        if (_player is null)
        {
            return;
        }

        long duration = _player.DurationMs;
        if (duration <= 0)
        {
            return;
        }

        long target = Math.Clamp(
            (long)Math.Round(duration * ProgressSlider.Value / 1000.0),
            0,
            duration);
        _player.SeekRelative(target - _player.PositionMs);
    }

    private void ResetProgressUi()
    {
        _syncingProgressSlider = true;
        ProgressSlider.Value = 0;
        _syncingProgressSlider = false;
        PlaybackTimeText.Text = "00:00 / 00:00";
    }

    private void SyncVolumeUi(int volume)
    {
        int clamped = Math.Clamp(volume, 0, 100);
        _syncingVolumeSlider = true;
        VolumeSlider.Value = clamped;
        _syncingVolumeSlider = false;
        VolumePercentText.Text = $"{clamped}%";
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

    private Task RefreshSidebarAsync()
    {
        RefreshFolderSelector();

        _sidebarRows = _index is null
            ? new List<SidebarVideoRow>()
            : PlayQueue.Build(
                    _index.Items,
                    _sidebarFilter.Length == 0 ? null : _sidebarFilter,
                    false)
                .Select(item => new SidebarVideoRow
                {
                    RelativePath = item.RelativePath,
                    FileName = item.FileName,
                    IsPlaying = string.Equals(
                        item.RelativePath,
                        CurrentItem?.RelativePath,
                        StringComparison.Ordinal)
                })
                .ToList();

        _syncingSidebar = true;
        VideoFileList.ItemsSource = _sidebarRows;
        VideoFileList.SelectedItem = _sidebarRows.FirstOrDefault(row => row.IsPlaying);
        _syncingSidebar = false;
        SidebarCountText.Text = $"共 {_sidebarRows.Count} 个视频";
        return Task.CompletedTask;
    }

    private void RefreshFolderSelector()
    {
        var folders = new List<SidebarFolderItem>
        {
            new() { DisplayText = "全部视频", RelativePath = string.Empty }
        };
        AddSidebarFolders(_categoryNodes, 0, folders);

        _syncingSidebar = true;
        FolderSelector.ItemsSource = folders;
        SidebarFolderItem? selected = folders.FirstOrDefault(folder =>
            string.Equals(folder.RelativePath, _sidebarFilter, StringComparison.Ordinal));
        FolderSelector.SelectedItem = selected ?? folders[0];
        _sidebarFilter = selected?.RelativePath ?? string.Empty;
        _syncingSidebar = false;
    }

    private static void AddSidebarFolders(
        IEnumerable<CategoryNode> nodes,
        int depth,
        List<SidebarFolderItem> target)
    {
        foreach (CategoryNode node in nodes)
        {
            target.Add(new SidebarFolderItem
            {
                DisplayText = new string(' ', depth * 2) +
                    string.Join(" / ", node.RelativePath.Split('/')),
                RelativePath = node.RelativePath
            });
            AddSidebarFolders(node.Children, depth + 1, target);
        }
    }

    private async void FolderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebar || FolderSelector.SelectedItem is not SidebarFolderItem folder)
        {
            return;
        }

        _sidebarFilter = NormalizeCategoryPath(folder.RelativePath);
        await RefreshSidebarAsync();
    }

    private async void VideoFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebar ||
            _index is null ||
            VideoFileList.SelectedItem is not SidebarVideoRow row)
        {
            return;
        }

        _queue = PlayQueue.Build(
            _index.Items,
            _sidebarFilter.Length == 0 ? null : _sidebarFilter,
            false);
        _queueIndex = _queue.FindIndex(item =>
            string.Equals(item.RelativePath, row.RelativePath, StringComparison.Ordinal));
        if (_queueIndex >= 0)
        {
            await PlayCurrentAsync();
        }
    }

    private void UpdateSidebarPlayingMarker()
    {
        string? playingPath = CurrentItem?.RelativePath;
        foreach (SidebarVideoRow row in _sidebarRows)
        {
            row.IsPlaying = string.Equals(row.RelativePath, playingPath, StringComparison.Ordinal);
        }
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

    private static string GetArchivedCategoryLabel(string relativePath)
    {
        string normalized = NormalizeCategoryPath(relativePath);
        return normalized.Length == 0 ? CategoryOverlayModel.RootLabel : normalized;
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

    private void CategoryRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: OverlayRow row })
        {
            CategoryList.SelectedItem = row;
        }
    }

    private async void CategoryRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: OverlayRow row })
        {
            return;
        }

        CategoryList.SelectedItem = row;
        e.Handled = true;
        await ArchiveSelectedCategoryAsync();
    }

    private void CategoryRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: OverlayRow row } item)
        {
            return;
        }

        CategoryList.SelectedItem = row;
        ContextMenu menu = CreateCategoryContextMenu(row);
        menu.PlacementTarget = item;
        item.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu CreateCategoryContextMenu(OverlayRow row)
    {
        bool isSpecialRow = row.IsRefresh || IsNewCategoryRow(row);
        bool isNamedCategory = !isSpecialRow && row.RelativePath.Length > 0;
        var menu = new ContextMenu();

        var newChild = new MenuItem
        {
            Header = "在此新建子分类",
            Tag = row,
            IsEnabled = !isSpecialRow
        };
        newChild.Click += CategoryContextNewChild_Click;

        var rename = new MenuItem
        {
            Header = "重命名",
            Tag = row,
            IsEnabled = isNamedCategory
        };
        rename.Click += CategoryContextRename_Click;

        var hide = new MenuItem
        {
            Header = "从菜单隐藏",
            Tag = row,
            IsEnabled = isNamedCategory
        };
        hide.Click += CategoryContextHide_Click;

        var open = new MenuItem
        {
            Header = "在资源管理器中打开",
            Tag = row,
            IsEnabled = !isSpecialRow
        };
        open.Click += CategoryContextOpenInExplorer_Click;

        menu.Items.Add(newChild);
        menu.Items.Add(rename);
        menu.Items.Add(hide);
        menu.Items.Add(open);
        return menu;
    }

    private void CategoryContextNewChild_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OverlayRow row } ||
            row.IsRefresh ||
            IsNewCategoryRow(row))
        {
            return;
        }

        CategoryList.SelectedItem = row;
        _lastSelectedCategoryRelativePath = NormalizeCategoryPath(row.RelativePath);
        CreateCategory();
    }

    private void CategoryContextRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OverlayRow row } ||
            row.IsRefresh ||
            IsNewCategoryRow(row) ||
            row.RelativePath.Length == 0)
        {
            return;
        }

        CategoryList.SelectedItem = row;
        RenameSelectedCategory();
    }

    private void CategoryContextHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OverlayRow row } ||
            row.IsRefresh ||
            IsNewCategoryRow(row) ||
            row.RelativePath.Length == 0)
        {
            return;
        }

        CategoryList.SelectedItem = row;
        HideSelectedCategory();
    }

    private void CategoryContextOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OverlayRow row } ||
            row.IsRefresh ||
            IsNewCategoryRow(row))
        {
            return;
        }

        OpenCategoryInExplorer(row);
    }

    private void OpenCategoryInExplorer(OverlayRow row)
    {
        if (_categoryTree is null)
        {
            ShowToast("视频库尚未加载");
            return;
        }

        try
        {
            string target = _categoryTree.ResolveTarget(row.RelativePath);
            if (!Directory.Exists(target))
            {
                ShowToast("分类目录不存在");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"打开资源管理器失败：{ex.Message}");
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

        bool archived = await ArchiveCurrentAsync(targetDirectory, row.RelativePath);
        if (archived)
        {
            CloseCategoryOverlay();
        }
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

    private async Task<bool> ArchiveCurrentAsync(string targetDirectory, string categoryRelativePath)
    {
        if (_archiveCoordinator is null || _rootDir is null || CurrentItem is null)
        {
            ShowToast("当前没有可归档的视频");
            return false;
        }

        VideoItem item = CurrentItem;
        string sourcePath = Path.Combine(
            _rootDir,
            item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        _isBusy = true;
        try
        {
            _player?.Pause();
            MoveOutcome outcome = await _archiveCoordinator.ArchiveAsync(item, targetDirectory);
            if (!outcome.Ok)
            {
                ShowToast(outcome.Message ?? Mover.Human(outcome.Error));
                return false;
            }

            RememberLastCategory(categoryRelativePath);

            if (!outcome.NoOp && outcome.DestPath is not null)
            {
                _journal.Record(sourcePath, outcome.DestPath);
                SaveJournal();
            }

            bool canUndo = !outcome.NoOp && outcome.DestPath is not null;
            string archiveMessage = $"已移动到 {GetArchivedCategoryLabel(categoryRelativePath)}";

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
                await RefreshSidebarAsync();
                ShowToast(archiveMessage, canUndo);
                return true;
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
                await RefreshSidebarAsync();
                ShowToast(archiveMessage, canUndo);
                return true;
            }

            await PlayCurrentAsync();
            RefreshOverlayRows(categoryRelativePath);
            await RefreshSidebarAsync();
            ShowToast(archiveMessage, canUndo);
            return true;
        }
        catch (Exception ex)
        {
            ShowToast($"归档失败：{ex.Message}");
            return false;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void RememberLastCategory(string relativePath)
    {
        if (_config is null)
        {
            return;
        }

        _config.LastCategory = NormalizeCategoryPath(relativePath);
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            ShowToast($"保存配置失败：{ex.Message}");
        }

        UpdateQuickArchiveButton();
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

        bool focusInSlider = IsKeyboardFocusWithinSlider();
        switch (e.Key)
        {
            case Key.Space:
                _player?.TogglePause();
                e.Handled = true;
                break;
            case Key.Left:
                if (focusInSlider)
                {
                    break;
                }

                _player?.SeekRelative(shift ? -60_000 : -5_000);
                e.Handled = true;
                break;
            case Key.Right:
                if (focusInSlider)
                {
                    break;
                }

                _player?.SeekRelative(shift ? 60_000 : 5_000);
                e.Handled = true;
                break;
            case Key.Up:
                if (focusInSlider)
                {
                    break;
                }

                if (_player is not null)
                {
                    _player.Volume += 5;
                    SyncVolumeUi(_player.Volume);
                    ShowToast($"音量 {_player.Volume}%");
                }
                e.Handled = true;
                break;
            case Key.Down:
                if (focusInSlider)
                {
                    break;
                }

                if (_player is not null)
                {
                    _player.Volume -= 5;
                    SyncVolumeUi(_player.Volume);
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
            case Key.B:
                ToggleSidebar();
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

    private static bool IsKeyboardFocusWithinSlider()
    {
        DependencyObject? current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is Slider)
            {
                return true;
            }

            current = GetVisualOrLogicalParent(current);
        }

        return false;
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
        long displayedPosition = _isProgressDragging && duration > 0
            ? (long)Math.Round(duration * ProgressSlider.Value / 1000.0)
            : position;

        PlaybackTimeText.Text = $"{FormatTime(displayedPosition)} / {FormatTime(duration)}";
        ProgressText.Text = _player.IsPlaying ? "播放中" : "已暂停";
        VolumePercentText.Text = $"{_player.Volume}%";

        if (!_isProgressDragging && duration > 0)
        {
            _syncingProgressSlider = true;
            ProgressSlider.Value = Math.Clamp(position * 1000.0 / duration, 0, 1000);
            _syncingProgressSlider = false;
        }
    }

    private static string FormatTime(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"mm\:ss");
    }

    private void ShowToast(string message, bool showUndo = false)
    {
        ToastText.Text = message;
        ToastUndoButton.Visibility = showUndo ? Visibility.Visible : Visibility.Collapsed;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async void ToastUndo_Click(object sender, RoutedEventArgs e)
    {
        await UndoLastArchiveAsync();
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
