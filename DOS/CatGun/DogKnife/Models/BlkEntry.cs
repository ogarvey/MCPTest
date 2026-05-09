namespace DogKnife.Models;

internal sealed record BlkEntry(string ArchivePath, int Offset, int Size)
{
    public string GetOutputPath(string outputRoot)
    {
        string[] segments = ArchivePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            throw new InvalidOperationException("Archive entry path is empty.");
        }

        foreach (string segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new InvalidOperationException($"Unsafe archive path segment: {ArchivePath}");
            }
        }

        string relativePath = Path.Combine(segments);
        return Path.Combine(outputRoot, relativePath);
    }
}
