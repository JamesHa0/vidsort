using VidSort.Core;

namespace VidSort.Core.Tests;

internal static class CategoryTests
{
    public static Task ScanAsync()
    {
        string root = NewCaseDir("01-scan");
        try
        {
            string domestic = Path.Combine(root, "电影", "国产");
            Directory.CreateDirectory(domestic);
            File.WriteAllBytes(Path.Combine(domestic, "a.mp4"), new byte[] { 1 });
            Directory.CreateDirectory(Path.Combine(root, "电影", "欧美"));
            Directory.CreateDirectory(Path.Combine(root, "剧集"));
            Directory.CreateDirectory(Path.Combine(root, "_inbox"));
            Directory.CreateDirectory(Path.Combine(root, ".shorts-trash"));

            var tree = new CategoryTree(root);
            List<CategoryNode> top = tree.Scan();

            Assert.Equal("剧集,电影", string.Join(",", top.Select(node => node.Name)), "顶层分类");

            CategoryNode movie = top.Single(node => node.Name == "电影");
            Assert.Equal("国产,欧美", string.Join(",", movie.Children.Select(node => node.Name)), "电影下的分类");

            CategoryNode domesticNode = movie.Children.Single(node => node.Name == "国产");
            Assert.Equal("电影/国产", domesticNode.RelativePath, "国产的相对路径");
            Assert.True(domesticNode.HasOwnVideos, "国产目录直接包含视频，HasOwnVideos 应为 true");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task HideUnhideAsync()
    {
        string root = NewCaseDir("02-hide");
        try
        {
            string domestic = Path.Combine(root, "电影", "国产");
            Directory.CreateDirectory(domestic);
            Directory.CreateDirectory(Path.Combine(root, "电影", "欧美"));

            var tree = new CategoryTree(root);
            tree.Hide("电影/国产");

            Assert.True(tree.IsHidden("电影\\国产"), "隐藏判定应归一化反斜杠并忽略大小写");

            CategoryNode movie = tree.Scan().Single(node => node.Name == "电影");
            Assert.Equal("欧美", string.Join(",", movie.Children.Select(node => node.Name)), "隐藏后电影下的分类");
            Assert.True(Directory.Exists(domestic), "隐藏不应删除磁盘目录");
            Assert.True(tree.ExportHidden().Contains("电影/国产"), "ExportHidden 应包含隐藏项");

            tree.Unhide("电影/国产");

            Assert.False(tree.IsHidden("电影/国产"), "恢复后不应再视为隐藏");
            movie = tree.Scan().Single(node => node.Name == "电影");
            Assert.Equal("国产,欧美", string.Join(",", movie.Children.Select(node => node.Name)), "恢复后电影下的分类");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task InvalidNamesAsync()
    {
        string root = NewCaseDir("03-invalid-names");
        try
        {
            string[] invalidNames =
            {
                "",
                "   ",
                "a/b",
                "a\\b",
                "a:b",
                "a*b",
                "a?b",
                "a\"b",
                "a<b",
                "a>b",
                "a|b",
                "CON",
                "nul.txt",
                "COM1",
                "lpt9",
                "结尾.",
                "结尾 ",
                new string('x', 65),
                "带\u0001控制符"
            };

            foreach (string name in invalidNames)
            {
                string? reason = CategoryName.Validate(name);
                Assert.True(reason is not null, $"非法名应被拒绝: <{name}>");
                Assert.True(
                    reason!.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'),
                    $"非法名原因应为中文: <{name}> => <{reason}>");
            }

            Assert.True(CategoryName.ValidateRelativePath("电影/国:产") is not null, "相对路径应逐段校验");
            Assert.True(CategoryName.ValidateRelativePath("电影/国产") is null, "合法相对路径应放行");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task ValidNamesAsync()
    {
        string root = NewCaseDir("04-valid-names");
        try
        {
            string[] validNames = { "电影", "国产", "2024 年", "A_B-1", "(测试)", "短剧·合集" };
            foreach (string name in validNames)
            {
                string? reason = CategoryName.Validate(name);
                Assert.True(reason is null, $"合法名不应被拒绝: <{name}> => <{reason}>");
            }

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task RegisterLazyAsync()
    {
        string root = NewCaseDir("05-register-lazy");
        try
        {
            var tree = new CategoryTree(root);

            string relativePath = tree.Register("电影", "新分类");

            Assert.Equal("电影/新分类", relativePath, "Register 返回的相对路径");
            string target = tree.ResolveTarget(relativePath);
            Assert.Equal(Path.Combine(root, "电影", "新分类"), target, "ResolveTarget 的目标");
            Assert.False(Directory.Exists(target), "Register 不应创建磁盘目录");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task ResolveTargetAsync()
    {
        string root = NewCaseDir("06-resolve-target");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "电影", "国产"));
            var tree = new CategoryTree(root);
            string expected = Path.Combine(root, "电影", "国产");

            Assert.Equal(expected, tree.ResolveTarget("电影/国产"), "按相对路径解析");

            CategoryNode domestic = tree.Scan()
                .Single(node => node.Name == "电影")
                .Children
                .Single(node => node.Name == "国产");
            Assert.Equal(expected, tree.ResolveTarget(domestic), "按分类节点解析");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task RenameAsync()
    {
        string root = NewCaseDir("07-rename");
        try
        {
            string oldDirectory = Path.Combine(root, "电影", "欧美");
            Directory.CreateDirectory(oldDirectory);
            File.WriteAllText(Path.Combine(oldDirectory, "a.mp4"), "x");

            var tree = new CategoryTree(root);
            tree.Hide("电影/欧美");
            tree.Hide("剧集");

            string renamed = tree.Rename("电影/欧美", "欧美片");

            Assert.Equal("电影/欧美片", renamed, "Rename 返回的相对路径");
            Assert.False(Directory.Exists(oldDirectory), "旧目录应消失");

            string newDirectory = Path.Combine(root, "电影", "欧美片");
            Assert.True(Directory.Exists(newDirectory), "新目录应存在");
            Assert.True(File.Exists(Path.Combine(newDirectory, "a.mp4")), "目录内容应随重命名移动");

            List<string> hidden = tree.ExportHidden();
            Assert.True(hidden.Contains("电影/欧美片"), "隐藏列表中的旧路径应更新为新路径");
            Assert.False(hidden.Contains("电影/欧美"), "隐藏列表不应保留旧路径");
            Assert.True(hidden.Contains("剧集"), "无关的隐藏项不应被清掉");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task DepthLimitAsync()
    {
        string root = NewCaseDir("08-depth-limit");
        try
        {
            var tree = new CategoryTree(root, maxDepth: 3);

            Assert.Equal("电影/国产/2024", tree.Register("电影/国产", "2024"), "第 3 层应允许");

            bool threw = false;
            try
            {
                tree.Register("电影/国产/2024", "更深的");
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert.True(threw, "超过 maxDepth=3 应抛 ArgumentException");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task VideoExtensionWhitelistAsync()
    {
        string root = NewCaseDir("09-video-extensions");
        try
        {
            Assert.True("a.MP4".IsVideoFile(), "a.MP4 应为视频");
            Assert.True("a.mkv".IsVideoFile(), "a.mkv 应为视频");
            Assert.True("a.m2ts".IsVideoFile(), "a.m2ts 应为视频");
            Assert.False("a.txt".IsVideoFile(), "a.txt 不应为视频");
            Assert.False("a.mp4.bak".IsVideoFile(), "a.mp4.bak 不应为视频");

            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewCaseDir(string label)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"vidsort-category-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
