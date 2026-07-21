using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// Radiant keys marked "client" in keys.txt exist only on the CSC side, so a GSC file must
/// never be offered or shown them.
/// </summary>
public class RadiantKeyVisibilityTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ObjectFields Fields => ObjectFields.Load(ApiDirectory);

    [Fact]
    public void ClientOnlyKey_IsHiddenFromGsc_AndVisibleToCsc()
    {
        ObjectFields fields = Fields;

        // classname is client-only in keys.txt.
        Assert.NotNull(fields.FindRadiantKey("classname"));
        Assert.Null(fields.FindRadiantKey("classname", ScriptLanguage.Gsc));
        Assert.NotNull(fields.FindRadiantKey("classname", ScriptLanguage.Csc));
    }

    [Fact]
    public void SharedKey_IsVisibleToBothLanguages()
    {
        ObjectFields fields = Fields;

        Assert.NotNull(fields.FindRadiantKey("origin", ScriptLanguage.Gsc));
        Assert.NotNull(fields.FindRadiantKey("origin", ScriptLanguage.Csc));
    }

    [Fact]
    public void CscSeesAtLeastAsManyKeysAsGsc()
    {
        ObjectFields fields = Fields;

        ImmutableArray<RadiantKey> gsc = fields.RadiantKeysFor(ScriptLanguage.Gsc);
        ImmutableArray<RadiantKey> csc = fields.RadiantKeysFor(ScriptLanguage.Csc);

        Assert.NotEmpty(gsc);
        Assert.True(csc.Length > gsc.Length, "CSC should additionally see the client-only keys.");
        Assert.DoesNotContain(gsc, key => string.Equals(key.Side, "client", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FieldNames_ExposesTheEngineFieldSurface()
    {
        // Completion needs the full name list, not just per-name lookup.
        ImmutableArray<string> names = Fields.FieldNames();

        Assert.NotEmpty(names);
        Assert.Contains(names, name => string.Equals(name, "origin", StringComparison.OrdinalIgnoreCase));
    }
}
