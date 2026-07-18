using System.Collections.Immutable;
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
    public void RenderMacro_ShowsDefineAndDoc()
    {
        MacroRecord macro = new("MAX_HEALTH", false, [], TextRange.Empty, "// the cap");

        string markdown = MarkdownDocRenderer.RenderMacro(macro);

        Assert.Contains("#define MAX_HEALTH", markdown, StringComparison.Ordinal);
        Assert.Contains("the cap", markdown, StringComparison.Ordinal);
    }
}
