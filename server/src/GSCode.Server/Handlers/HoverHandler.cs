using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Typing;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

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
            string? markdown = RenderHover(target, hit.Key);
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

    private string? RenderHover(NavigationTarget target, SymbolKey key)
    {
        switch ( key.Kind )
        {
            case SymbolKind.Function:
            {
                ImmutableArray<ResolvedFunction> functions = DatabaseQueries.LookupFunctions(
                    target.Store, target.ContextId, target.Path, key.Namespace, key.Name);
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
                return classes.Length > 0 ? RenderClass(classes[0].Class) : null;
            }
            case SymbolKind.Macro:
            {
                MacroRecord? macro = FindMacro(target, key.Name);
                return macro is not null ? MarkdownDocRenderer.RenderMacro(macro) : null;
            }
            case SymbolKind.Field:
                return RenderField(key.Name);
            case SymbolKind.StringLiteral:
            case SymbolKind.HashString:
            case SymbolKind.LocalizedString:
            case SymbolKind.AnimReference:
                return null;
            default:
                return null;
        }
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

    private string RenderField(string name)
    {
        // Enrich with known engine field types (the owner's entity kind isn't inferred
        // until FlowTyper, so list every kind that declares this field name).
        ImmutableArray<ObjectField> known = _objectFields.FindField(name);
        if ( known.Length == 0 )
        {
            return $"```gsc\n(field) {name}\n```";
        }

        System.Text.StringBuilder markdown = new();
        markdown.Append("```gsc\n(field) ").Append(name).Append("\n```\n\n---\n\nEngine field:\n");
        foreach ( ObjectField field in known )
        {
            markdown.Append("* `").Append(field.EntityKind).Append("`: ").Append(field.Type);
            if ( field.ReadOnly )
            {
                markdown.Append(" *(read-only)*");
            }

            markdown.Append('\n');
        }

        return markdown.ToString();
    }

    private static string RenderClass(ClassSymbol classSymbol)
    {
        string header = classSymbol.ParentKeyName is null
            ? $"class {classSymbol.Name}"
            : $"class {classSymbol.Name} : {classSymbol.ParentKeyName}";

        return $"```gsc\n{header}\n```";
    }
}
