using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Typing;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;
using TextRange = GSCode.Core.Text.TextRange;
using GSCode.Parser.Preprocessing;

namespace GSCode.Server.Handlers;

/// <summary>Rich markdown hover for functions (script + builtin), classes, macros, fields, and literals.</summary>
public sealed class HoverHandler : HoverHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;
    private readonly TextDocumentSelector _selector;

    /// <summary>
    /// One assignment-inference walk per document VERSION, not per hover.
    ///
    /// <see cref="InferredFieldType"/> asks <see cref="FlowTyper.InferAssignments(ParseResult)"/>,
    /// which walks every function in the file — and unlike <see cref="FlowTyper.InferValues"/> it
    /// carries NO memoisation of its own, so building a fresh typer per request (which this did)
    /// re-walked the whole file for every hover over a field. Hovering is a mouse-move away.
    ///
    /// Keyed by <see cref="ParseResult"/> reference, the same identity
    /// <see cref="InlayHintHandler"/>'s own cache uses and for the same reason: an unchanged
    /// document hands back the SAME instance, and a ConditionalWeakTable entry is collectible the
    /// moment nothing else holds its ParseResult — when the document closes or is next edited.
    ///
    /// Only the RESULT is shared, never the typer. A <see cref="FlowTyper"/> carries a cursor and a
    /// recording table as instance state, so two concurrent requests holding one would interfere;
    /// an <c>ImmutableArray</c> cannot.
    /// </summary>
    private readonly ConditionalWeakTable<ParseResult, InferredAssignments> _assignmentCache = new();

    /// <summary>
    /// A box for the array, because <see cref="ConditionalWeakTable{TKey, TValue}"/> takes a
    /// reference type and an <c>ImmutableArray</c> is a struct. Holding the builder instead would
    /// have meant copying the array back out on every read, which is the cost being removed.
    /// </summary>
    private sealed class InferredAssignments
    {
        public InferredAssignments(ImmutableArray<InferredAssignment> assignments)
        {
            Assignments = assignments;
        }

        public ImmutableArray<InferredAssignment> Assignments { get; }
    }

    public HoverHandler(NavigationSupport support, BuiltinApiSet builtins, ObjectFields objectFields, TextDocumentSelector selector)
    {
        _support = support;
        _builtins = builtins;
        _objectFields = objectFields;
        _selector = selector;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri, cancellationToken);
        if ( target is null )
        {
            return Task.FromResult<Hover?>(null);
        }

        PositionHit hit = _support.ResolveHit(target, request.Position.ToCore());
        if ( hit.Kind == HitKind.Reference )
        {
            string? markdown = RenderHover(target, hit.Key, hit.Range, hit.ReferenceKind);
            if ( markdown is null )
            {
                return Task.FromResult<Hover?>(null);
            }

            return Task.FromResult<Hover?>(new Hover
            {
                Range = hit.Range.ToLsp(),
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown }),
            });
        }

        // A documented keyword or directive (isdefined, notify, #using, …).
        if ( TryKeywordDocHover(target, request.Position.ToCore(), out string keywordMarkdown, out TextRange keywordRange) )
        {
            return Task.FromResult<Hover?>(new Hover
            {
                Range = keywordRange.ToLsp(),
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = keywordMarkdown }),
            });
        }

        // Not a classified reference: fall back to an inferred-type hover on a local variable.
        FlowTyper typer = new(_builtins.For(target.Language), _objectFields);
        if ( typer.TryGetLocalTypeAt(target.Result, request.Position.ToCore(), out LocalTypeHover local) )
        {
            string markdown = $"```gsc\n(local) {local.Name}: {local.Display}\n```";
            return Task.FromResult<Hover?>(new Hover
            {
                Range = local.Range.ToLsp(),
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown }),
            });
        }

        return Task.FromResult<Hover?>(null);
    }

    /// <summary>
    /// A markdown link to a declaration, for the line under a hover's signature. Functions, classes
    /// and macros all get one; builtins, keywords and fields never reach it — the first two because
    /// the engine declares them and there is nothing to open, a field because its "definition" is
    /// every write that agrees rather than one place.
    ///
    /// <paramref name="path"/> is the file <paramref name="range"/> is truly a position in, which
    /// for a symbol that arrived through an <c>#insert</c> is the HEADER and not the including
    /// file — <see cref="ResolvedFunction.DeclaringPath"/> and
    /// <see cref="ResolvedClass.DeclaringPath"/> answer that for a symbol, and a macro definition's
    /// <c>SourceFile</c> does for a macro. Pairing a header-true range with the including file's
    /// path points at whatever text happens to sit at that line and column over there.
    ///
    /// The target is spelled as a <c>#L&lt;line&gt;,&lt;column&gt;</c> fragment on the file URI,
    /// which is how an editor is told to put the caret somewhere rather than merely open the file;
    /// both halves are 1-based there while ours are 0-based. The label is script-relative so it
    /// reads the way the scripts themselves name files, and is fenced as code so a path's
    /// backslashes and underscores are not eaten as markdown escapes and emphasis.
    /// </summary>
    private string DefinitionLink(string path, TextRange range)
    {
        Position start = range.Start;

        ResolutionContext context = _support.Resolver.GetContext(path);
        string relative = _support.Resolver.GetScriptRelativePath(path, context);

        // "" means the file is under no known root — an untitled or out-of-tree script. Its own
        // name is still more use to the reader than an absolute path the widget would wrap.
        string label = relative.Length > 0 ? relative : System.IO.Path.GetFileName(path);

        string uri = DocumentUri.FromFileSystemPath(path).ToString();
        return $"[`{label}:{start.Line + 1}`]({uri}#L{start.Line + 1},{start.Character + 1})";
    }

    private string? RenderHover(
        NavigationTarget target, SymbolKey key, TextRange hitRange, ReferenceKind referenceKind)
    {
        switch ( key.Kind )
        {
            case SymbolKind.Function:
            {
                // Routed so a class method resolves too — through its own class, an ancestor, or by
                // name when the receiver's class is unknown. LookupFunctions alone never sees one.
                ImmutableArray<ResolvedFunction> functions = MethodResolution.ResolveCall(
                    target.Store, target.ContextId, target.Path, key, referenceKind,
                    askingNamespaces: target.Namespaces);

                if ( functions.Length > 0 )
                {
                    return MarkdownDocRenderer.RenderFunction(
                        functions[0].Function,
                        functions[0].OwnerClass,
                        DefinitionLink(functions[0].DeclaringPath, functions[0].Function.NameRange));
                }

                // Fall back to the namespace-less builtin library.
                BuiltinFunction? builtin = _builtins.For(target.Language).Find(key.Name);
                return builtin is not null ? MarkdownDocRenderer.RenderBuiltin(builtin) : null;
            }
            case SymbolKind.Class:
            {
                ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(
                    target.Store, target.ContextId, key.Namespace, key.Name);
                if ( classes.Length == 0 )
                {
                    return null;
                }

                return MarkdownDocRenderer.RenderClass(
                    classes[0].Class, DefinitionLink(classes[0].DeclaringPath, classes[0].Class.NameRange));
            }
            case SymbolKind.Macro:
            {
                MacroDefinition? macro = FindMacro(target, key.Name);
                if ( macro is null )
                {
                    return null;
                }

                // SourceFile is null for a macro this file defines itself; non-null names the .gsh
                // an #insert brought it in from, which is the answer the reader does not otherwise
                // have.
                string macroPath = macro.SourceFile ?? target.Path;
                return MarkdownDocRenderer.RenderMacro(
                    new MacroRecord(
                        macro.Name,
                        macro.IsFunctionLike,
                        macro.Parameters ?? [],
                        macro.NameRange,
                        macro.Documentation ?? ""),
                    FindMacroExpansion(target, key.Name, hitRange),
                    DefinitionLink(macroPath, macro.NameRange));
            }
            case SymbolKind.Field:
                return RenderField(key.Name, target.Language, target);
            case SymbolKind.Member:
                return RenderMember(target, key);
            case SymbolKind.StringLiteral:
                // The one shape a plain string literal reference is not: __FUNCTION__/__FILE__
                // already expanded to a string before parsing, so the reader looking at the literal
                // text on screen needs to be told what it resolved to — an ordinary string has
                // nothing to add beyond what is already on screen, so this is the only case here.
                return FindBuiltinExpansion(target, hitRange);
            case SymbolKind.HashString:
            case SymbolKind.LocalizedString:
            case SymbolKind.AnimReference:
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// What a <c>__FUNCTION__</c>/<c>__FILE__</c>/<c>__LINE__</c> use at <paramref name="hitRange"/>
    /// expanded to, or null when the hover is on an ordinary string. <c>BuiltinExpansions</c> is
    /// scoped to this same file's own preprocessing run, so a range match cannot land on another
    /// document's entry.
    /// </summary>
    private static string? FindBuiltinExpansion(NavigationTarget target, TextRange hitRange)
    {
        foreach ( BuiltinExpansion expansion in target.Result.Preprocessed.BuiltinExpansions )
        {
            if ( expansion.Range.Contains(hitRange.Start) )
            {
                return $"```gsc\n{expansion.Name}\n```\nExpands to: `{expansion.ExpandedText}`";
            }
        }

        return null;
    }

    /// <summary>
    /// The macro's body rendered for preview, with THIS call site's arguments substituted where
    /// the hover is on an invocation — `IS_TRUE( foo )` reads `isdefined( foo ) &amp;&amp; foo` rather
    /// than showing the macro's own parameter names back to the reader.
    ///
    /// Hovering the DEFINITION has no arguments to substitute, so it keeps the parameter names,
    /// which is what a definition should show.
    /// </summary>
    private static string FindMacroExpansion(NavigationTarget target, string name, TextRange hitRange)
    {
        foreach ( MacroDefinition definition in target.Result.Preprocessed.Macros.All )
        {
            if ( !string.Equals(definition.Name, name, StringComparison.Ordinal) )
            {
                continue;
            }

            return MacroExpansionPreview.Render(
                definition.Body,
                definition.Parameters ?? [],
                ArgumentsAt(target, hitRange),
                target.Result.Preprocessed.Macros);
        }

        return "";
    }

    /// <summary>
    /// The arguments written at the invocation covering <paramref name="hitRange"/>, or none when
    /// the hover is not on one. An invocation records where it is and what it calls but not what
    /// it was passed, so the text is read back out of the file.
    /// </summary>
    private static ImmutableArray<string> ArgumentsAt(NavigationTarget target, TextRange hitRange)
    {
        foreach ( MacroInvocation invocation in target.Result.Preprocessed.MacroInvocations )
        {
            // Only invocations written in THIS file: one reached through an #insert has its text
            // in another file that is not loaded here.
            if ( invocation.SourceFile is not null || !invocation.Range.Contains(hitRange.Start) )
            {
                continue;
            }

            // The range covers the NAME only — `IS_TRUE`, not `IS_TRUE( v )` — so the arguments
            // are read from the text that follows it.
            int afterName = target.Result.Text.GetOffset(invocation.Range.End);
            if ( afterName <= 0 || afterName > target.Result.Text.Length )
            {
                return [];
            }

            return MacroExpansionPreview.ArgumentsFollowing(target.Result.Text.Text, afterName);
        }

        return [];
    }

    /// <summary>
    /// The definition in effect for <paramref name="name"/> — the document's own macros, then any
    /// GSH it consults. The DEFINITION rather than a <see cref="MacroRecord"/> built from it,
    /// because the record drops <c>SourceFile</c>, and which file the <c>#define</c> is in is half
    /// of what the hover now reports.
    /// </summary>
    private static MacroDefinition? FindMacro(NavigationTarget target, string name)
    {
        foreach ( MacroDefinition definition in target.Result.Preprocessed.Macros.All )
        {
            if ( string.Equals(definition.Name, name, StringComparison.Ordinal) )
            {
                return definition;
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a doc when the cursor is on a keyword or directive token (isdefined, notify, #using,
    /// …). Returns false for non-keywords and for keywords nothing documents.
    ///
    /// Falls back to the BUILTIN LIBRARY when KeywordDocs has no entry, which is what makes
    /// <c>assert</c> and <c>assertmsg</c> hover at all. Those two are keywords AND engine functions:
    /// KeywordDocs deliberately omits them, saying they are "documented by the builtin API", while
    /// the only path that reads that API is the REFERENCE hover — and a reference is never recorded
    /// for them, because they lex as their own token kinds and
    /// <c>SymbolExtractor.RecordCalleeReference</c> matches identifier callees on
    /// <c>TokenKind.Identifier</c>. Each side assumed the other had it and neither did, so hovering
    /// assert produced nothing.
    /// </summary>
    private bool TryKeywordDocHover(NavigationTarget target, Position position, out string markdown, out TextRange range)
    {
        markdown = "";
        range = TextRange.Empty;

        ParseResult result = target.Result;
        int offset = result.Text.GetOffset(position);
        foreach ( Token token in result.Lexed.Tokens )
        {
            if ( offset < token.Start || offset >= token.End )
            {
                continue;
            }

            if ( !TokenFacts.IsKeyword(token.Kind) && !IsDirective(token.Kind) )
            {
                return false;
            }

            string name = CanonicalKeywordName(token, result);

            string? doc = KeywordDocs.Find(name);
            if ( doc is not null )
            {
                markdown = doc;
                range = token.Range;
                return true;
            }

            BuiltinFunction? builtin = _builtins.For(target.Language).Find(name);
            if ( builtin is not null )
            {
                markdown = MarkdownDocRenderer.RenderBuiltin(builtin);
                range = token.Range;
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// The name a keyword is documented under, which is not always how it was spelled.
    ///
    /// <c>prof_begin</c>/<c>prof_end</c> are the Infinity Ward-line spelling of
    /// <c>profilestart</c>/<c>profilestop</c> and lex to the same token kinds, but the lookup is by
    /// TEXT; resolving through the kind is what keeps one doc serving both spellings, including on
    /// CoD4, WaW, MW2 and BO1, where the Infinity Ward spelling is the one people write.
    /// </summary>
    private static string CanonicalKeywordName(Token token, ParseResult result)
    {
        switch ( token.Kind )
        {
            case TokenKind.ProfileStart:
                return "profilestart";
            case TokenKind.ProfileStop:
                return "profilestop";
            default:
                return token.GetText(result.Text).ToString();
        }
    }

    private static bool IsDirective(TokenKind kind)
    {
        return kind >= TokenKind.UsingDirective && kind <= TokenKind.EndifDirective;
    }

    /// <summary>
    /// This document's inferred assignments, walked once per version. See
    /// <see cref="_assignmentCache"/>.
    /// </summary>
    internal ImmutableArray<InferredAssignment> AssignmentsOf(NavigationTarget target)
    {
        if ( _assignmentCache.TryGetValue(target.Result, out InferredAssignments? cached) )
        {
            return cached.Assignments;
        }

        InferredAssignments inferred = new(
            new FlowTyper(_builtins.For(target.Language), _objectFields).InferAssignments(target.Result));

        // AddOrUpdate rather than Add: two hovers on the same unchanged document can race this
        // miss, and the walk is pure, so the race costs a duplicate computation rather than a wrong
        // answer. Add would throw on the loser instead.
        _assignmentCache.AddOrUpdate(target.Result, inferred);
        return inferred.Assignments;
    }

    /// <summary>
    /// A class <c>var</c>, named with the class that actually declares it — which for an inherited
    /// member is not the class the cursor is in, and is the fact a reader most needs here. The
    /// name on screen looks exactly like a local, so saying nothing at all is what it did before.
    /// </summary>
    private string RenderMember(NavigationTarget target, SymbolKey key)
    {
        string declaring = key.OwnerClass is null
            ? ""
            : MethodResolution.FindDeclaringClassForMember(
                target.Store, target.ContextId, key.OwnerClass, key.Name) ?? key.OwnerClass;

        StringBuilder markdown = new();
        markdown.Append("```gsc\n(member) ").Append(key.Name).Append("\n```\n");

        if ( declaring.Length > 0 )
        {
            foreach ( ResolvedClass resolved in DatabaseQueries.LookupClasses(
                target.Store, target.ContextId, namespaceName: null, declaring) )
            {
                markdown.Append("\n---\n\nmember of `").Append(resolved.Class.Name).Append('`');
                return markdown.ToString();
            }
        }

        return markdown.ToString();
    }

    /// <summary>
    /// The type this file's own writes give a field, or Unknown when they disagree or there are
    /// none. Every write has to agree: <c>self.state = "idle"</c> in one function and
    /// <c>self.state = 3</c> in another means the field genuinely holds both, and picking whichever
    /// came last would report a type that is wrong half the places it is read.
    /// </summary>
    private ScrType InferredFieldType(NavigationTarget target, string name, out string display)
    {
        ScrType agreed = ScrType.Unknown;
        display = "";
        bool seen = false;

        foreach ( InferredAssignment assignment in AssignmentsOf(target) )
        {
            if ( !assignment.IsField
                || !string.Equals(assignment.Name, name, StringComparison.OrdinalIgnoreCase) )
            {
                continue;
            }

            if ( !seen )
            {
                agreed = assignment.Type;
                display = assignment.Display;
                seen = true;
            }
            else if ( agreed != assignment.Type )
            {
                display = "";
                return ScrType.Unknown;
            }
            else if ( !string.Equals(display, assignment.Display, StringComparison.Ordinal) )
            {
                // The coarse types agree while the labels do not — two different classes, both
                // instances. The type is still knowable, so it is still reported; the finer label
                // is not, so it falls back rather than picking whichever was written first.
                display = agreed.DisplayName();
            }
        }

        return agreed;
    }

    private string RenderField(string name, ScriptLanguage language, NavigationTarget target)
    {
        // The .size pseudo-member has its own documentation.
        if ( string.Equals(name, "size", StringComparison.OrdinalIgnoreCase) )
        {
            string? sizeDoc = KeywordDocs.Find("size");
            if ( sizeDoc is not null )
            {
                return sizeDoc;
            }
        }

        // A name can be both an engine field and a radiant map key (origin, classname, …),
        // so both sections are appended rather than treated as alternatives.
        ImmutableArray<ObjectField> known = _objectFields.FindField(name);
        RadiantKey? radiant = _objectFields.FindRadiantKey(name, language);

        if ( known.Length == 0 && radiant is null )
        {
            // Nothing in the engine data, which is the ordinary case for a field the scripts
            // invented — and most of them are. What the file itself assigns is the only evidence
            // there is, so it is used, but only when every write agrees. That is the rule the
            // engine data already follows for a name several entity kinds declare: disagreement
            // means the answer is not knowable from here, and a guess is worse than a blank.
            ScrType inferred = InferredFieldType(target, name, out string display);
            return inferred.IsKnown()
                ? $"```gsc\n(field) {name}: {display}\n```"
                : $"```gsc\n(field) {name}\n```";
        }

        StringBuilder markdown = new();
        markdown.Append("```gsc\n(field) ").Append(name).Append("\n```\n");

        // The owner's entity kind isn't inferred here, so list every kind declaring the name.
        if ( known.Length > 0 )
        {
            markdown.Append("\n---\n\nEngine field:\n");
            foreach ( ObjectField field in known )
            {
                markdown.Append("* `").Append(field.EntityKind).Append("`: ").Append(field.Type);
                // Only weapon fields carry this, and every one of them does: Weapon Fields.txt
                // documents the whole set as read-only on the value GetWeapon() returns. The
                // flags on other kinds had no source and were removed.
                if ( field.ReadOnly )
                {
                    markdown.Append(" *(read-only)*");
                }

                markdown.Append('\n');
            }
        }

        if ( radiant is not null )
        {
            markdown.Append("\n---\n\nRadiant map key: `").Append(radiant.Type).Append("`\n");
            if ( radiant.Comment.Length > 0 )
            {
                markdown.Append('\n').Append(radiant.Comment).Append('\n');
            }
        }

        return markdown.ToString();
    }
}
