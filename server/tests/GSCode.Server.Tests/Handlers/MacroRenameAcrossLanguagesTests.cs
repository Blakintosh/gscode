using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Renaming a macro from a script, driven through RenameHandler the way the editor drives it.
///
/// A macro is declared in a <c>.gsh</c>, which is inserted into <c>.gsc</c> and <c>.csc</c> alike,
/// so the two language worlds are not a scope for it. The reported fault was that the rename query
/// took its scope from the ASKING file's language: started in the <c>.gsc</c> it rewrote the
/// <c>.gsc</c> and the <c>.gsh</c> and left every <c>.csc</c> use spelled the old way, expanding to
/// nothing. Starting the same rename from the <c>.gsh</c> worked, which is what made it easy to
/// miss by hand.
/// </summary>
public class MacroRenameAcrossLanguagesTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private const string HeaderRawPath = @"scripts\shared\flags.gsh";
    private static string HeaderPath => Path.Combine(Raw, HeaderRawPath);
    private static string GscPath => Path.Combine(Raw, @"scripts\shared\flags_test.gsc");
    private static string CscPath => Path.Combine(Raw, @"scripts\shared\flags_test.csc");

    private const string HeaderSource = "#define MAX_FLAGS 8\n";

    private const string ScriptSource =
        "#insert scripts\\shared\\flags.gsh;\nfunction f()\n{\n    x = MAX_FLAGS;\n}\n\nfunction g()\n{\n    f();\n}\n";

    /// <summary>The one MAX_FLAGS use in <see cref="ScriptSource"/>: line 3, inside the name.</summary>
    private static LspPosition MacroUse => new(3, 10);

    /// <summary>The one f() call in <see cref="ScriptSource"/>: line 8, inside the name.</summary>
    private static LspPosition FunctionCall => new(8, 4);

    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    /// <summary>Serves the one header, so the scripts' MAX_FLAGS is a macro use and not a variable.</summary>
    private sealed class HeaderInsertProvider : IInsertProvider
    {
        private static readonly SourceText Text = SourceText.From(HeaderSource);
        private static readonly ImmutableArray<Token> Tokens = Lexer.Lex(Text).Tokens;

        public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
        {
            inserted = new InsertedFile(GSCode.Core.Paths.PathUtil.NormalizeAbsolute(HeaderPath), Text, Tokens);
            return string.Equals(rawInsertPath, HeaderRawPath, StringComparison.OrdinalIgnoreCase);
        }

        public bool TryResolveInsertPath(string rawInsertPath, out string resolvedPath)
        {
            resolvedPath = GSCode.Core.Paths.PathUtil.NormalizeAbsolute(HeaderPath);
            return string.Equals(rawInsertPath, HeaderRawPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ParseResult AnalyzeAt(string source, string path, ScriptLanguage language)
    {
        return ScriptAnalysis.Analyze(
            path, language, SourceText.From(source), new HeaderInsertProvider(), new NameTable());
    }

    private static RenameHandler BuildHandler(string askingPath, ScriptLanguage askingLanguage)
    {
        ScriptDatabase database = new();
        database.Commit(AnalyzeAt(HeaderSource, HeaderPath, ScriptLanguage.Gsh), ResolutionContext.RawContext, false, HeaderRawPath);
        database.Commit(
            AnalyzeAt(ScriptSource, GscPath, ScriptLanguage.Gsc),
            ResolutionContext.RawContext, false, @"scripts\shared\flags_test.gsc");
        database.Commit(
            AnalyzeAt(ScriptSource, CscPath, ScriptLanguage.Csc),
            ResolutionContext.RawContext, false, @"scripts\shared\flags_test.csc");

        DocumentStore documents = new(static _ => new HeaderInsertProvider(), new NameTable());
        documents.AnalyzeIfStale(documents.Open(askingPath, ScriptSource, 1));

        NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()));

        return new RenameHandler(
            support,
            BuiltinApiSet.Load(ApiDirectory),
            ObjectFields.Load(ApiDirectory),
            TextDocumentSelector.ForLanguage(askingLanguage == ScriptLanguage.Csc ? "csc" : "gsc"));
    }

    private static async Task<WorkspaceEdit?> RenameAsync(string askingPath, ScriptLanguage language, LspPosition position)
    {
        RenameHandler handler = BuildHandler(askingPath, language);

        return await handler.Handle(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(askingPath) },
                Position = position,
                NewName = "FLAG_LIMIT",
            },
            CancellationToken.None);
    }

    private static bool Touches(WorkspaceEdit edit, string extension)
    {
        foreach ( KeyValuePair<DocumentUri, IEnumerable<TextEdit>> change in edit.Changes! )
        {
            if ( change.Key.GetFileSystemPath().EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                && change.Value.Any() )
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task AMacroRenamedFromTheGsc_EditsTheGshAndTheCscToo()
    {
        WorkspaceEdit? edit = await RenameAsync(GscPath, ScriptLanguage.Gsc, MacroUse);

        Assert.NotNull(edit);
        Assert.True(Touches(edit, ".gsc"), "the asking file itself");
        Assert.True(Touches(edit, ".gsh"), "the declaration");
        Assert.True(Touches(edit, ".csc"), "the other world's uses, which the old scope dropped");
    }

    [Fact]
    public async Task AMacroRenamedFromTheCsc_EditsTheGshAndTheGscToo()
    {
        // The mirror case, for the same reason: neither world owns the macro.
        WorkspaceEdit? edit = await RenameAsync(CscPath, ScriptLanguage.Csc, MacroUse);

        Assert.NotNull(edit);
        Assert.True(Touches(edit, ".csc"));
        Assert.True(Touches(edit, ".gsh"));
        Assert.True(Touches(edit, ".gsc"));
    }

    [Fact]
    public async Task AFunctionRenamedFromTheGsc_LeavesTheCscAlone()
    {
        // The isolation the macro rule must not widen: flags_test.gsc and flags_test.csc each
        // declare their own f(), and they are different functions.
        WorkspaceEdit? edit = await RenameAsync(GscPath, ScriptLanguage.Gsc, FunctionCall);

        Assert.NotNull(edit);
        Assert.True(Touches(edit, ".gsc"));
        Assert.False(Touches(edit, ".csc"));
    }
}
