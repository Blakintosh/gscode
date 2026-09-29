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
`GlobalObjectNames`, the keyword sets, `ScriptDocStyle`, the bundled data files, include semantics.

**Check it, do not assume it.** For a test you would keep on a named game, change the game to bo3 and
ask whether the assertion would change. If it would not, the game was incidental. A file named
`*Dialect*` is usually right; the files worth checking are the ones that are not, such as
`LocalReferencesTests` (MW2), `PrivateAccessLintTests`, and the include, namespace and unused-include
lints.

### The default is BO3

`ProfileScope.Default` is `GameProfile.BlackOps3`. It is what `GameProfile.Active` falls back to, and
the generic test sources are written in its syntax (`function`, `#namespace`, `#using`). Do not add a
second default; a test on another game is a dialect test by definition.

### GameProfile.Active is process-global

Production reads `Active` in forty-odd files, handlers included, at REQUEST time. So:

- **Pass the game explicitly AND scope Active to it.** The harnesses do both from one field. A
  workspace whose indexer and `Active` disagree fails silently: the store comes back empty, every
  "is it offered?" assertion fails for a reason that looks like the thing under test, and every "is it
  absent?" assertion passes without proving anything.
- **Never call `GameProfile.Select` in a test.** Use `ProfileScope.Use(profile)` in a `using`, which
  restores the previous game. The restore matters more than the select — `AssemblyInfo.cs` in
  Server.Tests records 121 failures caused by a leftover game, not by overlapping runs.
- The scope must outlive the handler call, not just the indexing. `HandlerWorkspace` holds it until
  disposed; a helper that restores in `finally` and then returns the handler to the caller is wrong.
- Leave `DisableTestParallelization` alone. Removing the production reads of `Active` is separate
  work, not part of a test pass.

The only tests that call `Select` directly are the ones testing `Select` (`GameProfileTests`,
`SupportedGamesHandlerTests`, `ConfigurationHandler`), and the corpus/sample sweeps under
`GameProfileCollection`.

## The path

An in-memory workspace lives at `TestPaths.RawRoot` (`c:\raw`). A test names a script by its
game-relative path — `scripts\lib.gsc` — and never spells a drive. `TestPaths.Raw(relative)` gives the
absolute path where one is needed. The root is lower case because `PathUtil.NormalizeAbsolute`
lowers every path on Windows, so a literal compares equal to what the database stores.

A path is the SUBJECT, and keeps its own literal, when the test is about roots: raw vs mods vs
workspace folders, root derivation, mod overlay order, a path with spaces, a drive-relative or
UNC path. `RootDerivationTests`, `PathResolverTests` and the overlay tests are examples.

## The harnesses

All in `server/tests`. `GSCode.Testing` is referenced by Workspace.Tests and Server.Tests with a
global `using GSCode.Testing;`; Parser.Tests needs a string, not a workspace, and does not reference
it.

| Type | Where | For |
|---|---|---|
| `FakeFileSystem` | `GSCode.Testing` | the in-memory tree. Directories are implied; paths normalized on the way in |
| `TestPaths` | `GSCode.Testing` | `RawRoot`, `Raw(relative)` |
| `TestFile` | `GSCode.Testing` | `(RelativePath, Text)` — one script under the raw root |
| `ProfileScope` | `GSCode.Testing` | `Default`, `Use(profile?)` — select and restore `Active` |
| `HandlerWorkspace` | `GSCode.Server.Tests/Handlers` | an indexed workspace wired like `ServerServices`: real insert provider with a shared `InsertCache`, builtins in `NavigationSupport`, `CompletionEngine`. `Open(relative)`, `Identify(relative)`, `Selector` |
| `TestWorkspace` | `GSCode.Workspace.Tests/Resolution` | indexed store + resolver for database and completion tests. Still takes a root and a profile; converge it on `TestPaths`/`TestFile` when its area is converted |

Extend a harness rather than working around it. If a test needs a piece the harness does not expose,
add it to the harness — wired the way production wires it — so the next test gets it too.

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

