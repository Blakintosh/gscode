using GSCode.Server.Handlers;
using GSCode.Workspace.Completion;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// completionItem/resolve is a round trip: the server stashes an identity in CompletionItem.Data,
/// the client hands it straight back, and the server has to find the symbol again from nothing
/// else. The KEYS are therefore part of the contract — the same class of wire-shape dependency
/// that broke the code-lens click, so they are pinned here rather than assumed.
/// </summary>
public class CompletionResolveDataTests
{
    private static readonly DocumentUri Uri =
        DocumentUri.FromFileSystemPath(@"C:\bo3\share\raw\scripts\util.gsc");

    [Fact]
    public void CarriesEverythingResolveNeedsToFindTheSymbol()
    {
        CompletionEntry entry = new(
            "give_weapon", CompletionKind.Function, "util::give_weapon", "give_weapon($0)", Namespace: "util");

        JObject data = CompletionHandler.ResolveData(entry, Uri);

        Assert.Equal("Function", data.Value<string>("kind"));
        Assert.Equal("give_weapon", data.Value<string>("name"));
        Assert.Equal("util", data.Value<string>("ns"));
        Assert.Contains("util.gsc", data.Value<string>("uri")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryValueIsAString()
    {
        // Plain strings only: Data crosses the client untouched, so anything with a serializer
        // opinion about casing or shape is a bug waiting to happen.
        JObject data = CompletionHandler.ResolveData(
            new CompletionEntry("cVehicle", CompletionKind.Class), Uri);

        Assert.All(data.Properties(), p => Assert.Equal(JTokenType.String, p.Value.Type));
    }

    [Fact]
    public void ANamespacelessSymbolCarriesAnEmptyNamespace()
    {
        // Builtins and macros have no namespace; resolve reads this as "search without one"
        // rather than as missing data.
        JObject data = CompletionHandler.ResolveData(
            new CompletionEntry("IPrintLn", CompletionKind.Function, "builtin"), Uri);

        Assert.Equal("", data.Value<string>("ns"));
    }
}
