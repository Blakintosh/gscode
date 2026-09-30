---
name: standard-tests
description: Write or convert a GSCode test so it uses the standard setup — one default game, one fake root, one in-memory workspace harness — and names a game or a real path only when that is what the test is about. Use when adding a test, when a test builds its own resolver/indexer/database, selects a GameProfile, writes to a temp directory, or spells a drive letter, and when asked to make the tests "generic", "standardized" or "less profile-dependent". Covers the subject-vs-incidental rule, the harnesses, the class format, what must stay on real disk, and the verification a conversion pass has to clear.
---

# Standard tests

A test states **what it is about**. Everything it is not about comes from the standard setup, spelled
the same way in every suite, so a reader sees the one thing that varies.

The rule that governs everything below: **is the value the SUBJECT of the test, or incidental to it?**
It is asked twice — of the game, and of the path.

## The game

### The thought experiment

Imagine GSCode supported one game. Which tests would lose their meaning? Those are the **dialect
tests**, and they are the only ones that should name a profile. Every other test would still say
something true, so it runs on the default and never mentions a game.

A test is a dialect test when its assertion depends on a capability that differs between profiles:
`ResolvesByNamespace` (namespace vs path-qualified calls — BO3 `lib::helper()` against CoD4
`maps\lib::helper()`), `HasMacros`, `HasFunctionKeyword`, `HasInlinePathCalls`, `HasHashStrings`,
`GlobalObjectNames`, the keyword sets (`private`, `class`, `const`), `ScriptDocStyle`, IW file-scope
constants, `#include` semantics, the bundled data files.

**Check it by reading, not by running.** Ask whether the assertion would change on bo3. Swapping the
game and running proves nothing: a CoD4-syntax source fails to PARSE under BO3, which is not the
dialect difference the test is about. The 2026-09-29 audit read every test naming a game and found
each one naming it only in the fact that needs it — the shape to keep. A generic class may hold a
dialect fact (`LocalReferencesTests` has one MW2 fact about file-scope constants); the game goes on
that fact, not the class.

### The default is BO3

`ProfileScope.Default` is `GameProfile.BlackOps3`. It is what `GameProfile.Active` falls back to, and
the generic test sources are written in its syntax (`function`, `#namespace`, `#using`). Do not add a
second default; a test on another game is a dialect test by definition.

### GameProfile.Active is process-global

Production reads `Active` in forty-odd files, handlers and lints included, at REQUEST time. So:

- **Pass the game to the harness; it scopes Active from the same field.** `HandlerWorkspace` and
  `TestWorkspace` both hand one profile to the indexer, the data loads, the analysis they give back
  AND `Active`. A workspace whose indexer and `Active` disagree fails silently: the store comes back
  empty, every "is it offered?" assertion fails for a reason that looks like the thing under test,
  and every "is it absent?" assertion passes without proving anything.
- **Never call `GameProfile.Select` in a test.** Use `ProfileScope.Use(profile)` in a `using`, which
  restores the PREVIOUS game (not bo3) and throws on an unsupported one. The restore matters more
  than the select — `AssemblyInfo.cs` in Server.Tests records 121 failures caused by a leftover game.
- **Scopes nest.** A test that asks a BO3 file against a CoD4 workspace opens a second
  `ProfileScope` for the asking game inside the workspace's (`IncludeUsageLintTests.Lint`).
- The scope must outlive the query, not just the indexing. Both harnesses hold it until disposed; a
  helper that restores in `finally` and then returns the handler to the caller is wrong.
- Leave `DisableTestParallelization` alone. Removing the production reads of `Active` is separate
  work, not part of a test pass.

The only tests that call `Select` directly: `GameProfileTests` (which tests `Select`), and the
corpus/sample sweeps under `GameProfileCollection`.

## The path

An in-memory workspace lives at `TestPaths.RawRoot` (`c:\raw`), with `TestPaths.ModsRoot`
(`c:\mods`) for the setups that configure a mods root and leave it empty. A test names a script by
its game-relative path — `scripts\lib.gsc` — and never spells a drive. `TestPaths.Raw(relative)`
gives the absolute path; `TestPaths.Config(files)` is the standard `RootConfig` over both roots.
Lower case because `PathUtil.NormalizeAbsolute` lowers every path on Windows, so a literal compares
equal to what the database stores.

A path is the SUBJECT, and keeps its own literal, when the test is about roots or is data:

