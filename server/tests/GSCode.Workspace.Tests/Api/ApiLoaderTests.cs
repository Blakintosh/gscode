using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

public class ApiLoaderTests
{
    // The bundled Api folder sits next to the test assembly (copied from GSCode.Workspace).
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    [Fact]
    public void Load_Gsc_HasManyFunctions()
    {
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);
        Assert.True(api.Count > 1000, $"expected a large GSC library, got {api.Count}");
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);

        BuiltinFunction? lower = api.Find("abs");
        BuiltinFunction? mixed = api.Find("Abs");

        Assert.NotNull(lower);
        Assert.NotNull(mixed);
        Assert.Equal(mixed!.Name, lower!.Name);
        Assert.Single(mixed.Overloads);
        Assert.Single(mixed.Overloads[0].Parameters);
    }

    [Fact]
    public void RenderBuiltin_ProducesSignatureAndDescription()
    {
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);
        BuiltinFunction abs = api.Find("Abs")!;

        string markdown = MarkdownDocRenderer.RenderBuiltin(abs);

        Assert.Contains("Abs(", markdown, StringComparison.Ordinal);
        Assert.Contains("absolute value", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderBuiltin_SurfacesParameterTypesAndDescriptions()
    {
        // The signature line shows only names, but the bundled library documents nearly every
        // parameter individually — 3,240 of the 3,289 GSC parameters carry a description.
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);

        string markdown = MarkdownDocRenderer.RenderBuiltin(api.Find("Abs")!);

        Assert.Contains("Parameters:", markdown, StringComparison.Ordinal);
        Assert.Contains("`value`", markdown, StringComparison.Ordinal);
        Assert.Contains("absolute value of", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderBuiltin_SurfacesTheReturnType()
    {
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);

        Assert.Contains("Returns:", MarkdownDocRenderer.RenderBuiltin(api.Find("Abs")!), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBuiltin_WarnsOnDevOnlyBuiltins()
    {
        // Calling one of these outside /# #/ compiles fine and then fails on a server without
        // developer script, so the warning belongs above the description rather than buried under it.
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc);
        BuiltinFunction printLn = api.Find("PrintLn")!;

        Assert.True(printLn.IsDevOnly);
        Assert.Contains("Development only", MarkdownDocRenderer.RenderBuiltin(printLn), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBuiltin_OmitsTheReturnLineForVoidBuiltins()
    {
        // Void is the common case, and a "Returns: void" line would be noise on most hovers.
        BuiltinFunction voidBuiltin = new(
            "DoThing",
            "Does the thing.",
            [new BuiltinOverload(null, [], "", ReturnsVoid: true)],
            "");

        Assert.DoesNotContain("Returns:", MarkdownDocRenderer.RenderBuiltin(voidBuiltin), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBuiltin_TheOverloadsListDoesNotRepeatThePrimaryPrototype()
    {
        // The comment above the loop says "additional overloads listed... under the primary
        // prototype" — but the loop iterated every overload, the primary included, so a
        // two-overload builtin showed its first signature twice: once in the fenced code block
        // at the top and again as the first bullet of its own "Overloads:" list.
        BuiltinOverload first = new("player", [new BuiltinParameter("origin", "", true, "vector")], "", false);
        BuiltinOverload second = new(
            "player", [new BuiltinParameter("origin", "", true, "vector"), new BuiltinParameter("angles", "", true, "vector")], "", false);
        BuiltinFunction builtin = new("SpawnSpectator", "", [first, second], "");

        string markdown = MarkdownDocRenderer.RenderBuiltin(builtin);
        int overloadsIndex = markdown.IndexOf("Overloads:", StringComparison.Ordinal);
        string overloadsSection = markdown[overloadsIndex..];

        Assert.DoesNotContain("* `<player> SpawnSpectator(origin)`", overloadsSection, StringComparison.Ordinal);
        Assert.Contains("* `<player> SpawnSpectator(origin, angles)`", overloadsSection, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBuiltin_WithNoOverloadData_ShowsUnknownRatherThanZeroParameters()
    {
        // 270 of BO1's 1,377 GSC builtins carry no overload data at all (222 for WAW, 55 for
        // MW2, 43 for CoD4). The signature line rendered them as `Name()`, which claims the
        // function takes nothing — a fact the data never stated.
        BuiltinFunction noData = new("AddTestClient", "", [], "");

        Assert.Contains("AddTestClient(...)", MarkdownDocRenderer.RenderBuiltin(noData), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderFunction_IncludesNamespaceParamsAndDoc()
    {
        FunctionSymbol function = new()
        {
            Name = "give_weapon",
            KeyName = "give_weapon",
            Namespace = "util",
            Parameters =
            [
                new ParameterSymbol("weapon", false, ""),
                new ParameterSymbol("ammo", false, "0"),
            ],
            NameRange = TextRange.Empty,
            FullRange = TextRange.Empty,
            Doc = ScriptDocComment.Parse("Summary: Gives a weapon.\nMandatoryArg: <weapon>: the weapon"),
        };

        string markdown = MarkdownDocRenderer.RenderFunction(function);

        Assert.Contains("util::give_weapon(weapon, ammo = 0)", markdown, StringComparison.Ordinal);
        Assert.Contains("Gives a weapon.", markdown, StringComparison.Ordinal);
        Assert.Contains("weapon", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFile_ALockedFile_ReportsFailureRatherThanThrowing()
    {
        // Only JsonException was caught, so a file that EXISTS but cannot be READ — locked by
        // another process, an AV scan, a permissions problem — crashed whatever called Load
        // instead of being treated as one more "could not parse this bundled data" case.
        string path = Path.Combine(Path.GetTempPath(), $"gscode_api_locked_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");

        try
        {
            using FileStream exclusiveLock = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            List<(string Path, Exception Exception)> failures = [];
            BuiltinApi api = ApiLoader.LoadFile(path, (failedPath, exception) => failures.Add((failedPath, exception)));

            Assert.Equal(0, api.Count);
            Assert.Single(failures);
            Assert.Equal(path, failures[0].Path);
            Assert.IsType<IOException>(failures[0].Exception);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RenderMacro_ShowsDefineAndDoc()
    {
        MacroRecord macro = new("MAX_HEALTH", false, [], TextRange.Empty, "// the cap");

        string markdown = MarkdownDocRenderer.RenderMacro(macro);

        Assert.Contains("#define MAX_HEALTH", markdown, StringComparison.Ordinal);
        Assert.Contains("the cap", markdown, StringComparison.Ordinal);
    }
}
