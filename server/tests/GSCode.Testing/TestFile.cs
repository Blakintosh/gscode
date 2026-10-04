namespace GSCode.Testing;

/// <summary>
/// One script in an in-memory workspace, named by its path under <see cref="TestPaths.RawRoot"/>
/// (<c>scripts\lib.gsc</c>) rather than by an absolute path.
/// </summary>
public sealed record TestFile(string RelativePath, string Text);