- roots: raw vs mods vs workspace folders, root derivation, overlay order across several roots, a
  path with a space, drive-relative or UNC (`PathResolverTests`, `RootDerivationTests`,
  `RawWriteGuardTests`, `ImportResolutionProbeCostTests`, `OverlayShadowingReferenceTests`,
  `WorkspaceFoldersHandlerTests`, `DiagnosticsUriTests`, `DiagnosticsPublishOrderTests`);
- data: hand-built `ScriptRecord`s that carry their own context ids and folders (the Database index
  tests, `SqliteCacheTests`, `RecordSerializerTests`, `WorkspaceDiagnosticsRefreshTests`,
  `WorkspaceSymbolShadowingTests`). Most already spell `c:\raw` and `c:\mods`.

About 310 literals in 28 files remain for those reasons. A smaller diff there is not unfinished work.

## The harnesses

All in `server/tests`. `GSCode.Testing` is a non-test library referenced by Workspace.Tests and
Server.Tests with a global `using GSCode.Testing;`. Parser.Tests needs a string, not a workspace, and
does not reference it — do not add the reference to reach `ProfileScope`; a Parser test that needs a
game passes it to the parser call.

| Type | Where | For |
|---|---|---|
| `FakeFileSystem` | `GSCode.Testing` | the in-memory tree. Directories are implied; paths normalized on the way in; every write stamp is `UnixEpoch` |
| `TestPaths` | `GSCode.Testing` | `RawRoot`, `ModsRoot`, `Raw(relative)`, `Config(files)` |
| `TestFile` | `GSCode.Testing` | `(RelativePath, Text)` — one script under the raw root |
| `ProfileScope` | `GSCode.Testing` | `Default`, `Use(profile?)` — select and restore `Active` |
| `TestWorkspace` | `GSCode.Testing` | `Build(files \| fakeFileSystem, profile?, mode)`: indexed store + resolver on `TestPaths.Config`, for lint, database and completion tests. `Analyze(relative)` for a file it holds, `Analyze(relative, text)` for one the index has not seen |
| `TestDocuments` | `GSCode.Testing` | `Standalone()`: open documents with no workspace behind them, for tests whose subject is the document store itself |
| `HandlerWorkspace` | `GSCode.Server.Tests/Handlers` | `BuildAsync(files, profile?, mode)`: wired like `ServerServices` — real `ResolverInsertProvider` over a shared `InsertCache` (so `#insert` works from a `.gsh` in the file list), builtins in `NavigationSupport`, `CompletionEngine`. `Open(relative)` for a file it indexed, `Open(relative, text)` for a buffer it did not, `Identify(relative)`, `Selector` |

`HandlerWorkspace.Selector` is `gsc`; no handler reads its selector while answering, so it serves
`.csc` requests too.

Extend a harness rather than working around it. If a test needs a piece the harness does not expose,
add it to the harness — wired the way production wires it — so the next test gets it too.

### Which setup a test gets

1. **The harness**, for every handler, lint, completion and database test that needs a workspace —
   including ones that want it EMPTY (`BuildAsync([])`) or want the asking file kept out of the
   index (`Open(relative, text)`, `TestWorkspace.Analyze(relative, text)`). A test that wants an
   empty or hand-built builtin library passes it to the HANDLER it constructs; the store is not
   what it is choosing (`InlayHintParameterTests`, `InlayHintMacroTests`).
2. **`TestDocuments.Standalone()`**, when the document store IS the subject: analysis gates,
   single-flight reruns, versions, untitled buffers.
3. **Hand-built pieces**, only for the one piece that is the subject, with the harness supplying
   the rest: records upserted with their own context ids go into the harness's own database
   (`ResolveForQueryTests`); a document store gated to park an analysis mid-flight is built by
   the test and handed to collaborators from `HandlerWorkspace.BuildAsync([])`
   (`DependentRefreshStampTests`); a hand-built builtin library goes to the lint that consumes it
   (`ArgumentCountLintTests`).
4. **Real disk**, when the file system is the subject (below).

"I seeded it by hand on purpose" is worth checking before it is believed: of the nine handler tests
first filed under hand-built, eight only wanted an empty workspace or an unindexed buffer.

## The format of a test class