- **No constructor, no `IDisposable`, no temp directory** on a class that only needs a workspace. The
  harness is built per question and disposed by `using`.
- **One private helper that asks the question**, parameterized by what the facts vary. Facts hold
  the assertions and a comment on anything non-obvious about them.
- **Sources as `const` strings** with position comments. A source a fact edits is built in the fact.
- **Fact names are sentences** stating the behaviour (`AHighlightNeverIncludesACallFromAnotherFile`),
  as the suites already do. Keep an existing name when converting: renaming a test is not a
  conversion, and the name list is how the pass is verified.
- **A dialect test names its game once**, as a `static readonly GameProfile` field or a
  `[Theory]` over short names, and passes it to the harness — never to `Select`.
- House C# style: Allman braces on every block, `if ( x )` padding, explicit types, no tuple
  deconstruction, no expression-bodied methods.

## What stays on real disk

A temp directory is correct when the file system IS the subject:

- `PhysicalFileSystemTests`, and anything asserting real write stamps — `FakeFileSystem` answers
  `UnixEpoch` for every file (`DependentRefreshStampTests`, the cache-pruning tests).
- SQLite and the persistent cache (`Cache/*`, `Restored*`, `DeleteDatabaseTests`).
- The bundled data loaders reading `AppContext.BaseDirectory\Api` (`ApiLoaderTests`, `ObjectFieldsTests`,
  `StockScriptsTests`) when they test the loading itself.
- Corpus, perf, scale and sample sweeps. They read real game installs by design.

Everything that reaches scripts through `IFileSystem` — the resolver, the indexer,
`ResolverInsertProvider` and therefore `#insert` — works over `FakeFileSystem`. Grep before
believing a comment that says something "cannot be faked".

## Doing a conversion pass

One area per pass, one `[VC]` commit per area. The areas, in the order they pay:

1. `Server.Tests/Handlers` files building a temp-dir workspace by hand (the pilot).
2. The remaining `Server.Tests/Handlers` files that construct `PathResolver`/`WorkspaceIndexer`.
3. `Workspace.Tests/Resolution/TestWorkspace` and its callers, onto `TestPaths` and `TestFile`.
4. `Workspace.Tests` Database, Completion and Analysis files that spell their own roots
   (`c:\ws`, `C:\bo3\share\raw`, `c:\work` ...) for an incidental path.
5. The profile audit: every file naming `Cod4`, `ModernWarfare2`, `BlackOps` or `ByName(...)`,
   put through the thought experiment above.

Steps:

1. **Baseline first.** Per the `build-and-test` skill, run the suite and save the test-name list:
   `dotnet test <proj> -c Release --nologo --no-build --list-tests | sed -n '/The following Tests are available/,$p' | sort`.
   If the editor's language server holds the Release DLLs (MSB3027), use `-c Debug` for both runs
   rather than killing it.
2. **Convert.** The harness replaces the setup; the assertions do not change. A test whose assertion
   must change to pass is a finding — stop and report it, do not edit the assertion.
3. **Verify.** Same filter, same counts: the name list must diff EMPTY against the baseline and the
   pass count must match. Run `SourceEncodingTests` (it is in the everyday run) — write files with
   the Write/Edit tools or Python with `newline='\n'`; Bash heredocs eat the backslashes in
   `@"scripts\lib.gsc"`.
4. **Grep the area** for what the pass removes: `GetTempPath`, `GameProfile.Select`,
   `new PhysicalFileSystem`, `RootConfig.Create`, `new WorkspaceIndexer`. What is left must be on the
   "stays on real disk" list or be a dialect test.
5. **Commit** `[VC]`, prose body with the counts, no co-author line.

## Knowing the pass worked

The measure is how many places a change to setup now has to land. Before the pilot, adding a header
cache to handler tests meant editing eleven copies of the same twenty lines; after it, one
constructor. Report that number, and the lines removed, not "tests cleaned up".
