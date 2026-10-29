namespace VidSort.Core;

public static class VideoExtensions
{
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp",
        ".avi",
        ".flv",
        ".m2ts",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".mts",
        ".ogv",
        ".rm",
        ".rmvb",
        ".ts",
        ".vob",
        ".webm",
        ".wmv"
    };

    public static bool IsVideoFile(this string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return KnownExtensions.Contains(Path.GetExtension(path));
    }
}
