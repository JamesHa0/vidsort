using VidSort.Core;

namespace VidSort.Core.Tests;

internal sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{label}: 期望 <{expected}>，实际 <{actual}>");
        }
    }

    public static void FileExists(string path, string label)
    {
        True(File.Exists(path), $"{label}: 文件不存在: {path}");
    }

    public static void FileMissing(string path, string label)
    {
        False(File.Exists(path), $"{label}: 文件不应存在: {path}");
    }

    public static void FileLength(string path, long expected, string label)
    {
        FileExists(path, label);
        Equal(expected, new FileInfo(path).Length, $"{label} 大小");
    }
}

internal sealed class SyncProgress : IProgress<MoveProgress>
{
    private readonly Action<MoveProgress> _onReport;

    public SyncProgress(Action<MoveProgress> onReport)
    {
        _onReport = onReport;
    }

    public void Report(MoveProgress value) => _onReport(value);
}

internal static class Program
{
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "vidsort-core-tests", RunId);
    private static readonly string HomeRoot = Path.Combine(AppContext.BaseDirectory, "vidsort-test-tmp", RunId);
    private static readonly string ShmRoot = Path.Combine("/dev/shm", "vidsort-core-tests", RunId);

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("同卷判定：同一目录树内 → 同卷", SameVolumeWithinTreeAsync),
            ("同卷判定：Path.GetTempPath() 下 与 /dev/shm 下 → 不同卷", DifferentVolumeAsync),
            ("基本归档：自动创建目标目录并移动源文件", BasicArchiveAsync),
            ("中文 + 空格 + 三级目录归档", ChineseSpacesDeepDirectoryAsync),
            ("同名冲突（默认 Rename）", ConflictRenameAsync),
            ("同名冲突（Skip）", ConflictSkipAsync),
            ("同名冲突（Overwrite）", ConflictOverwriteAsync),
            ("幂等：源已在目标目录", AlreadyInPlaceAsync),
            ("源不存在", SourceMissingAsync),
            ("目标目录只读", ReadOnlyTargetAsync),
            ("跨卷复制（/home → /dev/shm）", CrossVolumeCopyAsync),
            ("强制跨卷分支（同盘也复制）", ForcedCopyBranchAsync),
            ("复制中途取消", CancelDuringCopyAsync),
            ("分类树：扫描目录树并过滤隐藏目录", CategoryTests.ScanAsync),
            ("分类树：隐藏与恢复", CategoryTests.HideUnhideAsync),
            ("分类名：非法名全部拦截", CategoryTests.InvalidNamesAsync),
            ("分类名：合法名放行", CategoryTests.ValidNamesAsync),
            ("分类树：新建分类惰性", CategoryTests.RegisterLazyAsync),
            ("分类树：ResolveTarget 解析", CategoryTests.ResolveTargetAsync),
            ("分类树：重命名并同步隐藏列表", CategoryTests.RenameAsync),
            ("分类树：层级上限", CategoryTests.DepthLimitAsync),
            ("扩展名白名单", CategoryTests.VideoExtensionWhitelistAsync),
            ("索引扫描：过滤规则与分类路径", IndexTests.ScanFiltersAndCategoriesAsync),
            ("索引增量：未变项复用元数据", IndexTests.IncrementalReusesMetadataAsync),
            ("索引增量：变更项清空元数据", IndexTests.IncrementalChangeClearsMetadataAsync),
            ("索引持久化：Save/Load 往返", IndexTests.SaveLoadRoundTripAsync),
            ("索引归档：ApplyMove 同步路径", IndexTests.ApplyMoveUpdatesPathAsync),
            ("播放队列：排序、分类与可复现洗牌", IndexTests.PlayQueueOrderingAndShuffleAsync),
            ("协调器：成功归档并续播新路径", CoordinatorTests.SuccessfulArchiveAsync),
            ("协调器：归档失败仍续播原路径", CoordinatorTests.FailedArchiveKeepsSourceAndIndexAsync),
            ("协调器：已在目标目录时续播原路径", CoordinatorTests.NoOpArchiveKeepsIndexAsync),
            ("协调器：成功与失败均保留播放进度", CoordinatorTests.PlaybackResumesFromSavedPositionAsync)
        };

        int passed = 0;
        int failed = 0;

        try
        {
            foreach ((string name, Func<Task> run) in tests)
            {
                try
                {
                    await run();
                    passed++;
                    Console.WriteLine($"  PASS  {name}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  FAIL  {name}");
                    Console.WriteLine($"        原因: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            DeleteTree(TempRoot);
            DeleteTree(HomeRoot);
            DeleteTree(ShmRoot);
        }

        Console.WriteLine($"结果：{passed} 通过 / {failed} 失败");
        return failed == 0 ? 0 : 1;
    }

    private static Task SameVolumeWithinTreeAsync()
    {
        string root = CaseDir(TempRoot, "01-same-volume");
        string leftDir = Path.Combine(root, "a");
        string rightDir = Path.Combine(root, "b", "c");
        Directory.CreateDirectory(rightDir);

        Assert.True(
            Mover.DefaultSameVolume(Path.Combine(leftDir, "movie.mp4"), rightDir),
            "同一目录树内的路径应判定为同卷");

        return Task.CompletedTask;
    }

    private static Task DifferentVolumeAsync()
    {
        string leftDir = CaseDir(TempRoot, "02-temp-volume");
        string rightDir = CaseDir(ShmRoot, "02-shm-volume");

        Assert.False(
            Mover.DefaultSameVolume(Path.Combine(leftDir, "movie.mp4"), rightDir),
            "Path.GetTempPath() 与 /dev/shm 应判定为不同卷");

        return Task.CompletedTask;
    }

    private static async Task BasicArchiveAsync()
    {
        string root = CaseDir(TempRoot, "03-basic");
        string source = WriteFile(Path.Combine(root, "src", "movie.mp4"), 128, 0x10);
        string targetDir = Path.Combine(root, "archive", "new");
        string expected = Path.Combine(targetDir, "movie.mp4");

        MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir);

        Assert.True(outcome.Ok, "归档应成功");
        Assert.False(outcome.NoOp, "基本归档不应是 NoOp");
        Assert.Equal(MoveError.None, outcome.Error, "Error");
        Assert.FileMissing(source, "源文件");
        Assert.FileExists(expected, "目标文件");
        Assert.Equal(expected, outcome.DestPath, "DestPath");
        Assert.True(Directory.Exists(targetDir), "目标目录应被自动创建");
    }

    private static async Task ChineseSpacesDeepDirectoryAsync()
    {
        string root = CaseDir(TempRoot, "04-chinese-spaces");
        string source = WriteFile(Path.Combine(root, "源", "带 空格 的 片子.mkv"), 256, 0x20);
        string targetDir = Path.Combine(root, "剧集", "国产剧", "2024 年");
        string expected = Path.Combine(targetDir, "带 空格 的 片子.mkv");

        MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir);

        Assert.True(outcome.Ok, "中文、空格、三级目录归档应成功");
        Assert.Equal(MoveError.None, outcome.Error, "Error");
        Assert.Equal(expected, outcome.DestPath, "DestPath");
        Assert.FileExists(expected, "目标文件");
        Assert.FileMissing(source, "源文件");
    }

    private static async Task ConflictRenameAsync()
    {
        string root = CaseDir(TempRoot, "05-conflict-rename");
        string targetDir = CaseDir(root, "target");
        string source = WriteFile(Path.Combine(root, "source", "movie.mp4"), 200, 0x22);
        string original = WriteFile(Path.Combine(targetDir, "movie.mp4"), 100, 0x11);
        string expected = Path.Combine(targetDir, "movie (2).mp4");

        MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir);

        Assert.True(outcome.Ok, "默认 Rename 冲突策略应成功");
        Assert.Equal(MoveError.None, outcome.Error, "Error");
        Assert.Equal(expected, outcome.DestPath, "DestPath");
        Assert.FileLength(original, 100, "原目标文件");
        Assert.Equal((byte)0x11, File.ReadAllBytes(original)[0], "原目标文件内容");
        Assert.FileLength(expected, 200, "重命名后的新文件");
        Assert.Equal((byte)0x22, File.ReadAllBytes(expected)[0], "重命名后的新文件内容");
        Assert.FileMissing(source, "源文件");
    }

    private static async Task ConflictSkipAsync()
    {
        string root = CaseDir(TempRoot, "06-conflict-skip");
        string targetDir = CaseDir(root, "target");
        string source = WriteFile(Path.Combine(root, "source", "movie.mp4"), 200, 0x22);
        string original = WriteFile(Path.Combine(targetDir, "movie.mp4"), 100, 0x11);

        MoveOutcome outcome = await new Mover().ArchiveAsync(
            source,
            targetDir,
            new MoveOptions { OnConflict = ConflictPolicy.Skip });

        Assert.False(outcome.Ok, "Skip 冲突应返回失败");
        Assert.Equal(MoveError.ConflictSkipped, outcome.Error, "Error");
        Assert.Equal(Mover.Human(MoveError.ConflictSkipped), outcome.Message, "Message");
        Assert.FileLength(source, 200, "源文件应留在原处");
        Assert.FileLength(original, 100, "目标文件不应被改动");
        Assert.Equal((byte)0x11, File.ReadAllBytes(original)[0], "目标文件内容不应被改动");
    }

    private static async Task ConflictOverwriteAsync()
    {
        string root = CaseDir(TempRoot, "07-conflict-overwrite");
        string targetDir = CaseDir(root, "target");
        string source = WriteFile(Path.Combine(root, "source", "movie.mp4"), 200, 0x22);
        string target = WriteFile(Path.Combine(targetDir, "movie.mp4"), 100, 0x11);

        MoveOutcome outcome = await new Mover().ArchiveAsync(
            source,
            targetDir,
            new MoveOptions { OnConflict = ConflictPolicy.Overwrite });

        Assert.True(outcome.Ok, "Overwrite 冲突策略应成功");
        Assert.Equal(MoveError.None, outcome.Error, "Error");
        Assert.Equal(target, outcome.DestPath, "DestPath");
        Assert.FileLength(target, 200, "目标文件应被覆盖为源大小");
        Assert.Equal((byte)0x22, File.ReadAllBytes(target)[0], "目标文件内容应来自源文件");
        Assert.FileMissing(source, "源文件");
    }

    private static async Task AlreadyInPlaceAsync()
    {
        string root = CaseDir(TempRoot, "08-already-there");
        string targetDir = CaseDir(root, "target");
        string source = WriteFile(Path.Combine(targetDir, "movie.mp4"), 100, 0x33);

        MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir);

        Assert.True(outcome.Ok, "源已在目标目录时应返回成功");
        Assert.True(outcome.NoOp, "源已在目标目录时应是 NoOp");
        Assert.Equal(MoveError.AlreadyThere, outcome.Error, "Error");
        Assert.Equal(Mover.Human(MoveError.AlreadyThere), outcome.Message, "Message");
        Assert.FileLength(source, 100, "文件应仍在原处");
    }

    private static async Task SourceMissingAsync()
    {
        string root = CaseDir(TempRoot, "09-source-missing");
        string source = Path.Combine(root, "missing", "movie.mp4");
        string targetDir = Path.Combine(root, "target");

        MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir);

        Assert.False(outcome.Ok, "源不存在时应返回失败");
        Assert.Equal(MoveError.SourceMissing, outcome.Error, "Error");
        Assert.Equal(Mover.Human(MoveError.SourceMissing), outcome.Message, "Message");
    }

    private static async Task ReadOnlyTargetAsync()
    {
        string root = CaseDir(TempRoot, "10-read-only-target");
        string targetDir = CaseDir(root, "target");
        string source = WriteFile(Path.Combine(root, "source", "movie.mp4"), 128, 0x44);

        UnixFileMode readOnlyMode =
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        UnixFileMode restoreMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        File.SetUnixFileMode(targetDir, readOnlyMode);
        try
        {
            MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir);

            Assert.False(outcome.Ok, "只读目标目录应返回失败");
            Assert.Equal(MoveError.AccessDenied, outcome.Error, "Error");
            Assert.Equal(Mover.Human(MoveError.AccessDenied), outcome.Message, "Message");
            Assert.FileLength(source, 128, "源文件应仍在原处");
            Assert.Equal(0, Directory.GetFiles(targetDir).Length, "只读目录中不应产生目标文件");
        }
        finally
        {
            File.SetUnixFileMode(targetDir, restoreMode);
        }
    }

    private static async Task CrossVolumeCopyAsync()
    {
        string root = CaseDir(HomeRoot, "11-cross-volume");
        string targetDir = CaseDir(ShmRoot, "11-cross-volume");
        const int size = 5 * 1024 * 1024;
        string source = WriteFile(Path.Combine(root, "source", "movie.mp4"), size, 0x55);
        string expected = Path.Combine(targetDir, "movie.mp4");
        var reports = new List<MoveProgress>();
        var options = new MoveOptions
        {
            Progress = new SyncProgress(progress => reports.Add(progress))
        };

        MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir, options);

        Assert.True(outcome.Ok, "跨卷复制应成功");
        Assert.Equal(MoveError.None, outcome.Error, "Error");
        Assert.FileMissing(source, "源文件");
        Assert.FileLength(expected, size, "目标文件");
        Assert.True(reports.Count >= 1, "进度回调至少应调用 1 次");
        Assert.Equal(size, reports[^1].Total, "最后一次进度的 Total");
        Assert.Equal(size, reports[^1].Copied, "最后一次进度的 Copied");
        Assert.Equal(reports[^1].Total, reports[^1].Copied, "最后一次进度应是满值");
    }

    private static async Task ForcedCopyBranchAsync()
    {
        string root = CaseDir(TempRoot, "12-forced-copy");
        string targetDir = CaseDir(root, "target");
        const int size = 1024 * 1024;
        string source = WriteFile(Path.Combine(root, "source", "movie.mp4"), size, 0x66);
        string expected = Path.Combine(targetDir, "movie.mp4");
        var mover = new Mover { SameVolumeTest = (_, _) => false };

        MoveOutcome outcome = await mover.ArchiveAsync(source, targetDir);

        Assert.True(outcome.Ok, "强制跨卷分支应成功");
        Assert.Equal(MoveError.None, outcome.Error, "Error");
        Assert.FileMissing(source, "源文件应被删除");
        Assert.FileLength(expected, size, "目标文件");
    }

    private static async Task CancelDuringCopyAsync()
    {
        string root = CaseDir(HomeRoot, "13-cancel");
        string targetDir = CaseDir(ShmRoot, "13-cancel");
        const int size = 6 * 1024 * 1024;
        string source = WriteFile(Path.Combine(root, "source", "movie.mp4"), size, 0x77);
        string expected = Path.Combine(targetDir, "movie.mp4");
        using var cts = new CancellationTokenSource();
        int reportCount = 0;
        var options = new MoveOptions
        {
            BufferSize = 64 * 1024,
            Cancel = cts.Token,
            Progress = new SyncProgress(_ =>
            {
                reportCount++;
                cts.Cancel();
            })
        };

        MoveOutcome outcome = await new Mover().ArchiveAsync(source, targetDir, options);

        Assert.False(outcome.Ok, "取消后应返回失败");
        Assert.Equal(MoveError.Cancelled, outcome.Error, "Error");
        Assert.Equal(Mover.Human(MoveError.Cancelled), outcome.Message, "Message");
        Assert.True(reportCount >= 1, "取消前应至少收到 1 次进度回调");
        Assert.FileLength(source, size, "源文件应完好");
        Assert.FileMissing(expected, "取消后目标文件不应存在");
        Assert.Equal(0, Directory.GetFiles(targetDir).Length, "目标目录下不应有半成品");
    }

    private static string CaseDir(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WriteFile(string path, int size, byte value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = new byte[size];
        Array.Fill(data, value);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            ResetDirectoryModes(path);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ResetDirectoryModes(string root)
    {
        UnixFileMode writableMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        try
        {
            File.SetUnixFileMode(root, writableMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetUnixFileMode(directory, writableMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
