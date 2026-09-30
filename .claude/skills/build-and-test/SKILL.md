---
name: build-and-test
description: Build and test the GSCode server and client. Use whenever running dotnet build, dotnet test, or the corpus suites in this repo — it covers the per-project Release convention, the running-server DLL lock, and the silent no-op that makes a green corpus run meaningless.
---

# Building and testing GSCode

## Always build per project, in Release

```bash
cd server
dotnet build src/GSCode.Parser/GSCode.Parser.csproj -c Release --nologo
dotnet test  tests/GSCode.Workspace.Tests/GSCode.Workspace.Tests.csproj -c Release --nologo
```

**Why per project rather than the solution:** a running language server holds its DLLs open, so a
solution-wide build fails with MSB3027 partway through and leaves you guessing which project broke.
Building the one project under change avoids the lock entirely.

**Which configuration:** the one the running server is NOT using. That is usually Debug, which is
why Release is the default here, but it is not fixed: a session on 2026-09-29 found the editor's
server running from `src\GSCode.Server\bin\Release`, and every Server.Tests Release build failed
with MSB3027 ("The file is locked by: .NET Host"). Check before assuming:

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Select-Object ProcessId, CommandLine
```

and build the other configuration — for BOTH the baseline and the verification run, since a test
list from one and a pass count from the other are not a comparison. Never kill the process: it is
the user's editor.

The three suites:

| Project | Covers |
|---|---|
| `tests/GSCode.Parser.Tests` | lexer, preprocessor, parser, extraction, game profiles |
| `tests/GSCode.Workspace.Tests` | resolution, database, completion, lints, typing, cache |
| `tests/GSCode.Server.Tests` | LSP handlers, formatter, and the real-corpus sweeps |

## The corpus environment variables

Every one is optional, and an absent corpus makes its tests **no-op and pass**. Each names the
game's **raw folder directly**.

```
GSCODE_CORPUS_COD4   …\CoD4-Mod-Tools\raw
GSCODE_CORPUS_WAW    …\cod5-mod-tools\raw
GSCODE_CORPUS_MW2    …\IW4
GSCODE_CORPUS_BO1    …\Call of Duty Black Ops 42740\raw
GSCODE_CORPUS_BO3    …\Call of Duty Black Ops III\share\raw
```

**They are read at process start**, so setting one at user scope does NOT reach an already-running
shell. Pass them inline for the run:

```powershell
$env:GSCODE_CORPUS_BO3='...\share\raw'; dotnet test tests\GSCode.Server.Tests\... -c Release --nologo
```

## The three categories

| filter | what it runs |
|---|---|
| `Category!=Corpus&Category!=Perf` | the unit tests. This is the everyday run, and what CI uses |
| `Category=Corpus` | the sweep over five games' real scripts. The arbiter for any diagnostic change |
| `Category=Perf` | per-file timing and the lex/preprocess/parse/extract split |
| `Category=Scale` | generated 10K–50K-file workspaces: cold/warm start, memory, completion and lint against budgets. No-op unless `GSCODE_SCALE_SIZES` is set (e.g. `10000,25000,50000`) |

Note the everyday filter excludes BOTH. `Category!=Corpus` alone now picks up the perf sweep, which
needs the game installs and takes a second pass over every script.

## Read the duration, not just the word "Passed"

This is the trap worth internalising. A `Category=Corpus` run over five games takes **two to three
minutes**; the usual cod4 + bo3 pair about **thirty seconds**. If it finishes in milliseconds, every
corpus test no-opped because the variables were not visible — and the run proved nothing while
looking exactly like success. `--logger "console;verbosity=detailed"` prints each test's own lines
(`bo3: 980 files …`), which is the quickest proof a corpus was actually read.

**Sweep cod4 and bo3**, the two dialect families, unless a change is specific to another game. The
variables are usually set at user scope, so clear the ones you are not sweeping inline:
`GSCODE_CORPUS_BO1= GSCODE_CORPUS_WAW= GSCODE_CORPUS_MW2= dotnet test …`.

## Files a run writes back

- `Category=Corpus` REWRITES `tests/GSCode.Server.Tests/harvest/*.json` and the per-game
  `Api/<game>_stock_scripts.txt` from what it just swept. A diff there after an unrelated change is
  the committed file being stale, not your change — confirm on the base commit (a throwaway
  `git worktree`) and revert it rather than committing it with your work.
- Reports land in `temp/` (`gscode-perf-*.html`, `gscode-lint-budget-*.html`, `gscode-scale.md`),
  which is not committed.
- `Category=Scale` generates its workspaces under `%TEMP%\gscode-scale` (or `GSCODE_SCALE_ROOT`) —
  gigabytes at 50K, reused between runs, never cleaned up by the suite.

## The encoding gate

`SourceEncodingTests` (in the everyday run) fails on any tracked text file holding a byte-order mark
or a carriage return — `.gitattributes` says LF and git only enforces that on the way IN, so a CRLF
written to disk is invisible to `git diff` and caught only here. Scripts that write source files
must write UTF-8, no BOM, `\n` line endings: PowerShell's `Set-Content` and Python's text mode on
Windows are the usual culprits.

## Before committing a diagnostic change

The corpus is the arbiter. A new Error or Warning must be swept over the ~5,300 shipped scripts
across the five games before it ships: anything it reports there is either a real defect in code
that shipped and works, or a false positive in ours. Zero is the expected answer.

## Client

```bash
cd client
npm run compile          # tsc
npm run lint             # no output is the bar: 0 errors AND 0 warnings
npm run bundle-server    # dotnet publish into client/service/
npm run package          # bundle-server, then vsce package (whose prepublish runs `npm run bundle`)
```

What ships is not tsc's output. `npm run bundle` empties `out/` and has esbuild write one bundled
`out/extension.js` with the dependencies inside it, and the package excludes `node_modules`. F5
still runs `tsc -watch` into `out/`, so a dev session never runs the bundle: a change to how the
client imports something is only proven by installing the VSIX. Run `npm run compile` after
packaging to put the tsc output back for F5.

Every warning the linter used to report was one false positive: `naming-convention` flagging the
quoted VS Code setting keys (`"format.maxBlankLines"`), which no casing could fix. The config now
exempts names that require quotes, so a warning today is real. This section used to give the
warning count instead - it said 26 while the real figure was 28, and nobody noticed. A count that
is expected to be nonzero is a number nothing checks; zero is one anybody can.

After `bundle-server`, check `client/service/Api/` holds all 23 data files. A stale bundle there
once made CoD4 load BO3's builtins, which presented as every engine call being unknown.
