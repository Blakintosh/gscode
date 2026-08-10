using GSCode.Core.Text;
using GSCode.Server.Handlers;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The code-lens command's arguments cross a JSON boundary into TypeScript, so their SHAPE is
/// part of the contract. Sending the position as an object previously put C# PascalCase on the
/// wire — `Arguments` is a JArray, and OmniSharp's camelCase resolver never rewrites an
/// already-materialized JToken — so the client read `position.line` as undefined and
/// vscode.Position threw "Unexpected type". These pin the primitives.
/// </summary>
public class CodeLensArgumentTests
{
    private static JArray Arguments()
    {
        DocumentUri uri = DocumentUri.FromFileSystemPath(@"C:\bo3\share\raw\scripts\util.gsc");
        return CodeLensHandler.ShowReferencesArguments(uri, new Position(12, 4));
    }

    [Fact]
    public void PositionIsSentAsTwoNumbers_NotAnObject()
    {
        JArray arguments = Arguments();

        Assert.Equal(3, arguments.Count);
        Assert.Equal(JTokenType.String, arguments[0].Type);
        Assert.Equal(JTokenType.Integer, arguments[1].Type);
        Assert.Equal(JTokenType.Integer, arguments[2].Type);

        Assert.Equal(12, arguments[1].Value<int>());
        Assert.Equal(4, arguments[2].Value<int>());
    }

    [Fact]
    public void NoArgumentIsAnObject()
    {
        // The regression guard: an object here is what broke the click, whatever its casing.
        Assert.All(Arguments(), token => Assert.NotEqual(JTokenType.Object, token.Type));
    }

    [Fact]
    public void UriRoundTripsAsAParseableString()
    {
        string uriText = Arguments()[0].Value<string>()!;

        // The client does vscode.Uri.parse on this, so it has to survive the round trip.
        Assert.StartsWith("file:", uriText, StringComparison.Ordinal);
        Assert.Contains("util.gsc", uriText, StringComparison.OrdinalIgnoreCase);
    }
}
