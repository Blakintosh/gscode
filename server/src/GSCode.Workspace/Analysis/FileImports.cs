using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>One import directive, resolved to the record it names.</summary>
public sealed record ImportedFile(string RawPath, TextRange DirectiveRange, ScriptRecord Record);

/// <summary>
/// A file's import directives, each resolved to a record, done ONCE.
///
/// Four lints wanted exactly this and each wrote it out: walk the directives, resolve every path
/// through the <see cref="PathResolver"/>, normalize, <c>store.TryGet</c>, and abandon the whole pass
/// the moment either step fails. On a BO3 file that meant the same <c>#using</c> list resolved three
/// times per analysis — <see cref="NamespaceUsageLint"/>, <see cref="UnusedUsingLint"/> and
/// <see cref="AmbiguousFunctionLint"/> — and each resolve is a filesystem probe per configured root.
/// This runs on every keystroke.
///
/// <see cref="Complete"/> carries the shared bail-out. It is false when any directive did not resolve
/// or was not indexed, and every one of those lints stands down on it for the same reason: a file
/// they cannot read might supply the namespace they were about to complain about, or the second
/// declaration that makes a name ambiguous, or the function that makes an import used after all.
///
/// Both directive kinds are gathered in one pass and kept apart, because no dialect has both and a
/// single list would let a <c>#using</c> be judged by the rule for <c>#include</c>. That distinction
/// is not decorative: on BO3 the include list is always empty, and a lint reading "the imports"
/// without asking which kind would have found every <c>#using</c> waiting for it.
/// </summary>
public sealed class FileImports
{
    private FileImports(ImmutableArray<ImportedFile> usings, ImmutableArray<ImportedFile> includes, bool complete)
    {
        Usings = usings;
        Includes = includes;
        Complete = complete;
    }

    public ImmutableArray<ImportedFile> Usings { get; }
    public ImmutableArray<ImportedFile> Includes { get; }

    /// <summary>Whether every directive resolved AND was indexed — see the type's own remarks.</summary>
    public bool Complete { get; }

    /// <summary>The records of both kinds, for a caller that does not care which directive named them.</summary>
    public IEnumerable<ScriptRecord> Records
    {
        get { return Usings.Concat(Includes).Select(static imported => imported.Record); }
    }

    public static FileImports Resolve(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath,
        GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        string extension = game.ExtensionFor(language);
        ResolutionContext context = resolver.GetContext(askingPath);

        ImmutableArray<ImportedFile>.Builder usings = ImmutableArray.CreateBuilder<ImportedFile>();
        ImmutableArray<ImportedFile>.Builder includes = ImmutableArray.CreateBuilder<ImportedFile>();
        bool complete = true;

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            string path;
            ImmutableArray<ImportedFile>.Builder target;

            switch ( element )
            {
                case UsingNode usingNode:
                    path = usingNode.Path;
                    target = usings;
                    break;
                case IncludeNode includeNode:
                    path = includeNode.Path;
                    target = includes;
                    break;
                default:
                    continue;
            }

            string? resolved = resolver.Resolve(context, path + extension);
            if ( resolved is null )
            {
                complete = false;
                continue;
            }

            if ( !store.TryGet(PathUtil.NormalizeAbsolute(resolved), out ScriptRecord record) )
            {
                complete = false;
                continue;
            }

            target.Add(new ImportedFile(path, element.Range, record));
        }

        return new FileImports(usings.ToImmutable(), includes.ToImmutable(), complete);
    }
}