```csharp
/// <summary>
/// What the feature must do, and why this test drives it through what it does (the handler, the
/// indexer, a real two-file workspace). One paragraph per idea; no restating the method names.
/// </summary>
public sealed class ReferencesAcrossNamespacesTests
{
    // Position comments sit beside the source they describe: "helper();" is line 4, column 4.
    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";
    private const string CallerSource = "...";

    /// <summary>The one question every fact below asks, with only what varies as parameters.</summary>
    private static async Task<List<string>> ReferencesAtAsync(string relativePath, int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
        ]);
        workspace.Open(relativePath);

        ReferencesHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);
        ...
    }

    [Fact]
    public async Task ACallFromAnotherNamespaceIsAReference()
    {
        ...
    }
}
```

With a game, the list is indented under the call and the profile follows it:

```csharp
        using TestWorkspace workspace = TestWorkspace.Build(
            [
                new TestFile(@"maps\_utility.gsc", UtilitySource),
                new TestFile(@"maps\mp\caller.gsc", CallerSource),
            ],
            s_cod4);
```

- **No constructor, no `IDisposable`, no temp directory** on a class that only needs a workspace. The
  harness is built per question and disposed by `using`.
- **One private helper that asks the question**, parameterized by what the facts vary. Facts hold
  the assertions and a comment on anything non-obvious about them. A parameter that always equals a
  file already in the workspace (the source text of the opened file) goes.
- **Sources as `const` strings** with position comments. A source a fact edits is built in the fact.
- **Fact names are sentences** stating the behaviour (`AHighlightNeverIncludesACallFromAnotherFile`),
  as the suites already do. Keep an existing name when converting: renaming a test is not a
  conversion, and the name list is how the pass is verified.
- **A dialect test names its game once**, as a `static readonly GameProfile` field, a local in the
  one fact, or a `[Theory]` over short names, and passes it to the harness — never to `Select`.
- House C# style: Allman braces on every block, `if ( x )` padding, explicit types, no tuple
  deconstruction, no expression-bodied methods.

## What stays on real disk

A temp directory is correct when the file system IS the subject:

- `PhysicalFileSystemTests`, and anything asserting real write stamps — `FakeFileSystem` answers
  `UnixEpoch` for every file (the cache-pruning tests). Read what "stamp" means before filing a
  test here: `DependentRefreshStampTests` is about the diagnostics VERSION stamp, never wrote a
  file, and carried a temp directory for nothing.
- SQLite and the persistent cache (`Cache/*`, `Restored*`, `DeleteDatabaseTests`).
- The bundled data loaders reading `AppContext.BaseDirectory\Api` (`ApiLoaderTests`, `ObjectFieldsTests`,
  `StockScriptsTests`) when they test the loading itself.
- Corpus, perf, scale and sample sweeps. They read real game installs by design.

Everything that reaches scripts through `IFileSystem` — the resolver, the indexer,
`ResolverInsertProvider` and therefore `#insert` — works over `FakeFileSystem`. The only direct disk
reads in `src` are the bundled-data loaders and the cache. Grep before believing a comment that says
something "cannot be faked"; `HoverDefinitionLinkTests` said so about its macro case, and it was wrong.

## Doing a conversion pass

Six areas were converted on 2026-09-29 (`test/standard-harness`): the temp-dir handler tests, the
rest of the handlers, `TestWorkspace` itself, the incidental roots, the profile audit, and the
Workspace.Tests helpers that wrote the resolver/database/indexer block by hand. New tests start on
the standard setup; a pass today is for drift, or for a file the list below names as still
hand-built.

A helper may build a `TestWorkspace` with `using`, query it, and return plain results (a store, a
diagnostic list) — but only on the DEFAULT game, where disposing restores the same Active the
queries would have seen anyway. A helper on another game must keep the workspace alive for the
caller's queries, or run them itself before it returns.

1. **Baseline first.** Build and run the suite, then save the test-name list:
   `dotnet test <proj> -c <cfg> --nologo --no-build --list-tests | sed -n '/The following Tests are available/,$p' | sort`.
   The `build-and-test` skill says which configuration: whichever one the editor's running server
   did NOT load. Use the same one for both runs.
2. **Convert.** The harness replaces the setup; the assertions do not change. Replacing an incidental
   path INSIDE an expected value — `C:\ws\maps\top.gsc` becoming `TestPaths.Raw(@"maps\top.gsc")`,
   or the path a hover prints — is setup. Changing a count, a shape, or presence/absence is a
   FINDING: stop and report it, do not edit the assertion.
3. **Check that converted facts can fail.** A test moved from hand-committed analyses to the real
   indexer can go green for a different reason. Confirm each converted class asserts at least one
   thing PRESENT (a label, a caller, a touched file, a count above zero). If every fact asserts
   absence or `Single`, add the thing it says is absent once, watch it fail, revert.
