using System.Collections.Immutable;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// Counts tree walks and existence probes, so a test can assert on work done rather than on
/// results. Shared rather than private to one test class: the probe count is exactly what proves
/// or disproves a multi-root resolve cost, which more than one suite wants to assert on.
/// </summary>
public sealed class CountingFileSystem : IFileSystem
{
    private readonly FakeFileSystem _inner;

    public CountingFileSystem(FakeFileSystem inner)
    {
        _inner = inner;
    }

    public int EnumerationCount { get; private set; }

    public int FileExistsCount { get; private set; }

    public bool FileExists(string absolutePath)
    {
        FileExistsCount++;
        return _inner.FileExists(absolutePath);
    }

    public bool DirectoryExists(string absolutePath)
    {
        return _inner.DirectoryExists(absolutePath);
    }

    public string ReadAllText(string absolutePath)
    {
        return _inner.ReadAllText(absolutePath);
    }

    public DateTime GetLastWriteTimeUtc(string absolutePath)
    {
        return _inner.GetLastWriteTimeUtc(absolutePath);
    }

    public IEnumerable<string> EnumerateFilesWithExtensions(string directory, ImmutableArray<string> extensions)
    {
        EnumerationCount++;
        return _inner.EnumerateFilesWithExtensions(directory, extensions);
    }
}
