using GSCode.Core;
using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Handlers;

/// <summary>
/// Quick fixes over the open document: "Remove duplicate #using" for a redundant import,
/// "Add #using ..." for a qualified call whose defining file the document does not yet import
/// (the natural fix for the NamespaceNotImported lint), and for a call that resolved to nothing
/// (5013/5014) either declaring the function here or importing and qualifying it from wherever it
/// does exist.
/// </summary>
public sealed class CodeActionHandler : CodeActionHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public CodeActionHandler(DocumentStore documents, NavigationSupport support, TextDocumentSelector selector)
    {
        _documents = documents;
        _support = support;
        _selector = selector;
    }

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = _selector,
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
        };
    }

    // No lazy resolution — actions carry their edit up front.
    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    public override Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<CommandOrCodeActionContainer?>(null);
        }

        ParseResult result = document.LatestResult;
        TextRange selection = request.Range.ToCore();
        List<CommandOrCodeAction> actions = [];

        foreach ( UsingNode duplicate in FindRemovableDuplicates(result, selection) )
        {
            actions.Add(new CommandOrCodeAction(BuildRemoveAction(request.TextDocument.Uri, duplicate)));
        }

        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is not null )
        {
            Position insertAt = UsingInsertionPoint(result);
            foreach ( string usingPath in FindMissingUsings(result, target.Store, target.ContextId, target.Path, selection) )
            {
                actions.Add(new CommandOrCodeAction(BuildAddUsingAction(request.TextDocument.Uri, usingPath, insertAt)));
            }
        }

        AddDiagnosticFixes(request, result, actions, target);

        return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer(actions));
    }

    /// <summary>
    /// Fixes driven by the diagnostics the client reported for the selection. Keyed off the
    /// request's context rather than re-derived, because the workspace lints run in
    /// TextSyncHandler and are not recomputable from the ParseResult alone.
    /// </summary>
    /// <param name="target">
    /// The workspace view, when there is one. Only the unresolved-call fixes need it — everything
    /// else here is answerable from the document alone — so it is optional rather than a
    /// precondition, and those fixes simply go unoffered on a document with no indexed context.
    /// </param>
    internal static void AddDiagnosticFixes(
        CodeActionParams request,
        ParseResult result,
        List<CommandOrCodeAction> actions,
        NavigationTarget? target = null)
    {
        DocumentUri uri = request.TextDocument.Uri;
        List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic> unusedUsings = [];

        foreach ( OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic diagnostic in request.Context.Diagnostics )
        {
            switch ( CodeOf(diagnostic) )
            {
                case GscDiagnosticCode.UnusedUsing:
                    unusedUsings.Add(diagnostic);
                    actions.Add(new CommandOrCodeAction(BuildDeleteLineAction(uri, result, diagnostic)));
                    continue;
                case GscDiagnosticCode.PreferBooleanLiteral:
                    AddBooleanLiteralFix(uri, result, diagnostic, actions);
                    continue;
                case GscDiagnosticCode.UsingAfterDeclaration:
                    AddMoveUsingFix(uri, result, diagnostic, actions);
                    continue;

                // Both halves of an unresolved call get the same two offers, because from the fix's
                // point of view they are one situation: a name with no definition behind it. Which
                // code fired says where we LOOKED, not what the user should do about it.
                case GscDiagnosticCode.ScriptFunctionNotFound:
                case GscDiagnosticCode.BuiltinFunctionNotFound:
                    foreach ( CodeAction fix in UnresolvedCallFixes(
                        uri, result, target?.Store, target?.ContextId ?? "", target?.Path ?? "", diagnostic) )
                    {
                        actions.Add(new CommandOrCodeAction(fix));
                    }

                    continue;
                default:
                    continue;
            }
        }

        // One click for the common cleanup, rather than N separate fixes.
        if ( unusedUsings.Count > 1 )
        {
            actions.Add(new CommandOrCodeAction(BuildRemoveAllUnusedAction(uri, unusedUsings)));
        }
    }

    private static GscDiagnosticCode? CodeOf(OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic diagnostic)
    {
        if ( diagnostic.Code is null || !diagnostic.Code.Value.IsLong )
        {
            return null;
        }

        return (GscDiagnosticCode)diagnostic.Code.Value.Long;
    }

    /// <summary>The whole line a range sits on, including its trailing newline.</summary>
    private static TextRange LineRangeOf(TextRange range)
    {
        return new TextRange(new Position(range.Start.Line, 0), new Position(range.Start.Line + 1, 0));
    }

    /// <summary>The trimmed source of a line, used to name an action after what it acts on.</summary>
    private static string LineTextOf(ParseResult result, int line)
    {
        if ( line < 0 || line >= result.Text.LineCount )
        {
            return "";
        }

        int start = result.Text.GetOffset(new Position(line, 0));
        int end = line + 1 < result.Text.LineCount
            ? result.Text.GetOffset(new Position(line + 1, 0))
            : result.Text.Length;

        return result.Text.Text[start..end].Trim().TrimEnd(SemiColon);
    }

    private const char SemiColon = ';';

    private static CodeAction BuildDeleteLineAction(DocumentUri uri, ParseResult result, OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic diagnostic)
    {
        TextRange range = diagnostic.Range.ToCore();
        TextEdit edit = new() { Range = LineRangeOf(range).ToLsp(), NewText = "" };

        return new CodeAction
        {
            Title = "Remove unused " + LineTextOf(result, range.Start.Line),
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic>(diagnostic),
            Edit = SingleEdit(uri, edit),
        };
    }

    private static CodeAction BuildRemoveAllUnusedAction(DocumentUri uri, List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic> unusedUsings)
    {
        // Whole-line deletions on distinct lines never overlap, so order does not matter.
        HashSet<int> lines = [];
        List<TextEdit> edits = [];
        foreach ( OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic diagnostic in unusedUsings )
        {
            TextRange range = diagnostic.Range.ToCore();
            if ( lines.Add(range.Start.Line) )
            {
                edits.Add(new TextEdit { Range = LineRangeOf(range).ToLsp(), NewText = "" });
            }
        }

        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new() { [uri] = edits };

        return new CodeAction
        {
            Title = "Remove all " + edits.Count + " unused #using directives",
            Kind = CodeActionKind.QuickFix,
            Edit = new WorkspaceEdit { Changes = changes },
        };
    }

    /// <summary>
    /// Replaces a literal 0/1 with false/true. The replacement is read from the source at the
    /// diagnostic's range rather than parsed out of its message, so the fix cannot drift if the
    /// wording ever changes.
    /// </summary>
    private static void AddBooleanLiteralFix(DocumentUri uri, ParseResult result, OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic diagnostic, List<CommandOrCodeAction> actions)
    {
        TextRange range = diagnostic.Range.ToCore();
        int start = result.Text.GetOffset(range.Start);
        int end = result.Text.GetOffset(range.End);
        if ( start >= end || end > result.Text.Length )
        {
            return;
        }

        string literal = result.Text.Text[start..end].Trim();
        string replacement = "";
        if ( literal == "1" )
        {
            replacement = "true";
        }
        else if ( literal == "0" )
        {
            replacement = "false";
        }

        if ( replacement.Length == 0 )
        {
            return;
        }

        TextEdit edit = new() { Range = range.ToLsp(), NewText = replacement };
        actions.Add(new CommandOrCodeAction(new CodeAction
        {
            Title = "Replace " + literal + " with " + replacement,
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic>(diagnostic),
            Edit = SingleEdit(uri, edit),
        }));
    }

    /// <summary>
    /// Moves a #using that appears after the first declaration up to where imports belong. Two
    /// edits — delete the offending line, insert it at the top — applied as one operation.
    /// </summary>
    private static void AddMoveUsingFix(DocumentUri uri, ParseResult result, OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic diagnostic, List<CommandOrCodeAction> actions)
    {
        TextRange range = diagnostic.Range.ToCore();
        string directive = LineTextOf(result, range.Start.Line);
        if ( directive.Length == 0 )
        {
            return;
        }

        Position insertAt = UsingInsertionPoint(result, range.Start.Line);
        if ( insertAt.Line >= range.Start.Line )
        {
            // Nowhere earlier to move it to; leave the diagnostic without a fix.
            return;
        }

        List<TextEdit> edits =
        [
            new TextEdit { Range = LineRangeOf(range).ToLsp(), NewText = "" },
            new TextEdit { Range = new TextRange(insertAt, insertAt).ToLsp(), NewText = directive + ";" + "\n" },
        ];

        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new() { [uri] = edits };

        actions.Add(new CommandOrCodeAction(new CodeAction
        {
            Title = "Move " + directive + " above the first declaration",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit { Changes = changes },
        }));
    }

    /// <summary>
    /// The offers for a call that resolved to nothing (5013/5014): declare the function here, and —
    /// where the name DOES exist somewhere the file cannot currently see — import that file and
    /// qualify the call so it reaches it.
    ///
    /// The diagnostic's range covers the NAME TOKEN only, never the <c>ns::</c> in front of it (see
    /// <c>SymbolExtractor.RecordCalleeReference</c>, which records the reference against
    /// <c>NameToken</c>). That is what makes both edits simple: qualifying is an insert at the range
    /// start, and re-qualifying replaces the qualifier found by scanning back from it.
    /// </summary>
    internal static List<CodeAction> UnresolvedCallFixes(
        DocumentUri uri,
        ParseResult result,
        LanguageStore? store,
        string contextId,
        string askingPath,
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic diagnostic)
    {
        List<CodeAction> fixes = [];
        TextRange range = diagnostic.Range.ToCore();
        string name = TextAt(result, range);
        if ( name.Length == 0 || !IsIdentifier(name) )
        {
            return fixes;
        }

        TextRange? qualifier = QualifierRange(result, range);

        // Declaring it HERE only answers a call that named no other location. `other::foo()` says
        // where it expects to find the function, and writing foo into this file would not put it
        // there — that fix would have to edit the other file, which is a different operation and a
        // different set of ways to be wrong.
        if ( qualifier is null )
        {
            fixes.Add(BuildCreateFunctionAction(uri, result, name));
        }

        if ( store is null )
        {
            return fixes;
        }

        // Under a merge dialect an unqualified call already resolves by NAME across everything the
        // include graph pulled in, so a call that reached this diagnostic is not one an import would
        // fix. Only a namespace dialect can have the function present but out of reach.
        if ( !GameProfile.Active.ResolvesByNamespace )
        {
            return fixes;
        }

        HashSet<string> ownNamespaces = new(StringComparer.Ordinal);
        foreach ( string declared in result.Extraction.DeclaredNamespaces )
        {
            ownNamespaces.Add(declared);
        }

        HashSet<string> existingUsings = new(StringComparer.Ordinal);
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is UsingNode usingNode )
            {
                existingUsings.Add(StripExtension(NormalizePath(usingNode.Path)));
            }
        }

        Position insertAt = UsingInsertionPoint(result);
        HashSet<string> offered = new(StringComparer.Ordinal);

        // Namespace left null: every namespace is searched, which is the whole point — the caller
        // already knows the one that was written does not have it.
        foreach ( ResolvedFunction resolved in DatabaseQueries.LookupFunctions(
            store, contextId, askingPath, null, name.ToLowerInvariant()) )
        {
            string? namespaceName = resolved.Function.Namespace;
            if ( namespaceName is null || ownNamespaces.Contains(namespaceName) )
            {
                continue;
            }

            if ( resolved.Record.RelativePath.Length == 0 )
            {
                continue;
            }

            string usingPath = StripExtension(NormalizePath(resolved.Record.RelativePath));

            // One offer per namespace+file pair. The same namespace spread over several files is
            // normal, and each file is a genuinely different import.
            if ( !offered.Add(namespaceName + "|" + usingPath) )
            {
                continue;
            }

            fixes.Add(BuildImportAndQualifyAction(
                uri, namespaceName, usingPath, existingUsings.Contains(usingPath), insertAt, range, qualifier));
        }

        return fixes;
    }

    /// <summary>
    /// Declares the missing function at the end of the file, opened the way the dialect declares
    /// one — BO3 writes `function foo()`, the merge games the bare name.
    ///
    /// No parameter list, deliberately: the call site's argument count is not on the diagnostic, and
    /// a guessed list that disagreed with the call would be worse than an empty one the user fills
    /// in with the caret already inside it.
    /// </summary>
    private static CodeAction BuildCreateFunctionAction(DocumentUri uri, ParseResult result, string name)
    {
        string opening = GameProfile.Active.HasFunctionKeyword ? "function " : "";

        // A file that does not end in a newline would otherwise get the declaration welded onto its
        // last line.
        string separator = result.Text.Length > 0 && result.Text.Text[^1] != '\n' ? "\n\n" : "\n";
        Position end = result.Text.GetPosition(result.Text.Length);
        TextEdit edit = new()
        {
            Range = new TextRange(end, end).ToLsp(),
            NewText = separator + opening + name + "()\n{\n}\n",
        };

        return new CodeAction
        {
            Title = "Create function '" + name + "'",
            Kind = CodeActionKind.QuickFix,
            Edit = SingleEdit(uri, edit),
        };
    }

    /// <summary>
    /// Imports the file that declares the function and points the call at it. Up to two edits, as
    /// one operation: the `#using`, when it is not already there, and the qualifier — inserted when
    /// the call was written bare, replaced when it named a namespace that does not have the name.
    /// </summary>
    private static CodeAction BuildImportAndQualifyAction(
        DocumentUri uri,
        string namespaceName,
        string usingPath,
        bool alreadyImported,
        Position insertAt,
        TextRange nameRange,
        TextRange? qualifier)
    {
        List<TextEdit> edits = [];
        if ( !alreadyImported )
        {
            edits.Add(new TextEdit
            {
                Range = new TextRange(insertAt, insertAt).ToLsp(),
                NewText = "#using " + usingPath + ";\n",
            });
        }

        TextRange qualifierRange = qualifier ?? new TextRange(nameRange.Start, nameRange.Start);
        edits.Add(new TextEdit { Range = qualifierRange.ToLsp(), NewText = namespaceName + "::" });

        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new() { [uri] = edits };

        string title = alreadyImported
            ? "Qualify with '" + namespaceName + "::'"
            : "Add #using " + usingPath + " and qualify with '" + namespaceName + "::'";

        return new CodeAction
        {
            Title = title,
            Kind = CodeActionKind.QuickFix,
            Edit = new WorkspaceEdit { Changes = changes },
        };
    }

    /// <summary>
    /// The `ns::` written immediately before a call's name, as a range covering the namespace and
    /// the colons, or null when the call was written bare. Whitespace between the parts is allowed
    /// for, since nothing forbids `util :: foo()` even though nobody writes it.
    /// </summary>
    private static TextRange? QualifierRange(ParseResult result, TextRange nameRange)
    {
        string text = result.Text.Text;
        int index = result.Text.GetOffset(nameRange.Start);

        int afterColons = SkipWhitespaceBack(text, index);
        if ( afterColons < 2 || text[afterColons - 1] != ':' || text[afterColons - 2] != ':' )
        {
            return null;
        }

        int end = afterColons;
        int cursor = SkipWhitespaceBack(text, afterColons - 2);
        int start = cursor;
        while ( start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_') )
        {
            start--;
        }

        if ( start == cursor )
        {
            // Colons with no name in front of them; not a qualifier this can rewrite.
            return null;
        }

        return new TextRange(result.Text.GetPosition(start), result.Text.GetPosition(end));
    }

    private static int SkipWhitespaceBack(string text, int index)
    {
        while ( index > 0 && char.IsWhiteSpace(text[index - 1]) )
        {
            index--;
        }

        return index;
    }

    /// <summary>The source covered by a range, trimmed.</summary>
    private static string TextAt(ParseResult result, TextRange range)
    {
        int start = result.Text.GetOffset(range.Start);
        int end = result.Text.GetOffset(range.End);
        if ( start >= end || end > result.Text.Length )
        {
            return "";
        }

        return result.Text.Text[start..end].Trim();
    }

    /// <summary>
    /// Whether the text is a bare identifier. Guards the create-function fix against ever writing a
    /// declaration out of something that is not a name — a stale diagnostic range against an edited
    /// buffer is the way that happens.
    /// </summary>
    private static bool IsIdentifier(string text)
    {
        if ( char.IsDigit(text[0]) )
        {
            return false;
        }

        foreach ( char character in text )
        {
            if ( !char.IsLetterOrDigit(character) && character != '_' )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The #using directives whose path was already imported earlier in the file AND whose
    /// line overlaps the selection — i.e. the redundant ones offered for removal.
    /// </summary>
    internal static List<UsingNode> FindRemovableDuplicates(ParseResult result, TextRange selection)
    {
        List<UsingNode> duplicates = [];
        HashSet<string> seenPaths = new(StringComparer.Ordinal);

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is not UsingNode usingNode )
            {
                continue;
            }

            string normalized = NormalizePath(usingNode.Path);

            // Only the second-and-later occurrences of a path are redundant.
            if ( seenPaths.Add(normalized) )
            {
                continue;
            }

            if ( Overlaps(usingNode.Range, selection) )
            {
                duplicates.Add(usingNode);
            }
        }

        return duplicates;
    }

    /// <summary>
    /// Distinct #using paths that would make a qualified call in the selection resolvable but
    /// aren't imported yet: for each qualified call, the script-relative path of a visible file
    /// defining that function, minus the extension. Own-namespace calls and already-imported
    /// files are skipped.
    /// </summary>
    internal static List<string> FindMissingUsings(
        ParseResult result, LanguageStore store, string contextId, string askingPath, TextRange selection)
    {
        // The DECLARED set rather than the namespace spans: the spans include a leading region named
        // after the file whenever its imports sit above its #namespace line, and a phantom in here
        // read as "already own that namespace" and silently withheld the add-#using fix.
        HashSet<string> ownNamespaces = new(StringComparer.Ordinal);
        foreach ( string declared in result.Extraction.DeclaredNamespaces )
        {
            ownNamespaces.Add(declared);
        }

        HashSet<string> existingUsings = new(StringComparer.Ordinal);
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is UsingNode usingNode )
            {
                existingUsings.Add(StripExtension(NormalizePath(usingNode.Path)));
            }
        }

        List<string> missing = [];
        HashSet<string> offered = new(StringComparer.Ordinal);

        foreach ( GSCode.Core.Symbols.ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != GSCode.Core.Symbols.ReferenceKind.Call
                || entry.Key.Kind != GSCode.Core.Symbols.SymbolKind.Function )
            {
                continue;
            }

            string? namespaceName = entry.Key.Namespace;
            if ( namespaceName is null || ownNamespaces.Contains(namespaceName) )
            {
                continue;
            }

            if ( !Overlaps(entry.Range, selection) )
            {
                continue;
            }

            ImmutableArray<ResolvedFunction> defined = DatabaseQueries.LookupFunctions(
                store, contextId, askingPath, namespaceName, entry.Key.Name);
            foreach ( ResolvedFunction resolved in defined )
            {
                if ( resolved.Record.RelativePath.Length == 0 )
                {
                    continue;
                }

                string usingPath = StripExtension(NormalizePath(resolved.Record.RelativePath));
                if ( existingUsings.Contains(usingPath) || !offered.Add(usingPath) )
                {
                    continue;
                }

                missing.Add(usingPath);
            }
        }

        return missing;
    }

    /// <summary>Where a new #using should be inserted: after the last one, else the file top.</summary>
    /// <summary>
    /// Where a new #using belongs: just after the last one. <paramref name="beforeLine"/> caps
    /// which directives count, so moving a misplaced #using does not target a point below
    /// itself — the directive being moved is the very thing that must not anchor the insertion.
    /// </summary>
    private static Position UsingInsertionPoint(ParseResult result, int beforeLine = int.MaxValue)
    {
        int line = 0;
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is UsingNode usingNode && usingNode.Range.Start.Line < beforeLine )
            {
                line = usingNode.Range.Start.Line + 1;
            }
        }

        return new Position(line, 0);
    }

    private static CodeAction BuildRemoveAction(DocumentUri uri, UsingNode usingNode)
    {
        // Delete the whole line the directive sits on, including its trailing newline.
        int line = usingNode.Range.Start.Line;
        TextRange lineRange = new(new Position(line, 0), new Position(line + 1, 0));
        TextEdit edit = new() { Range = lineRange.ToLsp(), NewText = "" };

        return new CodeAction
        {
            Title = "Remove duplicate #using " + usingNode.Path,
            Kind = CodeActionKind.QuickFix,
            Edit = SingleEdit(uri, edit),
        };
    }

    private static CodeAction BuildAddUsingAction(DocumentUri uri, string usingPath, Position insertAt)
    {
        TextRange insertRange = new(insertAt, insertAt);
        TextEdit edit = new() { Range = insertRange.ToLsp(), NewText = "#using " + usingPath + ";\n" };

        return new CodeAction
        {
            Title = "Add #using " + usingPath,
            Kind = CodeActionKind.QuickFix,
            Edit = SingleEdit(uri, edit),
        };
    }

    private static WorkspaceEdit SingleEdit(DocumentUri uri, TextEdit edit)
    {
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes = new()
        {
            [uri] = new[] { edit },
        };

        return new WorkspaceEdit { Changes = changes };
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\').ToLowerInvariant();
    }

    private static string StripExtension(string path)
    {
        // Scripts are reached by #using, which names them without extension. Strip the server
        // or client extension; headers keep theirs (#insert names them in full).
        foreach ( string extension in new[] { GameProfile.Active.ServerScriptExtension, GameProfile.Active.ClientScriptExtension } )
        {
            if ( path.EndsWith(extension, StringComparison.Ordinal) )
            {
                return path[..^extension.Length];
            }
        }

        return path;
    }

    private static bool Overlaps(TextRange node, TextRange selection)
    {
        return node.Start <= selection.End && selection.Start <= node.End;
    }
}
