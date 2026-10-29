using System.Text.Json;

namespace VidSort.Core;

public sealed record JournalEntry(string FromAbsolute, string ToAbsolute, long AtUnixMs);

public sealed class ArchiveJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<JournalEntry> _entries = new();

    public IReadOnlyList<JournalEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Record(string fromAbsolute, string toAbsolute)
    {
        lock (_gate)
        {
            _entries.Add(new JournalEntry(
                fromAbsolute,
                toAbsolute,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }

    public bool TryPop(out JournalEntry entry)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                entry = null!;
                return false;
            }

            int lastIndex = _entries.Count - 1;
            entry = _entries[lastIndex];
            _entries.RemoveAt(lastIndex);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public void Save(string path)
    {
        JournalEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToArray();
        }

        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    public static ArchiveJournal Load(string path)
    {
        var journal = new ArchiveJournal();
        if (!File.Exists(path))
        {
            return journal;
        }

        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return journal;
        }

        List<JournalEntry>? entries = JsonSerializer.Deserialize<List<JournalEntry>>(json, JsonOptions);
        if (entries is not null)
        {
            journal._entries.AddRange(entries);
        }

        return journal;
    }
}

public static class ArchiveUndo
{
    public static async Task<MoveOutcome> UndoLastAsync(ArchiveJournal journal, Mover mover)
    {
        if (!journal.TryPop(out JournalEntry entry))
        {
            return MoveOutcome.Failure(MoveError.Unknown);
        }

        MoveOutcome outcome = await mover.ArchiveAsync(
            entry.ToAbsolute,
            Path.GetDirectoryName(entry.FromAbsolute)!);

        if (!outcome.Ok)
        {
            journal.Record(entry.FromAbsolute, entry.ToAbsolute);
        }

        return outcome;
    }
}
