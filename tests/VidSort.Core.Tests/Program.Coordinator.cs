using VidSort.Core;

namespace VidSort.Core.Tests;

internal sealed class FakePlayer : IPlayer
{
    public List<string> Calls { get; } = new();

    public long PositionMs { get; set; }

    public Task ReleaseAsync()
    {
        Calls.Add("release");
        return Task.CompletedTask;
    }

    public Task PlayAsync(string absolutePath, long startMs)
    {
        Calls.Add($"play:{absolutePath}|{startMs}");
        return Task.CompletedTask;
    }
}

internal static class CoordinatorTests
{
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    private static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(),
        "vidsort-coordinator-tests",
        RunId);

    public static async Task SuccessfulArchiveAsync()
    {
        string root = CaseDir("01-success");
        string targetDir = Path.Combine(root, "电影");
        string expected = Path.Combine(targetDir, "movie.mp4");
        try
        {
            WriteFile(Path.Combine(root, "inbox", "movie.mp4"), 128, 0x11);
            var index = new LibraryIndex(root);
            index.Scan();
            VideoItem item = index.Items.Single(entry => entry.RelativePath == "inbox/movie.mp4");
            var player = new FakePlayer { PositionMs = 4321 };
            var coordinator = new ArchiveCoordinator(player, new Mover(), index);

            MoveOutcome outcome = await coordinator.ArchiveAsync(item, targetDir);

            Assert.True(outcome.Ok, "成功归档应返回 Ok");
            Assert.False(outcome.NoOp, "成功归档不应是 NoOp");
            Assert.Equal(2, player.Calls.Count, "播放器调用次数");
            Assert.Equal("release", player.Calls[0], "第一次播放器调用");
            Assert.Equal($"play:{expected}|4321", player.Calls[1], "第二次播放器调用");
            Assert.FileMissing(Path.Combine(root, "inbox", "movie.mp4"), "源文件");
            Assert.FileExists(expected, "目标文件");
            Assert.Equal("电影/movie.mp4", item.RelativePath, "索引中的相对路径");
            VideoItem indexed = index.Items.Single(entry => entry.RelativePath == "电影/movie.mp4");
            Assert.Equal("电影", indexed.CategoryRelativePath, "索引中的分类路径");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    public static async Task FailedArchiveKeepsSourceAndIndexAsync()
    {
        string root = CaseDir("02-failure");
        string source = Path.Combine(root, "inbox", "movie.mp4");
        string targetDir = Path.Combine(root, "只读目标");
        Directory.CreateDirectory(targetDir);
        try
        {
            WriteFile(source, 128, 0x22);
            var index = new LibraryIndex(root);
            index.Scan();
            VideoItem item = index.Items.Single(entry => entry.RelativePath == "inbox/movie.mp4");
            var player = new FakePlayer { PositionMs = 2468 };
            var coordinator = new ArchiveCoordinator(player, new Mover(), index);

            File.SetUnixFileMode(targetDir, ReadOnlyDirectoryMode);
            MoveOutcome outcome = await coordinator.ArchiveAsync(item, targetDir);

            Assert.False(outcome.Ok, "只读目标目录应返回失败");
            Assert.Equal(MoveError.AccessDenied, outcome.Error, "Error");
            Assert.Equal(2, player.Calls.Count, "播放器调用次数");
            Assert.Equal("release", player.Calls[0], "第一次播放器调用");
            Assert.Equal($"play:{source}|2468", player.Calls[1], "第二次播放器调用");
            Assert.FileExists(source, "源文件");
            Assert.Equal("inbox/movie.mp4", item.RelativePath, "索引中的相对路径不应改变");
            Assert.Equal(1, index.Items.Count, "索引项数量");
        }
        finally
        {
            File.SetUnixFileMode(targetDir, WritableDirectoryMode);
            DeleteTree(root);
        }
    }

    public static async Task NoOpArchiveKeepsIndexAsync()
    {
        string root = CaseDir("03-noop");
        string targetDir = Path.Combine(root, "分类");
        string source = Path.Combine(targetDir, "movie.mp4");
        try
        {
            WriteFile(source, 128, 0x33);
            var index = new LibraryIndex(root);
            index.Scan();
            VideoItem item = index.Items.Single(entry => entry.RelativePath == "分类/movie.mp4");
            var player = new FakePlayer { PositionMs = 1357 };
            var coordinator = new ArchiveCoordinator(player, new Mover(), index);

            MoveOutcome outcome = await coordinator.ArchiveAsync(item, targetDir);

            Assert.True(outcome.Ok, "已在目标目录应返回 Ok");
            Assert.True(outcome.NoOp, "已在目标目录应返回 NoOp");
            Assert.Equal(2, player.Calls.Count, "播放器调用次数");
            Assert.Equal("release", player.Calls[0], "第一次播放器调用");
            Assert.Equal($"play:{source}|1357", player.Calls[1], "第二次播放器调用");
            Assert.FileExists(source, "文件应仍在原处");
            Assert.Equal("分类/movie.mp4", item.RelativePath, "索引中的相对路径不应改变");
            Assert.Equal(1, index.Items.Count, "索引项数量");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    public static async Task PlaybackResumesFromSavedPositionAsync()
    {
        string successRoot = CaseDir("04-position-success");
        string failureRoot = CaseDir("04-position-failure");
        string failureTarget = Path.Combine(failureRoot, "只读目标");
        Directory.CreateDirectory(failureTarget);
        try
        {
            WriteFile(Path.Combine(successRoot, "inbox", "movie.mp4"), 64, 0x44);
            var successIndex = new LibraryIndex(successRoot);
            successIndex.Scan();
            VideoItem successItem = successIndex.Items.Single();
            var successPlayer = new FakePlayer { PositionMs = 12345 };
            var successCoordinator = new ArchiveCoordinator(successPlayer, new Mover(), successIndex);

            MoveOutcome success = await successCoordinator.ArchiveAsync(
                successItem,
                Path.Combine(successRoot, "目标"));

            Assert.True(success.Ok, "成功归档应返回 Ok");
            Assert.Equal($"play:{success.DestPath}|12345", successPlayer.Calls[1], "成功归档的续播位置");

            string failureSource = Path.Combine(failureRoot, "inbox", "movie.mp4");
            WriteFile(failureSource, 64, 0x55);
            var failureIndex = new LibraryIndex(failureRoot);
            failureIndex.Scan();
            VideoItem failureItem = failureIndex.Items.Single(entry => entry.RelativePath == "inbox/movie.mp4");
            var failurePlayer = new FakePlayer { PositionMs = 12345 };
            var failureCoordinator = new ArchiveCoordinator(failurePlayer, new Mover(), failureIndex);

            File.SetUnixFileMode(failureTarget, ReadOnlyDirectoryMode);
            MoveOutcome failure = await failureCoordinator.ArchiveAsync(failureItem, failureTarget);

            Assert.False(failure.Ok, "只读目标目录应返回失败");
            Assert.Equal($"play:{failureSource}|12345", failurePlayer.Calls[1], "失败归档的续播位置");
        }
        finally
        {
            File.SetUnixFileMode(failureTarget, WritableDirectoryMode);
            DeleteTree(successRoot);
            DeleteTree(failureRoot);
        }
    }

    private static readonly UnixFileMode ReadOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly UnixFileMode WritableDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static string CaseDir(string name)
    {
        string directory = Path.Combine(TempRoot, name);
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
        try
        {
            File.SetUnixFileMode(root, WritableDirectoryMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetUnixFileMode(directory, WritableDirectoryMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
