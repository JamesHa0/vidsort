using VidSort.Core;

namespace VidSort.Core.Tests;

internal static class IndexTests
{
    public static Task ScanFiltersAndCategoriesAsync()
    {
        string working = NewWorkingDirectory("01-scan");
        try
        {
            string root = Path.Combine(working, "library");
            WriteFile(Path.Combine(root, "电影", "国产", "a.mp4"), 10, 0x11);
            WriteFile(Path.Combine(root, "电影", "b.mkv"), 20, 0x22);
            WriteFile(Path.Combine(root, "c.txt"), 30, 0x33);
            WriteFile(Path.Combine(root, "_inbox", "d.mp4"), 40, 0x44);
            WriteFile(Path.Combine(root, ".trash", "e.mp4"), 50, 0x55);

            var index = new LibraryIndex(root);
            ScanResult result = index.Scan();

            Assert.Equal(2, result.Items.Count, "Items 数量");
            Assert.Equal(2, result.Added, "Added");
            Assert.Equal(2, index.Items.Count, "索引 Items 数量");

            VideoItem a = result.Items.Single(item => item.RelativePath == "电影/国产/a.mp4");
            VideoItem b = result.Items.Single(item => item.RelativePath == "电影/b.mkv");
            Assert.Equal("电影/国产", a.CategoryRelativePath, "a.mp4 CategoryRelativePath");
            Assert.Equal("电影", b.CategoryRelativePath, "b.mkv CategoryRelativePath");
            Assert.Equal("a.mp4", a.FileName, "a.mp4 FileName");
        }
        finally
        {
            DeleteTree(working);
        }

        return Task.CompletedTask;
    }

    public static Task IncrementalReusesMetadataAsync()
    {
        string working = NewWorkingDirectory("02-incremental-reuse");
        try
        {
            string root = Path.Combine(working, "library");
            WriteFile(Path.Combine(root, "电影", "国产", "a.mp4"), 10, 0x11);
            WriteFile(Path.Combine(root, "电影", "b.mkv"), 20, 0x22);

            var index = new LibraryIndex(root);
            ScanResult first = index.Scan();
            VideoItem a = first.Items.Single(item => item.RelativePath == "电影/国产/a.mp4");
            a.DurationMs = 12345;
            a.Width = 1920;
            a.Height = 1080;

            ScanResult second = index.Scan(first.Items);

            Assert.Equal(2, second.Unchanged, "Unchanged");
            Assert.Equal(0, second.Added, "Added");
            VideoItem scannedA = second.Items.Single(item => item.RelativePath == "电影/国产/a.mp4");
            Assert.Equal(12345L, scannedA.DurationMs, "复用的 DurationMs");
            Assert.Equal(1920, scannedA.Width, "复用的 Width");
            Assert.Equal(1080, scannedA.Height, "复用的 Height");
        }
        finally
        {
            DeleteTree(working);
        }

        return Task.CompletedTask;
    }

    public static Task IncrementalChangeClearsMetadataAsync()
    {
        string working = NewWorkingDirectory("03-incremental-change");
        try
        {
            string root = Path.Combine(working, "library");
            WriteFile(Path.Combine(root, "电影", "国产", "a.mp4"), 10, 0x11);
            string bPath = WriteFile(Path.Combine(root, "电影", "b.mkv"), 20, 0x22);

            var index = new LibraryIndex(root);
            ScanResult first = index.Scan();
            VideoItem firstB = first.Items.Single(item => item.RelativePath == "电影/b.mkv");
            firstB.DurationMs = 777;
            firstB.Width = 640;
            firstB.Height = 360;
            DateTime newMtime = firstB.MtimeUtc.AddMinutes(5);

            WriteFile(bPath, 21, 0x23);
            File.SetLastWriteTimeUtc(bPath, newMtime);

            ScanResult second = index.Scan(first.Items);

            Assert.Equal(1, second.Changed, "Changed");
            Assert.Equal(1, second.Unchanged, "Unchanged");
            VideoItem secondB = second.Items.Single(item => item.RelativePath == "电影/b.mkv");
            Assert.True(secondB.DurationMs is null, "Changed 项 DurationMs 应为 null");
            Assert.True(secondB.Width is null, "Changed 项 Width 应为 null");
            Assert.True(secondB.Height is null, "Changed 项 Height 应为 null");
        }
        finally
        {
            DeleteTree(working);
        }

        return Task.CompletedTask;
    }

