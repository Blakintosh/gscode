using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// Radiant keys marked "client" in keys.txt exist only on the CSC side. The bundled data
/// currently contains no client-only keys at all (classname, the sole one upstream, is
/// corrected to "both" by the field-data generator), so the filtering mechanism is exercised
/// against synthetic data while the bundled artifact is asserted separately.
/// </summary>
public class RadiantKeyVisibilityTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ObjectFields Bundled => ObjectFields.Load(ApiDirectory);

    /// <summary>Writes a throwaway Api directory holding just a synthetic radiant-keys artifact.</summary>
    private static ObjectFields LoadSynthetic(string radiantKeysJson)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gscode-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "t7_radiant_keys.json"), radiantKeysJson);

        return ObjectFields.Load(directory);
    }

    [Fact]
    public void ClientOnlyKey_IsHiddenFromGsc_AndVisibleToCsc()
    {
        ObjectFields fields = LoadSynthetic("""
            [
              { "Name": "only_client", "Type": "string", "Side": "client", "Comment": "" },
              { "Name": "shared", "Type": "vector", "Side": "both", "Comment": "" }
            ]
            """);

        Assert.NotNull(fields.FindRadiantKey("only_client"));
        Assert.Null(fields.FindRadiantKey("only_client", ScriptLanguage.Gsc));
        Assert.NotNull(fields.FindRadiantKey("only_client", ScriptLanguage.Csc));
    }

    [Fact]
    public void SharedKey_IsVisibleToBothLanguages()
    {
        ObjectFields fields = LoadSynthetic("""
            [
              { "Name": "only_client", "Type": "string", "Side": "client", "Comment": "" },
              { "Name": "shared", "Type": "vector", "Side": "both", "Comment": "" }
            ]
            """);

        Assert.NotNull(fields.FindRadiantKey("shared", ScriptLanguage.Gsc));
        Assert.NotNull(fields.FindRadiantKey("shared", ScriptLanguage.Csc));
    }

    [Fact]
    public void EnumerationDropsClientOnlyKeysForGsc()
    {
        ObjectFields fields = LoadSynthetic("""
            [
              { "Name": "only_client", "Type": "string", "Side": "client", "Comment": "" },
              { "Name": "shared", "Type": "vector", "Side": "both", "Comment": "" }
            ]
            """);

        ImmutableArray<RadiantKey> gsc = fields.RadiantKeysFor(ScriptLanguage.Gsc);
        ImmutableArray<RadiantKey> csc = fields.RadiantKeysFor(ScriptLanguage.Csc);

        Assert.Equal("shared", Assert.Single(gsc).Name);
        Assert.Equal(2, csc.Length);
    }

    [Fact]
    public void BundledClassname_IsCorrectedToBothSides()
    {
        // keys.txt marks classname client-only, which is wrong — GSC reads it constantly.
        // The generator corrects it, and this pins the correction against a regeneration.
        RadiantKey? classname = Bundled.FindRadiantKey("classname");

        Assert.NotNull(classname);
        Assert.Equal("both", classname!.Side);
        Assert.NotNull(Bundled.FindRadiantKey("classname", ScriptLanguage.Gsc));
        Assert.NotNull(Bundled.FindRadiantKey("classname", ScriptLanguage.Csc));
    }

    [Fact]
    public void BundledKeys_CarryNoClientOnlyEntries()
    {
        // Documents the current state: if a tools update reintroduces one, this fails loudly
        // and the generator's correction table gets revisited.
        ImmutableArray<RadiantKey> gsc = Bundled.RadiantKeysFor(ScriptLanguage.Gsc);
        ImmutableArray<RadiantKey> csc = Bundled.RadiantKeysFor(ScriptLanguage.Csc);

        Assert.NotEmpty(gsc);
        Assert.Equal(csc.Length, gsc.Length);
    }

    [Fact]
    public void FieldNames_ExposesTheEngineFieldSurface()
    {
        // Completion needs the full name list, not just per-name lookup.
        ImmutableArray<string> names = Bundled.FieldNames();

        Assert.NotEmpty(names);
        Assert.Contains(names, name => string.Equals(name, "origin", StringComparison.OrdinalIgnoreCase));
    }
}
