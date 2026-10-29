namespace VidSort.Core;

public sealed class ArchiveCoordinator
{
    private readonly IPlayer _player;
    private readonly Mover _mover;
    private readonly LibraryIndex _index;

    public ArchiveCoordinator(IPlayer player, Mover mover, LibraryIndex index)
    {
        _player = player;
        _mover = mover;
        _index = index;
    }

    public async Task<MoveOutcome> ArchiveAsync(VideoItem item, string targetDirectory)
    {
        long position = _player.PositionMs;
        await _player.ReleaseAsync();

        string sourcePath = Path.Combine(
            _index.Root,
            item.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        MoveOutcome outcome = await _mover.ArchiveAsync(sourcePath, targetDirectory);

        if (outcome.Ok && !outcome.NoOp)
        {
            string relativePath = Path.GetRelativePath(_index.Root, outcome.DestPath!)
                .Replace(Path.DirectorySeparatorChar, '/');
            _index.ApplyMove(item.RelativePath, relativePath);
            await _player.PlayAsync(outcome.DestPath!, position);
        }
        else
        {
            await _player.PlayAsync(sourcePath, position);
        }

        return outcome;
    }
}
