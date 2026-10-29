namespace VidSort.Core;

public enum ConflictPolicy
{
    Rename,
    Skip,
    Overwrite
}

public enum MoveError
{
    None,
    SourceMissing,
    AlreadyThere,
    ConflictSkipped,
    AccessDenied,
    SharingViolation,
    NoSpace,
    PathTooLong,
    Cancelled,
    Unknown
}

public record MoveProgress(long Copied, long Total);

public class MoveOptions
{
    public ConflictPolicy OnConflict { get; set; } = ConflictPolicy.Rename;
    public IProgress<MoveProgress>? Progress { get; set; }
    public CancellationToken Cancel { get; set; } = default;
    public int BufferSize { get; set; } = 1 << 20;
}

public record MoveOutcome(bool Ok, bool NoOp, string? DestPath, MoveError Error, string? Message)
{
    public static MoveOutcome Success(string destPath) =>
        new(true, false, destPath, MoveError.None, null);

    public static MoveOutcome AlreadyInPlace(string path) =>
        new(true, true, path, MoveError.AlreadyThere, HumanMessage(MoveError.AlreadyThere));

    public static MoveOutcome Failure(MoveError error) =>
        new(false, false, null, error, HumanMessage(error));

    public static MoveOutcome Failure(MoveError error, string? message) =>
        new(false, false, null, error, message ?? HumanMessage(error));

    private static string HumanMessage(MoveError error) => Mover.Human(error);
}

public class Mover
{
    public Func<string, string, bool> SameVolumeTest { get; init; } = DefaultSameVolume;

    public async Task<MoveOutcome> ArchiveAsync(string sourceFile, string targetDir, MoveOptions? opt = null)
    {
        MoveOptions options = opt ?? new MoveOptions();

        try
        {
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                return MoveOutcome.Failure(MoveError.SourceMissing);
            }

            string sourcePath = Path.GetFullPath(sourceFile);
            if (!File.Exists(sourcePath))
            {
                return MoveOutcome.Failure(MoveError.SourceMissing);
            }

            string targetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
            string? sourceDirectory = Path.GetDirectoryName(sourcePath);
            if (sourceDirectory is not null && PathsEqual(sourceDirectory, targetDirectory))
            {
                return MoveOutcome.AlreadyInPlace(sourcePath);
            }

            options.Cancel.ThrowIfCancellationRequested();

            string? destinationPath = ResolveDestination(sourcePath, targetDirectory, options.OnConflict);
            if (destinationPath is null)
            {
                return MoveOutcome.Failure(MoveError.ConflictSkipped);
            }

            if (OperatingSystem.IsWindows() && destinationPath.Length > 259)
            {
                return MoveOutcome.Failure(MoveError.PathTooLong);
            }

            try
            {
                Directory.CreateDirectory(targetDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return MoveOutcome.Failure(MoveError.AccessDenied);
            }

            if (SameVolumeTest(sourcePath, targetDirectory))
            {
                options.Cancel.ThrowIfCancellationRequested();

                try
                {
                    File.Move(
                        sourcePath,
                        destinationPath,
                        options.OnConflict == ConflictPolicy.Overwrite);
                    return MoveOutcome.Success(destinationPath);
                }
                catch (IOException ex) when (IsCrossDevice(ex))
                {
                }
            }

            await CopyFileAsync(sourcePath, destinationPath, options);
            return MoveOutcome.Success(destinationPath);
        }
        catch (Exception ex)
        {
            return MoveOutcome.Failure(MapError(ex));
        }
    }

    public static bool DefaultSameVolume(string a, string b)
    {
        string pathA = Path.GetFullPath(a);
        string pathB = Path.GetFullPath(b);

        if (OperatingSystem.IsWindows())
        {
            string? rootA = Path.GetPathRoot(pathA);
            string? rootB = Path.GetPathRoot(pathB);
            return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(GetMountPoint(pathA), GetMountPoint(pathB), StringComparison.Ordinal);
    }

    public static string Human(MoveError e) => e switch
    {
        MoveError.None => "操作成功",
        MoveError.SourceMissing => "源文件不存在",
        MoveError.AlreadyThere => "文件已在目标目录中",
        MoveError.ConflictSkipped => "目标位置已存在同名文件，已跳过",
        MoveError.AccessDenied => "拒绝访问目标位置",
        MoveError.SharingViolation => "文件正被其他程序占用",
        MoveError.NoSpace => "磁盘空间不足",
        MoveError.PathTooLong => "目标路径过长",
        MoveError.Cancelled => "已取消，原文件未改动",
        MoveError.Unknown => "发生未知错误",
        _ => "发生未知错误"
    };

    private static string? ResolveDestination(string sourcePath, string targetDirectory, ConflictPolicy policy)
    {
        string fileName = Path.GetFileName(sourcePath);
        string destinationPath = Path.Combine(targetDirectory, fileName);

        if (policy == ConflictPolicy.Overwrite)
        {
            return destinationPath;
        }

        if (!File.Exists(destinationPath))
        {
            return destinationPath;
        }

        if (policy == ConflictPolicy.Skip)
        {
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int index = 2; index < int.MaxValue; index++)
        {
            destinationPath = Path.Combine(targetDirectory, $"{stem} ({index}){extension}");
            if (!File.Exists(destinationPath))
            {
                return destinationPath;
            }
        }

        throw new IOException("无法找到可用的目标文件名。");
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, MoveOptions options)
    {
        if (options.BufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.BufferSize), "缓冲区大小必须大于零。");
        }

        try
        {
            long sourceLength;

            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                options.BufferSize,
                FileOptions.Asynchronous))
            {
                sourceLength = source.Length;
                byte[] buffer = new byte[options.BufferSize];
                long copied = 0;

                while (true)
                {
                    options.Cancel.ThrowIfCancellationRequested();

                    int read = await source.ReadAsync(buffer.AsMemory(), options.Cancel);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), options.Cancel);
                    copied += read;
                    options.Progress?.Report(new MoveProgress(copied, sourceLength));
                }

                await destination.FlushAsync(options.Cancel);
            }

            if (new FileInfo(destinationPath).Length != sourceLength)
            {
                throw new IOException("复制后的目标文件大小与源文件不一致。");
            }

            options.Cancel.ThrowIfCancellationRequested();
            File.Delete(sourcePath);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            comparison);
    }

    private static string GetMountPoint(string path)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(path);
        string? bestMatch = null;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string root = Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.DirectorySeparatorChar.ToString();
            }

            if (IsPathUnder(normalizedPath, root) &&
                (bestMatch is null || root.Length > bestMatch.Length))
            {
                bestMatch = root;
            }
        }

        return bestMatch ?? Path.GetPathRoot(path) ?? normalizedPath;
    }

    private static bool IsPathUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        string prefix;
        if (root.Length == 1 && root[0] == Path.DirectorySeparatorChar)
        {
            prefix = root;
        }
        else
        {
            prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
        }

        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsCrossDevice(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 17 or 18;
    }

    private static MoveError MapError(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return MoveError.Cancelled;
        }

        if (ex is UnauthorizedAccessException)
        {
            return MoveError.AccessDenied;
        }

        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return MoveError.SourceMissing;
        }

        if (ex is IOException io)
        {
            int code = io.HResult & 0xFFFF;
            if (code is 32 or 33)
            {
                return MoveError.SharingViolation;
            }

            if (code is 28 or 112)
            {
                return MoveError.NoSpace;
            }
        }

        return MoveError.Unknown;
    }
}
