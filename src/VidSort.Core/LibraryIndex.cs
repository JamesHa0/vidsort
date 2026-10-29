using System.Text.Json;

namespace VidSort.Core;

public sealed class VideoItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime MtimeUtc { get; set; }
    public string CategoryRelativePath { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public enum ScanChangeKind
{
    Added,
    Changed,
    Removed,
    Unchanged
}

public sealed class ScanResult
{
    public int Added { get; set; }
    public int Changed { get; set; }
    public int Removed { get; set; }
    public int Unchanged { get; set; }
    public List<VideoItem> Items { get; set; } = new();
}

public sealed class LibraryIndex
{
    private List<VideoItem> _items = new();

    public LibraryIndex(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public IReadOnlyList<VideoItem> Items => _items;

    public ScanResult Scan(IEnumerable<VideoItem>? previousMetadata = null)
    {
        var previous = new Dictionary<string, VideoItem>(StringComparer.Ordinal);
        if (previousMetadata != null)
        {
            foreach (VideoItem item in previousMetadata)
            {
                previous[item.RelativePath] = item;
            }
        }

        var scanned = new List<VideoItem>();
        int added = 0;
        int changed = 0;
        int unchanged = 0;

        if (Directory.Exists(Root))
        {
            ScanDirectory(Root, string.Empty, previous, scanned, ref added, ref changed, ref unchanged);
        }

        var currentPaths = new HashSet<string>(scanned.Select(item => item.RelativePath), StringComparer.Ordinal);
        int removed = previous.Keys.Count(path => !currentPaths.Contains(path));

        scanned.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        _items = scanned;

        return new ScanResult
        {
            Added = added,
            Changed = changed,
            Removed = removed,
            Unchanged = unchanged,
            Items = scanned
        };
    }

    private static void ScanDirectory(
        string directory,
        string relativeDirectory,
        Dictionary<string, VideoItem> previous,
        List<VideoItem> scanned,
        ref int added,
        ref int changed,
        ref int unchanged)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string fileName = Path.GetFileName(file);
            if (!fileName.IsVideoFile())
            {
                continue;
            }

            var info = new FileInfo(file);
            string relativePath = relativeDirectory.Length == 0
                ? fileName
                : relativeDirectory + "/" + fileName;
            string category = CategoryOf(relativePath);

            var item = new VideoItem
            {
                RelativePath = relativePath,
                FileName = fileName,
                SizeBytes = info.Length,
                MtimeUtc = info.LastWriteTimeUtc,
                CategoryRelativePath = category
            };

            if (previous.TryGetValue(relativePath, out VideoItem? old))
            {
                if (old.SizeBytes == item.SizeBytes && old.MtimeUtc == item.MtimeUtc)
                {
                    item.DurationMs = old.DurationMs;
                    item.Width = old.Width;
                    item.Height = old.Height;
                    unchanged++;
                }
                else
                {
                    changed++;
                }
            }
            else
            {
                added++;
            }

            scanned.Add(item);
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith("_", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            string relativeChild = relativeDirectory.Length == 0
                ? name
                : relativeDirectory + "/" + name;
            ScanDirectory(child, relativeChild, previous, scanned, ref added, ref changed, ref unchanged);
        }
    }

    public void ApplyMove(string fromRelativePath, string toRelativePath)
    {
        string from = NormalizeRelativePath(fromRelativePath);
        string to = NormalizeRelativePath(toRelativePath);

        foreach (VideoItem item in _items)
        {
            if (!string.Equals(item.RelativePath, from, StringComparison.Ordinal))
            {
                continue;
            }

            item.RelativePath = to;
            item.FileName = FileNameOf(to);
            item.CategoryRelativePath = CategoryOf(to);
        }
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var persisted = new PersistedIndex { Items = _items };
        File.WriteAllText(path, JsonSerializer.Serialize(persisted, JsonOptions));
    }

    public static LibraryIndex Load(string path, string root)
    {
        var persisted = JsonSerializer.Deserialize<PersistedIndex>(File.ReadAllText(path), JsonOptions);
        var index = new LibraryIndex(root);
        index._items = persisted?.Items ?? new List<VideoItem>();
        return index;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string FileNameOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath.Substring(slash + 1);
    }

    private static string CategoryOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash <= 0 ? string.Empty : relativePath.Substring(0, slash);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed class PersistedIndex
    {
        public List<VideoItem> Items { get; set; } = new();
    }
}

public static class PlayQueue
{
    public static List<VideoItem> Build(
        IEnumerable<VideoItem> items,
        string? categoryRelativePath = null,
        bool shuffle = false,
        int seed = 0)
    {
        IEnumerable<VideoItem> filtered = items;

        if (categoryRelativePath != null)
        {
            string category = categoryRelativePath.Replace('\\', '/').Trim('/');
            string prefix = category.Length == 0 ? string.Empty : category + "/";
            filtered = filtered.Where(item =>
                string.Equals(item.CategoryRelativePath, category, StringComparison.Ordinal) ||
                (prefix.Length > 0 && item.RelativePath.StartsWith(prefix, StringComparison.Ordinal)));
        }

        var queue = filtered.ToList();

        if (!shuffle)
        {
            queue.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
            return queue;
        }

        var random = new Random(seed);
        for (int i = queue.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (queue[i], queue[j]) = (queue[j], queue[i]);
        }

        return queue;
    }
}
