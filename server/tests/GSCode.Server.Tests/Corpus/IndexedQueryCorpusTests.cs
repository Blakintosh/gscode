using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Queries that stopped walking the store and read an index instead must return exactly what the
/// walk did. Each keeps its old walk here as the reference and is asked, against a real bo3 and cod4
/// index, every question of its kind the index can be asked — every header, every file, every class.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class IndexedQueryCorpusTests
{
    private readonly ITestOutputHelper _output;

    public IndexedQueryCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class Tally
    {
        public int Asked { get; set; }
        public int Answers { get; set; }
        public List<string> Differing { get; } = [];
    }

    [Fact]
    public async Task EveryIndexedQuery_AnswersAsTheWalkDid()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await CheckAsync(GameProfile.BlackOps3, CorpusFixture.Resolver);
            measured = true;
        }

        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is not null )
        {
            GameCorpus captured = cod4;
            await CheckAsync(captured.Profile, () => GameCorpusFixture.Resolver(captured));
            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: neither %GSCODE_CORPUS_BO3% nor %GSCODE_CORPUS_COD4% found.");
        }
    }

    private async Task CheckAsync(GameProfile profile, Func<PathResolver> resolverFactory)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            PathResolver resolver = resolverFactory();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), new NameTable());
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            Report(profile, "header inserters", CheckHeaderInserters(database));
            Report(profile, "rename plans", CheckRenamePlans(database));
            Report(profile, "path children", CheckPathChildren(database));
            Report(profile, "class lookups", CheckClassLookups(database));
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    private void Report(GameProfile profile, string query, Tally tally)
    {
        _output.WriteLine($"{profile.ShortName}: {query} — {tally.Asked:N0} asked, {tally.Answers:N0} answers, {tally.Differing.Count} differing");
        foreach ( string differing in tally.Differing.Take(10) )
        {
            _output.WriteLine($"  DIFFERS: {differing}");
        }

        Assert.Empty(tally.Differing);
    }

    private static Tally CheckHeaderInserters(ScriptDatabase database)
    {
        Tally tally = new();
        foreach ( ScriptRecord header in database.AllGshRecords.ToList() )
        {
            string relative = PathUtil.NormalizeScriptPath(header.RelativePath);
            foreach ( string asked in new[] { relative, "" } )
            {
                List<string> expected = WalkScriptsInserting(database, header.Path, asked);
                List<string> actual = [];
                foreach ( ScriptRecord record in DatabaseQueries.ScriptsInserting(database, header.Path, asked) )
                {
                    actual.Add(record.Path);
                }

                actual.Sort(StringComparer.Ordinal);
                tally.Asked++;
                tally.Answers += actual.Count;
                if ( !expected.SequenceEqual(actual) )
                {
                    tally.Differing.Add($"{header.Path} ('{asked}'): expected {expected.Count}, got {actual.Count}");
                }
            }
        }

        return tally;
    }

    private static Tally CheckRenamePlans(ScriptDatabase database)
    {
        Tally tally = new();
        foreach ( ScriptRecord record in database.AllRecords.ToList() )
        {
            if ( record.RelativePath.Length == 0 )
            {
                continue;
            }

            bool isInsert = record.Language == ScriptLanguage.Gsh;
            string from = DependencyRewrite.ToDirectivePath(record.RelativePath, isInsert);
            string to = DependencyRewrite.ToDirectivePath("renamed\\" + record.RelativePath, isInsert);

            List<string> expected = WalkPlanRename(database, from, to, isInsert);
            List<string> actual = [];
            foreach ( DependencyEdit edit in DependencyRewrite.PlanRename(database, from, to, isInsert) )
            {
                actual.Add($"{edit.FilePath}@{edit.Range}");
            }

            actual.Sort(StringComparer.Ordinal);
            tally.Asked++;
            tally.Answers += actual.Count;
            if ( !expected.SequenceEqual(actual) )
            {
                tally.Differing.Add($"{from}: expected {expected.Count}, got {actual.Count}");
            }
        }

        return tally;
    }

    /// <summary>
    /// Every folder any indexed file sits under, listed for every context that holds a file, from
    /// each language store (as <c>#using</c> sees it) and from the header store (as <c>#insert</c> does).
    /// </summary>
    private static Tally CheckPathChildren(ScriptDatabase database)
    {
        Tally tally = new();
        CheckPathChildren(tally, database.Gsc.AllRecords.ToList(), keepExtension: false,
            (directory, context) => database.Gsc.PathChildren(directory, context));
        CheckPathChildren(tally, database.Csc.AllRecords.ToList(), keepExtension: false,
            (directory, context) => database.Csc.PathChildren(directory, context));
        CheckPathChildren(tally, database.AllGshRecords.ToList(), keepExtension: true,
            (directory, context) => database.GshPathChildren(directory, context));
        return tally;
    }

    private static void CheckPathChildren(
        Tally tally, List<ScriptRecord> records, bool keepExtension,
        Func<string, string, List<(string Segment, bool IsFolder)>> indexed)
    {
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase) { "" };
        HashSet<string> contexts = new(StringComparer.Ordinal);
        foreach ( ScriptRecord record in records )
        {
            contexts.Add(record.ContextId);
            string path = DirectiveForm(record, keepExtension);
            for ( int index = 0; index < path.Length; index++ )
            {
                if ( path[index] == '\\' )
                {
                    directories.Add(path[..(index + 1)]);
                }
            }
        }

        foreach ( string directory in directories )
        {
            foreach ( string context in contexts )
            {
                SortedDictionary<string, bool> expected = WalkPathChildren(records, keepExtension, directory, context);
                SortedDictionary<string, bool> actual = Listing(indexed(directory, context));

                tally.Asked++;
                tally.Answers += actual.Count;
                if ( !expected.SequenceEqual(actual) )
                {
                    tally.Differing.Add($"'{directory}' as {context}: expected {expected.Count}, got {actual.Count}");
                }
            }
        }
    }

    private static string DirectiveForm(ScriptRecord record, bool keepExtension)
    {
        string relative = record.RelativePath.Replace('/', '\\');
        if ( keepExtension )
        {
            return relative;
        }

        return Path.ChangeExtension(relative, null) ?? relative;
    }

    /// <summary>The walk path completion replaced: every record's directive form against the typed folder.</summary>
    private static SortedDictionary<string, bool> WalkPathChildren(
        List<ScriptRecord> records, bool keepExtension, string directory, string contextId)
    {
        Dictionary<string, bool> segments = new(StringComparer.OrdinalIgnoreCase);
        foreach ( ScriptRecord record in records )
        {
            if ( record.RelativePath.Length == 0 || !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            string path = DirectiveForm(record, keepExtension);
            if ( !path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) )
            {
                continue;
            }

            string remainder = path[directory.Length..];
            if ( remainder.Length == 0 )
            {
                continue;
            }

            int separator = remainder.IndexOf('\\');
            bool isFolder = separator >= 0;
            string segment = isFolder ? remainder[..separator] : remainder;
            segments[segment] = segments.TryGetValue(segment, out bool existing) ? existing || isFolder : isFolder;
        }

        return Listing(segments.Select(static pair => (pair.Key, pair.Value)));
    }

    /// <summary>Keyed lowercase: the spelling kept for a segment is whichever each path saw first.</summary>
    private static SortedDictionary<string, bool> Listing(IEnumerable<(string Segment, bool IsFolder)> children)
    {
        SortedDictionary<string, bool> listing = new(StringComparer.Ordinal);
        foreach ( (string Segment, bool IsFolder) child in children )
        {
            listing[child.Segment.ToLowerInvariant()] = child.IsFolder;
        }

        return listing;
    }

    /// <summary>Every class declared anywhere, looked up by bare name and qualified, from the declaring file's context.</summary>
    private static Tally CheckClassLookups(ScriptDatabase database)
    {
        Tally tally = new();
        foreach ( LanguageStore store in database.BothLanguageStores )
        {
            foreach ( ScriptRecord record in store.AllRecords.ToList() )
            {
                foreach ( ClassSymbol classSymbol in record.Classes )
                {
                    foreach ( string? namespaceName in new[] { null, classSymbol.Namespace } )
                    {
                        List<string> expected = WalkLookupClasses(store, record.ContextId, namespaceName, classSymbol.KeyName);
                        List<string> actual = DescribeClasses(
                            DatabaseQueries.LookupClasses(store, record.ContextId, namespaceName, classSymbol.KeyName));

                        tally.Asked++;
                        tally.Answers += actual.Count;
                        if ( !expected.SequenceEqual(actual) )
                        {
                            tally.Differing.Add($"{namespaceName}::{classSymbol.KeyName}: expected {expected.Count}, got {actual.Count}");
                        }
                    }
                }
            }
        }

        return tally;
    }

    /// <summary>The read LookupClasses replaced: every declarer of the bare name, filtered by namespace.</summary>
    private static List<string> WalkLookupClasses(LanguageStore store, string contextId, string? namespaceName, string keyName)
    {
        ImmutableArray<ResolvedClass>.Builder matches = ImmutableArray.CreateBuilder<ResolvedClass>();
        foreach ( string path in store.Classes.PathsDeclaring(keyName) )
        {
            if ( !store.TryGet(path, out ScriptRecord record) || !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                if ( classSymbol.KeyName == keyName && (namespaceName is null || classSymbol.Namespace == namespaceName) )
                {
                    matches.Add(new ResolvedClass(classSymbol, record));
                }
            }
        }

        return DescribeClasses(DatabaseQueries.ApplyShadowing(
            matches.ToImmutable(), static match => match.Record, static match => match.Class.KeyName, store, contextId));
    }

    private static List<string> DescribeClasses(ImmutableArray<ResolvedClass> classes)
    {
        List<string> described = [];
        foreach ( ResolvedClass resolved in classes )
        {
            described.Add($"{resolved.Record.Path}:{resolved.Class.Namespace}::{resolved.Class.KeyName}@{resolved.Class.NameRange}");
        }

        described.Sort(StringComparer.Ordinal);
        return described;
    }

    /// <summary>The walk ScriptsInserting replaced: every header per round of the closure, then every script.</summary>
    private static List<string> WalkScriptsInserting(ScriptDatabase database, string header, string headerRelativePath)
    {
        HashSet<string> changed = new(StringComparer.Ordinal) { header };
        bool grew = true;
        while ( grew )
        {
            grew = false;
            foreach ( ScriptRecord record in database.AllGshRecords.ToList() )
            {
                if ( !changed.Contains(record.Path) && InsertsAny(record, changed, headerRelativePath) )
                {
                    changed.Add(record.Path);
                    grew = true;
                }
            }
        }

        List<string> inserting = [];
        foreach ( ScriptRecord record in database.Gsc.AllRecords.Concat(database.Csc.AllRecords) )
        {
            if ( InsertsAny(record, changed, headerRelativePath) )
            {
                inserting.Add(record.Path);
            }
        }

        inserting.Sort(StringComparer.Ordinal);
        return inserting;
    }

    private static bool InsertsAny(ScriptRecord record, HashSet<string> changed, string headerRelativePath)
    {
        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( !edge.IsInsert )
            {
                continue;
            }

            if ( changed.Contains(edge.ResolvedPath) )
            {
                return true;
            }

            if ( headerRelativePath.Length > 0
                && string.Equals(PathUtil.NormalizeScriptPath(edge.RawPath), headerRelativePath, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The walk PlanRename replaced: every record in every store.</summary>
    private static List<string> WalkPlanRename(ScriptDatabase database, string oldPath, string newPath, bool isInsert)
    {
        string wanted = Canonical(oldPath);
        List<string> edits = [];
        if ( wanted == Canonical(newPath) )
        {
            return edits;
        }

        foreach ( ScriptRecord record in database.AllRecords )
        {
            foreach ( DependencyEdge edge in record.Dependencies )
            {
                if ( edge.IsInsert == isInsert && Canonical(edge.RawPath) == wanted )
                {
                    edits.Add($"{record.Path}@{edge.Range}");
                }
            }
        }

        edits.Sort(StringComparer.Ordinal);
        return edits;
    }

    private static string Canonical(string directivePath)
    {
        return directivePath.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
    }
}
