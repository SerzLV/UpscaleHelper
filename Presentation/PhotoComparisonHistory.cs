using UpscaleHelper.Infrastructure;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

internal sealed class PhotoComparisonHistory(string directory)
{
    private const int Capacity = 10;
    private readonly LocalJsonFile<FileComparison[]> file = new(directory, "photo-comparisons.json");
    private FileComparison[]? recent;

    public FileComparison[] Available() =>
        (recent ??= file.Read() ?? []).Where(IsAvailable).Take(Capacity).ToArray();

    public void Remember(FileComparison pair)
    {
        if (!IsAvailable(pair)) return;
        recent = [pair, .. Available().Where(item => !SamePair(item, pair)).Take(Capacity - 1)];
        file.Write(recent);
    }

    private static bool IsAvailable(FileComparison? pair) => pair is { Kind: MediaKind.Image } &&
        IsPhoto(pair.Original) && IsPhoto(pair.Enhanced) && !SamePath(pair.Original, pair.Enhanced);

    private static bool IsPhoto(string? path) => path is not null && Path.IsPathFullyQualified(path) &&
        SupportedMedia.GetKind(path) == MediaKind.Image && File.Exists(path);

    private static bool SamePair(FileComparison left, FileComparison right) =>
        SamePath(left.Original, right.Original) && SamePath(left.Enhanced, right.Enhanced);

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
