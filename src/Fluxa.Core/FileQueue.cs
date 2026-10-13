namespace Fluxa.Core;

public sealed class FileQueue<T>(Func<T, string> path, Func<T, bool> complete)
{
    public const int PendingLimit = 500;
    public List<T> Items { get; } = [];
    public int PendingCount => Items.Count(item => !complete(item));

    public bool CanAdd(string candidate) => PendingCount < PendingLimit &&
        !Items.Any(item => !complete(item) && string.Equals(Path.GetFullPath(path(item)),
            Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase));
}
