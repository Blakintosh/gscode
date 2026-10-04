using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;

namespace GSCode.Testing;

/// <summary>
/// Hand-built store records, for the tests whose subject is the data a record carries — context
/// ids, relative paths, which symbols sit where — rather than what the indexer would make of a
/// source. A test adds what it is about with <c>with { Functions = [...] }</c>; everything a
/// record requires and the test does not care about is filled in here the same way every time.
/// </summary>
public static class TestRecords
{
    /// <summary>A range for a symbol whose position no assertion reads.</summary>
    public static TextRange Anywhere { get; } = TextRange.FromCoordinates(0, 0, 0, 1);

    /// <summary>A record for <paramref name="path"/> with nothing in it.</summary>
    public static ScriptRecord At(
        string path, string contextId = "raw", string relativePath = "", ScriptLanguage language = ScriptLanguage.Gsc)
    {
        return new ScriptRecord
        {
            Path = path,
            Language = language,
            ContextId = contextId,
            ContentHash = 0,
            RelativePath = relativePath,
        };
    }

    /// <summary>A function declared as <paramref name="keyName"/>, which is also its display name.</summary>
    public static FunctionSymbol Function(string keyName, string namespaceName = "")
    {
        return new FunctionSymbol
        {
            Name = keyName,
            KeyName = keyName,
            Namespace = namespaceName,
            NameRange = Anywhere,
            FullRange = Anywhere,
        };
    }

    /// <summary>A class written as <paramref name="name"/>; its key is the lowercase form, as the extractor makes it.</summary>
    public static ClassSymbol Class(string name, string namespaceName = "")
    {
        return new ClassSymbol
        {
            Name = name,
            KeyName = name.ToLowerInvariant(),
            Namespace = namespaceName,
            NameRange = Anywhere,
            FullRange = Anywhere,
        };
    }
}
