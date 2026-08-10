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

**Why per project rather than the solution:** a running language server holds the Debug DLLs
open, so a solution-wide build fails with MSB3027 partway through and leaves you guessing which
project broke. Building the one project under change avoids the lock entirely.

**Why Release:** the Debug output is what the extension host is using. Writing to it while the
server runs is the same collision.

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

Note the everyday filter excludes BOTH. `Category!=Corpus` alone now picks up the perf sweep, which
needs the game installs and takes a second pass over every script.

## Read the duration, not just the word "Passed"

This is the trap worth internalising. A `Category=Corpus` run over five games takes **two to three
minutes**. If it finishes in milliseconds, every corpus test no-opped because the variables were
not visible — and the run proved nothing while looking exactly like success.

## Before committing a diagnostic change

The corpus is the arbiter. A new Error or Warning must be swept over the ~5,300 shipped scripts
across the five games before it ships: anything it reports there is either a real defect in code
that shipped and works, or a false positive in ours. Zero is the expected answer.

## Client

```bash
cd client
npm run compile          # tsc
npm run lint             # 26 pre-existing naming warnings, 0 errors is the bar
npm run bundle-server    # dotnet publish into client/service/
```

After `bundle-server`, check `client/service/Api/` holds all 23 data files. A stale bundle there
once made CoD4 load BO3's builtins, which presented as every engine call being unknown.
