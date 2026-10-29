using VidSort.Core;

namespace VidSort.App.Services;

public sealed class OverlayRow
{
    public string DisplayText { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public bool IsSelected { get; set; }

    public bool IsRefresh { get; set; }
}

public sealed class CategoryOverlayModel
{
    public const string RootLabel = "未分类（库根）";
    public const string RefreshLabel = "刷新分类（F5）";

    private const string Indent = "　　";
    private const string SelectedMarker = "● ";

    public CategoryOverlayModel(IEnumerable<CategoryNode> categories, string? currentCategoryRelativePath)
    {
        string current = Normalize(currentCategoryRelativePath);

        Rows.Add(CreateRow(RootLabel, string.Empty, current.Length == 0, false, 0));

        foreach (CategoryNode category in categories)
        {
            AddCategory(category, 0, current);
        }

        Rows.Add(new OverlayRow
        {
            DisplayText = RefreshLabel,
            RelativePath = string.Empty,
            IsSelected = false,
            IsRefresh = true
        });
    }

    public List<OverlayRow> Rows { get; } = new();

    public static List<OverlayRow> Build(
        IEnumerable<CategoryNode> categories,
        string? currentCategoryRelativePath) =>
        new CategoryOverlayModel(categories, currentCategoryRelativePath).Rows;

    private void AddCategory(CategoryNode category, int depth, string current)
    {
        Rows.Add(CreateRow(
            category.Name,
            category.RelativePath,
            string.Equals(Normalize(category.RelativePath), current, StringComparison.Ordinal),
            false,
            depth));

        foreach (CategoryNode child in category.Children)
        {
            AddCategory(child, depth + 1, current);
        }
    }

    private static OverlayRow CreateRow(
        string name,
        string relativePath,
        bool isSelected,
        bool isRefresh,
        int depth)
    {
        string indent = string.Concat(Enumerable.Repeat(Indent, depth));
        return new OverlayRow
        {
            DisplayText = (isSelected ? SelectedMarker : string.Empty) + indent + name,
            RelativePath = relativePath,
            IsSelected = isSelected,
            IsRefresh = isRefresh
        };
    }

    private static string Normalize(string? relativePath) =>
        (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
}
