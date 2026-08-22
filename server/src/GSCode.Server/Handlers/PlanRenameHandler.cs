using System.Collections.Immutable;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Server.Configuration;
using MediatR;
using OmniSharp.Extensions.JsonRpc;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>Request for gscode/planRename: where a script is moving.</summary>
[Method("gscode/planRename", Direction.ClientToServer)]
public sealed class PlanRenameParams : IRequest<PlanRenameResponse>
{
    public string OldPath { get; set; } = "";
    public string NewPath { get; set; } = "";
}

/// <summary>One directive path to rewrite, in client-friendly coordinates.</summary>
public sealed class PlanRenameEdit
{
    public string Path { get; set; } = "";
    public int StartLine { get; set; }
    public int StartCharacter { get; set; }
    public int EndLine { get; set; }
    public int EndCharacter { get; set; }
    public string NewText { get; set; } = "";
}

/// <summary>Response for gscode/planRename.</summary>
public sealed class PlanRenameResponse
{
    public PlanRenameEdit[] Edits { get; set; } = [];
}

/// <summary>
/// Plans the <c>#using</c>/<c>#insert</c> path edits a script rename implies, so renaming a
/// file does not silently break every importer.
///
/// This is a custom request rather than the standard <c>willRenameFiles</c> handler because
/// OmniSharp 0.19.9 models `FileRename` with a single `Uri` property — the spec's
/// `oldUri`/`newUri` pair is absent, so a server-side handler cannot learn the destination.
/// The client has the correct data from `workspace.onWillRenameFiles` and calls this, keeping
/// all the path reasoning here where the database lives.
/// </summary>
public sealed class PlanRenameHandler : IJsonRpcRequestHandler<PlanRenameParams, PlanRenameResponse>
{
    private readonly ScriptDatabase _database;
    private readonly ResolverHolder _resolver;

    public PlanRenameHandler(ScriptDatabase database, ResolverHolder resolver)
    {
        _database = database;
        _resolver = resolver;
    }

    public Task<PlanRenameResponse> Handle(PlanRenameParams request, CancellationToken cancellationToken)
    {
        ImmutableArray<DependencyEdit> edits = Plan(request.OldPath, request.NewPath);
        if ( edits.Length == 0 )
        {
            return Task.FromResult(new PlanRenameResponse());
        }

        List<PlanRenameEdit> mapped = new(edits.Length);
        foreach ( DependencyEdit edit in edits )
        {
            mapped.Add(new PlanRenameEdit
            {
                Path = edit.FilePath,
                StartLine = edit.Range.Start.Line,
                StartCharacter = edit.Range.Start.Character,
                EndLine = edit.Range.End.Line,
                EndCharacter = edit.Range.End.Character,
                NewText = edit.NewText,
            });
        }

        Log.Information("Rename plan: {Count} directive path(s) to update", mapped.Count);
        return Task.FromResult(new PlanRenameResponse { Edits = [.. mapped] });
    }

    /// <summary>
    /// The edits one rename implies. Yields nothing when the file is unknown to the database,
    /// or when either location sits outside every root — a path that is not script-relative
    /// cannot be named by a directive at all.
    /// </summary>
    private ImmutableArray<DependencyEdit> Plan(string oldPathText, string newPathText)
    {
        if ( oldPathText.Length == 0 || newPathText.Length == 0 )
        {
            return [];
        }

        string oldPath = PathUtil.NormalizeAbsolute(oldPathText);
        string newPath = PathUtil.NormalizeAbsolute(newPathText);

        if ( !TryFindRecord(oldPath, out ScriptRecord record) || record.RelativePath.Length == 0 )
        {
            return [];
        }

        PathResolver resolver = _resolver.Current;
        string newRelative = resolver.GetScriptRelativePath(newPath, resolver.GetContext(newPath));
        if ( newRelative.Length == 0 )
        {
            return [];
        }

        // A .gsh is reached by #insert (extension kept); scripts by #using (extension dropped).
        bool isInsert = record.Language == ScriptLanguage.Gsh;

        return DependencyRewrite.PlanRename(
            _database,
            DependencyRewrite.ToDirectivePath(record.RelativePath, isInsert),
            DependencyRewrite.ToDirectivePath(newRelative, isInsert),
            isInsert);
    }

    private bool TryFindRecord(string normalizedPath, out ScriptRecord record)
    {
        if ( _database.Gsc.TryGet(normalizedPath, out record) )
        {
            return true;
        }

        if ( _database.Csc.TryGet(normalizedPath, out record) )
        {
            return true;
        }

        return _database.TryGetGsh(normalizedPath, out record);
    }
}
