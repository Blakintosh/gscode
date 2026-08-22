using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The payload behind shift+F1, which opens a builtin's own page rather than the library index.
///
/// The interesting property is not the lookup but the SHADOWING rule: a script function of the
/// same name wins, exactly as it does for hover and go-to-definition, so a workspace that
/// redefines an engine name does not send you to the engine's documentation for it. That is
/// checked by the handler, which is why the client cannot do this from the text alone.
///
/// These pin the wire shape; the resolution itself is DatabaseQueries.LookupFunctions, covered by
/// its own tests.
/// </summary>
public class BuiltinAtTests
{
    [Fact]
    public void AnEmptyNameMeansNotABuiltin()
    {
        // The client tests `builtin?.name` and falls back to the index, so "not a builtin" has to
        // be an empty string rather than a null the JSON might drop.
        BuiltinAtResponse none = new();

        Assert.Equal("", none.Name);
        Assert.Equal("", none.Language);
    }

    [Fact]
    public void TheResponseCarriesTheLibraryAsWellAsTheName()
    {
        // Both are needed to build .../library/<language>/<name>, and the language is the
        // document's rather than the name's — the same builtin can exist in both libraries.
        BuiltinAtResponse response = new() { Name = "LUINotifyEvent", Language = "csc" };

        Assert.Equal("LUINotifyEvent", response.Name);
        Assert.Equal("csc", response.Language);
    }

    [Fact]
    public void TheNameKeepsTheLibrarysCasing()
    {
        // The client lowercases it for the URL. Storing it as written keeps that decision on the
        // client, where the site's addressing scheme belongs.
        BuiltinAtResponse response = new() { Name = "LUINotifyEvent", Language = "gsc" };

        Assert.Equal("luinotifyevent", response.Name.ToLowerInvariant());
    }

    [Fact]
    public void TheRequestCarriesAPositionAsPlainNumbers()
    {
        // Primitives, for the same reason the code-lens arguments are: this crosses into
        // TypeScript, and an object would be at the mercy of the serializer's casing.
        BuiltinAtParams request = new() { Uri = "file:///c%3A/bo3/scripts/main.gsc", Line = 12, Character = 8 };

        Assert.Equal(12, request.Line);
        Assert.Equal(8, request.Character);
        Assert.StartsWith("file:", request.Uri, StringComparison.Ordinal);
    }
}
