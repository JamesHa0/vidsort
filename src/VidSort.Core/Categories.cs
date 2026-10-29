namespace VidSort.Core;

public static class CategoryName
{
    public const int MaxLength = 64;

    private static readonly char[] InvalidChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "分类名不能为空";
        }

        if (name.Length > MaxLength)
        {
            return $"分类名不能超过 {MaxLength} 个字符";
        }

        foreach (char ch in name)
        {
            if (ch < 32)
            {
                return "分类名不能包含控制字符";
            }
        }

        if (name.IndexOfAny(InvalidChars) >= 0)
        {
            return "分类名不能包含 \\ / : * ? \" < > | 中的字符";
        }

        if (name.EndsWith(' '))
        {
            return "分类名不能以空格结尾";
        }

        if (name.EndsWith('.'))
        {
            return "分类名不能以点结尾";
        }

        int dotIndex = name.IndexOf('.');
        string stem = dotIndex >= 0 ? name[..dotIndex] : name;
        stem = stem.TrimEnd(' ');

        if (IsReservedName(stem))
        {
            return "分类名不能使用 Windows 保留名";
        }

        return null;
    }

    public static string? ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }

        string[] segments = relativePath.Split('/', '\\');
        foreach (string segment in segments)
        {
            string? reason = Validate(segment);
            if (reason != null)
            {
                return reason;
            }
        }

        return null;
    }

    private static bool IsReservedName(string stem)
    {
        string upper = stem.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        if (upper.Length == 4)
        {
            string prefix = upper[..3];
            char suffix = upper[3];
            if ((prefix == "COM" || prefix == "LPT") && suffix >= '1' && suffix <= '9')
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class CategoryNode
{
    public string Name { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public List<CategoryNode> Children { get; set; } = new();

    public bool HasOwnVideos { get; set; }
}

public sealed class CategoryTree
{
    private readonly List<string> _hidden;

    public CategoryTree(string root, IEnumerable<string>? hidden = null, int maxDepth = 3)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("库根目录不能为空", nameof(root));
        }

        if (maxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        MaxDepth = maxDepth;
        _hidden = new List<string>();

        if (hidden is not null)
        {
            foreach (string path in hidden)
            {
                AddHidden(NormalizeHiddenPath(path));
            }
        }
    }

    public string Root { get; }

    public int MaxDepth { get; }

    public IReadOnlyCollection<string> Hidden => _hidden.AsReadOnly();

    public List<CategoryNode> Scan()
    {
        var nodes = new List<CategoryNode>();
        if (!Directory.Exists(Root))
        {
            return nodes;
        }

        ScanLevel(Root, string.Empty, 1, nodes);
        return nodes;
    }

    public string ResolveTarget(CategoryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return ResolveTarget(node.RelativePath);
    }

    public string ResolveTarget(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        if (normalized.Length > 0)
        {
            string? reason = CategoryName.ValidateRelativePath(normalized);
            if (reason is not null)
            {
                throw new ArgumentException(reason, nameof(relativePath));
            }
        }

        return BuildPath(normalized);
    }

    public string Register(string parentRelativePath, string name)
    {
        string? nameReason = CategoryName.Validate(name);
        if (nameReason is not null)
        {
            throw new ArgumentException(nameReason, nameof(name));
        }

        string parent = NormalizeRelativePath(parentRelativePath);
        if (parent.Length > 0)
        {
            string? pathReason = CategoryName.ValidateRelativePath(parent);
            if (pathReason is not null)
            {
                throw new ArgumentException(pathReason, nameof(parentRelativePath));
            }
        }

        int parentDepth = parent.Length == 0 ? 0 : parent.Split('/').Length;
        if (parentDepth + 1 > MaxDepth)
        {
            throw new ArgumentException($"分类层级不能超过 {MaxDepth} 层", nameof(name));
        }

        return parent.Length == 0 ? name : parent + "/" + name;
    }

    public void Hide(string relativePath)
    {
        AddHidden(NormalizeHiddenPath(relativePath));
    }

    public void Unhide(string relativePath)
    {
        string normalized = NormalizeHiddenPath(relativePath);
        _hidden.RemoveAll(path => string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsHidden(string relativePath)
    {
        string normalized = NormalizeHiddenPath(relativePath);
        if (normalized.Length == 0)
        {
            return false;
        }

        return _hidden.Exists(path => string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public string Rename(string relativePath, string newName)
    {
        string oldRelativePath = NormalizeRelativePath(relativePath);
        if (oldRelativePath.Length == 0)
        {
            throw new ArgumentException("不能重命名库根目录", nameof(relativePath));
        }

        string? pathReason = CategoryName.ValidateRelativePath(oldRelativePath);
        if (pathReason is not null)
        {
            throw new ArgumentException(pathReason, nameof(relativePath));
        }

        string? nameReason = CategoryName.Validate(newName);
        if (nameReason is not null)
        {
            throw new ArgumentException(nameReason, nameof(newName));
        }

        string source = BuildPath(oldRelativePath);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"分类目录不存在: {source}");
        }

        int lastSlash = oldRelativePath.LastIndexOf('/');
        string parent = lastSlash < 0 ? string.Empty : oldRelativePath[..lastSlash];
        string newRelativePath = parent.Length == 0 ? newName : parent + "/" + newName;

        if (string.Equals(oldRelativePath, newRelativePath, StringComparison.Ordinal))
        {
            return newRelativePath;
        }

        string target = BuildPath(newRelativePath);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"目标分类已存在: {target}");
        }

        Directory.Move(source, target);
        UpdateHiddenAfterRename(oldRelativePath, newRelativePath);
        return newRelativePath;
    }

    public List<string> ExportHidden() => new(_hidden);

    private void ScanLevel(string directory, string relativePrefix, int depth, List<CategoryNode> target)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        var directories = Directory.EnumerateDirectories(directory)
            .Select(path => new DirectoryInfo(path))
            .Where(info => !ShouldSkip(info.Name))
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (DirectoryInfo info in directories)
        {
            string relativePath = relativePrefix.Length == 0
                ? info.Name
                : relativePrefix + "/" + info.Name;

            if (IsHidden(relativePath))
            {
                continue;
            }

            var node = new CategoryNode
            {
                Name = info.Name,
                RelativePath = relativePath,
                HasOwnVideos = HasDirectVideos(info.FullName)
            };

            if (depth < MaxDepth)
            {
                ScanLevel(info.FullName, relativePath, depth + 1, node.Children);
            }

            target.Add(node);
        }
    }

    private static bool HasDirectVideos(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (file.IsVideoFile())
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkip(string name) =>
        name.Length > 0 && (name[0] == '_' || name[0] == '.');

    private void AddHidden(string normalized)
    {
        if (normalized.Length == 0)
        {
            return;
        }

        if (!IsHidden(normalized))
        {
            _hidden.Add(normalized);
        }
    }

    private void UpdateHiddenAfterRename(string oldRelativePath, string newRelativePath)
    {
        for (int i = 0; i < _hidden.Count; i++)
        {
            string hidden = _hidden[i];
            if (string.Equals(hidden, oldRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                _hidden[i] = newRelativePath;
                continue;
            }

            string prefix = oldRelativePath + "/";
            if (hidden.Length > prefix.Length &&
                hidden.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _hidden[i] = newRelativePath + hidden[oldRelativePath.Length..];
            }
        }
    }

    private string BuildPath(string normalizedRelativePath)
    {
        if (normalizedRelativePath.Length == 0)
        {
            return Root;
        }

        string path = Root;
        foreach (string segment in normalizedRelativePath.Split('/'))
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }

    private static string NormalizeRelativePath(string? relativePath) =>
        (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');

    private static string NormalizeHiddenPath(string? relativePath) =>
        (relativePath ?? string.Empty).Replace('\\', '/').Trim(' ', '/');
}
