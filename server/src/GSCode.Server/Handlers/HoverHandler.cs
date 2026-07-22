using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Typing;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;
using TextRange = GSCode.Core.Text.TextRange;

namespace GSCode.Server.Handlers;

/// <summary>Rich markdown hover for functions (script + builtin), classes, macros, fields, and literals.</summary>
public sealed class HoverHandler : HoverHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;
    private readonly TextDocumentSelector _selector;

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
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<Hover?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());
        if ( hit.Kind == HitKind.Reference )
        {
            string? markdown = RenderHover(target, hit.Key, hit.Range);
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
        if ( TryKeywordDocHover(target.Result, request.Position.ToCore(), out string keywordMarkdown, out TextRange keywordRange) )
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
            string markdown = $"```gsc\n(local) {local.Name}: {local.Type.DisplayName()}\n```";
            return Task.FromResult<Hover?>(new Hover
            {
                Range = local.Range.ToLsp(),
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown }),
            });
        }

        return Task.FromResult<Hover?>(null);
    }

    private string? RenderHover(NavigationTarget target, SymbolKey key, TextRange hitRange)
    {
        switch ( key.Kind )
        {
            case SymbolKind.Function:
            {
                ImmutableArray<ResolvedFunction> functions = DatabaseQueries.LookupFunctions(
                    target.Store, target.ContextId, target.Path, key.Namespace, key.Name, askingNamespaces: target.Namespaces);
                if ( functions.Length > 0 )
                {
                    return MarkdownDocRenderer.RenderFunction(functions[0].Function);
                }

                // Fall back to the namespace-less builtin library.
                BuiltinFunction? builtin = _builtins.For(target.Language).Find(key.Name);
                return builtin is not null ? MarkdownDocRenderer.RenderBuiltin(builtin) : null;
            }
            case SymbolKind.Class:
            {
                ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(
                    target.Store, target.ContextId, key.Namespace, key.Name);
                return classes.Length > 0 ? MarkdownDocRenderer.RenderClass(classes[0].Class) : null;
            }
            case SymbolKind.Macro:
            {
                MacroRecord? macro = FindMacro(target, key.Name);
                return macro is not null
                    ? MarkdownDocRenderer.RenderMacro(macro, FindMacroExpansion(target, key.Name, hitRange))
                    : null;
            }
            case SymbolKind.Field:
                return RenderField(key.Name, target.Language);
            case SymbolKind.StringLiteral:
            case SymbolKind.HashString:
            case SymbolKind.LocalizedString:
            case SymbolKind.AnimReference:
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The macro's body rendered for preview, with THIS call site's arguments substituted where
    /// the hover is on an invocation — `IS_TRUE( foo )` reads `isdefined( foo ) && foo` rather
    /// than showing the macro's own parameter names back to the reader.
    ///
    /// Hovering the DEFINITION has no arguments to substitute, so it keeps the parameter names,
    /// which is what a definition should show.
    /// </summary>
    private static string FindMacroExpansion(NavigationTarget target, string name, TextRange hitRange)
    {
        foreach ( GSCode.Parser.Preprocessing.MacroDefinition definition in target.Result.Preprocessed.Macros.All )
        {
            if ( !string.Equals(definition.Name, name, StringComparison.Ordinal) )
            {
                continue;
            }

            return MacroExpansionPreview.Render(
                definition.Body,
                definition.Parameters ?? [],
                ArgumentsAt(target, hitRange));
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
        foreach ( GSCode.Parser.Preprocessing.MacroInvocation invocation in target.Result.Preprocessed.MacroInvocations )
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

    private MacroRecord? FindMacro(NavigationTarget target, string name)
    {
        // The document's own macros, then any GSH it consults.
        foreach ( GSCode.Parser.Preprocessing.MacroDefinition definition in target.Result.Preprocessed.Macros.All )
        {
            if ( string.Equals(definition.Name, name, StringComparison.Ordinal) )
            {
                return new MacroRecord(
                    definition.Name,
                    definition.IsFunctionLike,
                    definition.Parameters ?? [],
                    definition.NameRange,
                    definition.Documentation ?? "");
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a keyword/directive doc when the cursor is on a documented keyword or directive
    /// token (isdefined, notify, #using, …). Returns false for undocumented tokens and non-keywords.
    /// </summary>
    private static bool TryKeywordDocHover(ParseResult result, Position position, out string markdown, out TextRange range)
    {
        markdown = "";
        range = TextRange.Empty;

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

            string? doc = KeywordDocs.Find(token.GetText(result.Text).ToString());
            if ( doc is null )
            {
                return false;
            }

            markdown = doc;
            range = token.Range;
            return true;
        }

        return false;
    }

    private static bool IsDirective(TokenKind kind)
    {
        return kind >= TokenKind.UsingDirective && kind <= TokenKind.EndifDirective;
    }

    private string RenderField(string name, ScriptLanguage language)
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
            return $"```gsc\n(field) {name}\n```";
        }

        System.Text.StringBuilder markdown = new();
        markdown.Append("```gsc\n(field) ").Append(name).Append("\n```\n");

        // The owner's entity kind isn't inferred here, so list every kind declaring the name.
        if ( known.Length > 0 )
        {
            markdown.Append("\n---\n\nEngine field:\n");
            foreach ( ObjectField field in known )
            {
                markdown.Append("* `").Append(field.EntityKind).Append("`: ").Append(field.Type);
                // No field carries this today — the flags had no source and were removed — but
                // the marker stays, so data that can be sourced needs no code change.
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
