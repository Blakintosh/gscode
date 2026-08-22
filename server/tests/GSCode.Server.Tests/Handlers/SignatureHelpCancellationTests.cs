using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Signature help drops a request the client has already cancelled.
///
/// It is the third read path with no debounce in front of it — <c>ResolveFresh</c> runs the whole
/// per-file analysis when the document is stale — and it retriggers on every ',' through an
/// argument list, so a client typing an argument cancels the previous request and sends another.
/// The token was taken and never read.
/// </summary>
public class SignatureHelpCancellationTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ScriptPath => Path.Combine(Raw, @"scripts\shared\sig_test.gsc");

    /// <summary>
    /// A call being written: the cursor sits in the first argument. A BUILTIN, so the signature
    /// comes from the bundled API and this pins the handler rather than the database's contents.
    /// </summary>
    private const string Source = "#namespace sig;\nfunction caller()\n{\n    x = Abs( \n}\n";

    private static LspPosition InsideTheCall => new(3, 12);

    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static SignatureHelpHandler BuildHandler()
    {
        ScriptDatabase database = new();
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        documents.AnalyzeIfStale(documents.Open(ScriptPath, Source, 1));

        NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()));

        return new SignatureHelpHandler(
            support,
            new SignatureEngine(database, BuiltinApiSet.Load(ApiDirectory)),
            TextDocumentSelector.ForLanguage("gsc"));
    }

    private static Task<SignatureHelp?> HandleAsync(CancellationToken cancellationToken)
    {
        return BuildHandler().Handle(
            new SignatureHelpParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(ScriptPath) },
                Position = InsideTheCall,
            },
            cancellationToken);
    }

    [Fact]
    public async Task ACancelledRequestIsNotAnswered()
    {
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        // Cancelled rather than empty: the protocol wants a cancelled response, and an empty one is
        // a claim that there is no signature here.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HandleAsync(source.Token));
    }

    [Fact]
    public async Task AnUncancelledRequestStillAnswers()
    {
        // The control, so the check above cannot pass by the handler simply never answering.
        SignatureHelp? help = await HandleAsync(CancellationToken.None);

        Assert.NotNull(help);
        Assert.Contains(help.Signatures, s => s.Label.StartsWith("Abs(", StringComparison.Ordinal));
    }
}