4. **Verify.** Same filter, same counts: the name list must diff EMPTY against the baseline and the
   pass count must match. `SourceEncodingTests` runs in the everyday filter.
5. **Prune the usings the pass orphaned.** IDE0005 is not enforced here, so nothing flags them.
   Temporarily enable it for the project and format only the touched files:
   ```bash
   printf '[*.cs]\ndotnet_diagnostic.IDE0005.severity = warning\n' > tests/<Proj>/.editorconfig
   dotnet format style tests/<Proj>/<Proj>.csproj --diagnostics IDE0005 --severity warn --include <files>
   rm tests/<Proj>/.editorconfig
   ```
   Build first: on a project that does not compile, IDE0005 has nothing to go on.
6. **Grep the area** for what the pass removes: `GetTempPath`, `GameProfile.Select`,
   `PhysicalFileSystem`, `RootConfig.Create`, `TestPaths.Config(`, `PathResolver`,
   `WorkspaceIndexer`, `DocumentStore`, `NavigationSupport`, `.Commit(`, drive letters. Grep the TYPE
   names, not `new X`: `PhysicalFileSystem fileSystem = new();` is target-typed, and a search for
   `new PhysicalFileSystem` walked past one for three passes. `TestPaths.Config(` is on the list because a
   hand-built workspace spelled on the standard roots no longer matches `RootConfig.Create` — the
   grep that stops finding a pattern is not proof the pattern is gone. What is left must be on the
   "stays on real disk" list, in "What is still hand-built", a root/data path from "The path", or a
   dialect test.
7. **Commit** `[VC]`, prose body with the counts, no co-author line.

**Writing the files.** C# test sources are full of `@"scripts\lib.gsc"` and `"#using scripts\\lib;"`.
Bash heredocs and `sed` replacement strings mangle those backslashes, and Python's text mode writes
CRLF. Write edit scripts with the Write tool, as `.py` files using raw strings and
`open(..., newline='\n')`, then run them. Never pipe a heredoc into `python -` after another command
that reads stdin (`cat > /dev/null; python - <<EOF` hangs).

**A const that becomes a call.** `private const string X = @"C:\bo3\share\raw\..."` rewritten to
`TestPaths.Raw(...)` must become `static readonly`; a default parameter value must become a relative
path resolved inside the method. The compiler reports both (CS0133, CS1736).

## What is still hand-built, and why

- **The indexer, resolver or cache is the subject:** `Indexing/*` (concurrency, ownership, watcher
  races, restore, pruning, insert cache), `HeaderEditRefreshTests`, `DependencyRewriteTests`,
  `PathResolverTests`, `ScriptDatabaseTests` (partial vs full passes over one fixture tree).
- **Seeded by hand on purpose:** `ArgumentCountLintTests` (a hand-built builtin library),
  `DialectIncludeScopeTests` and `ExportSignatureTests` (records committed per dialect),
  `DevBlockCallLintTests` (commits the asking file beside the index), `ClassMethodLintTests`' and
  `UsingNotFoundLintTests`' resolver-only helpers. In the handlers, only the one subject piece
  in `DependentRefreshStampTests` (its gated document store) and `WorkspaceSymbolShadowingTests`
  (hand-built records into a bare database, since the handler takes nothing else).
- **Style, not setup:** `CompletionEngineTests.BuildWorld` now builds on `TestWorkspace` but still
  returns a tuple its sixty callers deconstruct, against the house style. Its CoD4 fact asserts on
  a builtin because a merge-dialect fixture "extracts to nothing" — true of the default-game
  workspace it builds, and fixable by passing CoD4. A candidate for its own commit.

The 2026-09-29 pass found two helpers indexing CoD4-syntax fixtures under the default BO3 — their
stores held no declarations at all (`FunctionResolutionLintTests.LintAsCod4`,
`DialectCompletionTests.BuildEngine`). Both now pass the game, and both still pass. Look for this
whenever a fixture's source has no `function` keyword.

## Knowing the pass worked

The measure is how many places a change to setup now has to land. Before these passes, adding a
header cache to handler tests meant editing eleven copies of the same twenty lines, `RootConfig`
was built the same way in 43 places, and two private `HeaderInsertProvider` copies stood in for the
real one. Now each is one constructor or one method. Report that number, and the lines removed, not
"tests cleaned up".