    public static Task SaveLoadRoundTripAsync()
    {
        string working = NewWorkingDirectory("04-save-load");
        try
        {
            string root = Path.Combine(working, "library");
            WriteFile(Path.Combine(root, "电影", "国产", "a.mp4"), 10, 0x11);
            WriteFile(Path.Combine(root, "电影", "b.mkv"), 20, 0x22);

            var index = new LibraryIndex(root);
            index.Scan();
            VideoItem a = index.Items.Single(item => item.RelativePath == "电影/国产/a.mp4");
            a.DurationMs = 98765;
            a.Width = 1280;
            a.Height = 720;

            string jsonPath = Path.Combine(working, "index.json");
            index.Save(jsonPath);
            Assert.FileExists(jsonPath, "索引 JSON");

            LibraryIndex loaded = LibraryIndex.Load(jsonPath, root);
            Assert.Equal(index.Items.Count, loaded.Items.Count, "加载后的 Items 数量");
            VideoItem loadedA = loaded.Items.Single(item => item.RelativePath == "电影/国产/a.mp4");
            Assert.Equal(a.RelativePath, loadedA.RelativePath, "RelativePath");
            Assert.Equal(a.SizeBytes, loadedA.SizeBytes, "SizeBytes");
            Assert.Equal(a.DurationMs, loadedA.DurationMs, "DurationMs");
            Assert.Equal(a.MtimeUtc, loadedA.MtimeUtc, "MtimeUtc");
        }
        finally
        {
            DeleteTree(working);
        }

        return Task.CompletedTask;
    }

    public static Task ApplyMoveUpdatesPathAsync()
    {
        string working = NewWorkingDirectory("05-apply-move");
        try
        {
            string root = Path.Combine(working, "library");
            WriteFile(Path.Combine(root, "电影", "国产", "a.mp4"), 10, 0x11);
            WriteFile(Path.Combine(root, "电影", "b.mkv"), 20, 0x22);

            var index = new LibraryIndex(root);
            index.Scan();

            index.ApplyMove("电影/b.mkv", "电影/国产/b.mkv");

            Assert.Equal(2, index.Items.Count, "Items 数量");
            VideoItem moved = index.Items.Single(item => item.RelativePath == "电影/国产/b.mkv");
            Assert.Equal("电影/国产", moved.CategoryRelativePath, "移动后的 CategoryRelativePath");
            Assert.Equal("b.mkv", moved.FileName, "移动后的 FileName");
        }
        finally
        {
            DeleteTree(working);
        }

        return Task.CompletedTask;
    }

    public static Task PlayQueueOrderingAndShuffleAsync()
    {
        string working = NewWorkingDirectory("06-play-queue");
        try
        {
            string root = Path.Combine(working, "library");
            WriteFile(Path.Combine(root, "根.mp4"), 10, 0x11);
            WriteFile(Path.Combine(root, "电影", "b.mkv"), 20, 0x22);
            WriteFile(Path.Combine(root, "电影", "国产", "c.mp4"), 30, 0x33);
            WriteFile(Path.Combine(root, "其他", "d.mkv"), 40, 0x44);

            var index = new LibraryIndex(root);
            ScanResult result = index.Scan();

            List<VideoItem> ordered = PlayQueue.Build(result.Items);
            Assert.Equal(4, ordered.Count, "全库队列数量");
            for (int i = 1; i < ordered.Count; i++)
            {
                Assert.True(
                    string.CompareOrdinal(ordered[i - 1].RelativePath, ordered[i].RelativePath) < 0,
                    "shuffle=false 应按 RelativePath 序数排序");
            }

            List<VideoItem> category = PlayQueue.Build(result.Items, "电影");
            Assert.Equal(2, category.Count, "电影分类队列数量");
            Assert.True(category.Any(item => item.RelativePath == "电影/b.mkv"), "分类应包含直接子项");
            Assert.True(category.Any(item => item.RelativePath == "电影/国产/c.mp4"), "分类应包含子分类项");

            List<VideoItem> shuffledA = PlayQueue.Build(result.Items, shuffle: true, seed: 20261029);
            List<VideoItem> shuffledB = PlayQueue.Build(result.Items, shuffle: true, seed: 20261029);
            Assert.True(
                shuffledA.Select(item => item.RelativePath).SequenceEqual(shuffledB.Select(item => item.RelativePath)),
                "相同 seed 的洗牌结果应一致");

            List<string> shuffledSorted = shuffledA.Select(item => item.RelativePath).OrderBy(path => path, StringComparer.Ordinal).ToList();
            List<string> orderedPaths = ordered.Select(item => item.RelativePath).ToList();
            Assert.True(shuffledSorted.SequenceEqual(orderedPaths), "洗牌后的集合应与顺序模式一致");
        }
        finally
        {
            DeleteTree(working);
        }

        return Task.CompletedTask;
    }

    private static string NewWorkingDirectory(string caseName)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vidsort-index-tests",
            Guid.NewGuid().ToString("N"),
            caseName);
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
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
