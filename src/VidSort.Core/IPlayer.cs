namespace VidSort.Core;

public interface IPlayer
{
    long PositionMs { get; }

    Task ReleaseAsync();

    Task PlayAsync(string absolutePath, long startMs);
}
