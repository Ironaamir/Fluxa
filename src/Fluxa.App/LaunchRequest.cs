namespace Fluxa.App;

internal sealed record LaunchRequest(bool ConvertImmediately, IReadOnlyList<string> Files)
{
    public static LaunchRequest Parse(IEnumerable<string> arguments)
    {
        var args = arguments.ToArray();
        var convert = args.FirstOrDefault() == "--convert";
        var hasAction = convert || args.FirstOrDefault() == "--open";
        var files = args.Skip(hasAction ? 1 : 0).Select(Path.GetFullPath).ToArray();
        return new(convert, files);
    }
}
