using VidSort.Core;

namespace VidSort.Core.Tests;

internal static class JournalTests
{
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    private static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(),
        "vidsort-journal-tests",
        RunId);

    public static async Task UndoLastRestoresSingleAsync()
    {
        string root = CaseDir("01-single-undo");
        try
        {
            string source = WriteFile(Path.Combine(root, "inbox", "a.mp4"), 64, 0x11);
            string targetDir = Path.Combine(root, "电影", "国产");
            MoveOutcome archived = await new Mover().ArchiveAsync(source, targetDir);
            Assert.True(archived.Ok, "归档应成功");
            Assert.True(archived.DestPath is not null, "归档结果应包含目标路径");

            var journal = new ArchiveJournal();
            journal.Record(source, archived.DestPath!);

            Assert.Equal(1, journal.Count, "归档日志数量");
            Assert.Equal(archived.DestPath, journal.Entries[0].ToAbsolute, "日志目标路径");
            Assert.Equal(source, journal.Entries[0].FromAbsolute, "日志源路径");

            MoveOutcome undone = await ArchiveUndo.UndoLastAsync(journal, new Mover());

            Assert.True(undone.Ok, "撤销应成功");
            Assert.FileExists(source, "撤销后的源文件");
            Assert.FileMissing(archived.DestPath!, "撤销后的归档文件");
            Assert.Equal(0, journal.Count, "撤销后的日志数量");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    public static async Task UndoLastIsLifoAsync()
    {
        string root = CaseDir("02-lifo-undo");
        try
        {
            string sourceA = WriteFile(Path.Combine(root, "inbox", "a.mp4"), 64, 0x22);
            string sourceB = WriteFile(Path.Combine(root, "inbox", "b.mp4"), 64, 0x33);
            MoveOutcome archivedA = await new Mover().ArchiveAsync(sourceA, Path.Combine(root, "电影"));
            MoveOutcome archivedB = await new Mover().ArchiveAsync(sourceB, Path.Combine(root, "剧集"));
            Assert.True(archivedA.Ok && archivedB.Ok, "两次归档都应成功");

            var journal = new ArchiveJournal();
            journal.Record(sourceA, archivedA.DestPath!);
            journal.Record(sourceB, archivedB.DestPath!);

            MoveOutcome firstUndo = await ArchiveUndo.UndoLastAsync(journal, new Mover());

            Assert.True(firstUndo.Ok, "第一次撤销应成功");
            Assert.Equal(sourceB, firstUndo.DestPath, "第一次撤销应先还原 b.mp4");
            Assert.FileExists(sourceB, "第一次撤销后的 b.mp4");
            Assert.FileMissing(sourceA, "第一次撤销后 a.mp4 应仍在归档目录");
            Assert.Equal(1, journal.Count, "第一次撤销后的日志数量");

            MoveOutcome secondUndo = await ArchiveUndo.UndoLastAsync(journal, new Mover());

            Assert.True(secondUndo.Ok, "第二次撤销应成功");
            Assert.Equal(sourceA, secondUndo.DestPath, "第二次撤销应还原 a.mp4");
            Assert.FileExists(sourceA, "第二次撤销后的 a.mp4");
            Assert.FileExists(sourceB, "第二次撤销后的 b.mp4");
            Assert.Equal(0, journal.Count, "第二次撤销后的日志数量");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    public static async Task PersistenceAndFailureRollbackAsync()
    {
        string root = CaseDir("03-persistence-rollback");
        string targetDir = Path.Combine(root, "电影", "国产");
        try
        {
            string source = WriteFile(Path.Combine(root, "inbox", "a.mp4"), 64, 0x44);
            MoveOutcome archived = await new Mover().ArchiveAsync(source, targetDir);
            Assert.True(archived.Ok, "归档应成功");

            var journal = new ArchiveJournal();
            journal.Record(source, archived.DestPath!);
            string journalPath = Path.Combine(root, "journal.json");
            journal.Save(journalPath);

            ArchiveJournal loaded = ArchiveJournal.Load(journalPath);

            Assert.Equal(journal.Count, loaded.Count, "加载后的日志数量");
            Assert.Equal(journal.Entries.Count, loaded.Entries.Count, "加载后的条目数量");
            for (int index = 0; index < journal.Entries.Count; index++)
            {
                Assert.Equal(journal.Entries[index].FromAbsolute, loaded.Entries[index].FromAbsolute, $"第 {index} 条源路径");
                Assert.Equal(journal.Entries[index].ToAbsolute, loaded.Entries[index].ToAbsolute, $"第 {index} 条目标路径");
            }

            File.SetUnixFileMode(targetDir, ReadOnlyDirectoryMode);
            MoveOutcome failedUndo = await ArchiveUndo.UndoLastAsync(loaded, new Mover());

            Assert.False(failedUndo.Ok, "目标目录只读时撤销应失败");
            Assert.Equal(1, loaded.Count, "撤销失败后日志记录应保留");
            Assert.Equal(source, loaded.Entries[0].FromAbsolute, "回滚后的源路径");
            Assert.Equal(archived.DestPath, loaded.Entries[0].ToAbsolute, "回滚后的目标路径");
        }
        finally
        {
            File.SetUnixFileMode(targetDir, WritableDirectoryMode);
            DeleteTree(root);
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
