# Development — build, run, test, release

Everything needed to go from a fresh clone to a shipped `.vsix`. The `build-and-test` skill
(`.claude/skills/build-and-test/SKILL.md`) holds the finer points; this page is the map.

---

## Toolchain

| Need | Version | For |
|---|---|---|
| .NET SDK | 10.0 (`net10.0`, C# 14 pinned in `server/Directory.Build.props`) | the server, tests, `tools/field-data` |
| Node.js + npm | 20 (what CI uses) | the client extension |
| VS Code | 1.85 or newer | running the extension |
| Windows | — | the `scripts\*.bat` wrappers; the code itself is cross-platform |

Users need only the .NET 10 **runtime**; the extension checks for it at activation.

## Build rules you will hit

- **Warnings are errors**, analyzers at `latest`, code style enforced in the build
  (`server/Directory.Build.props`, `server/.editorconfig`). XML doc comments are compiled and
  validated, because this codebase keeps its reasoning in them.
- **House C# style:** braces always, `if ( x )` spacing, explicit simple logic. The editorconfig and
  surrounding code are the reference.
- **The server version** is stated once, in `Directory.Build.props` (`<Version>`), and must match
  `client/package.json`.
- **A running language server locks its DLLs.** Build and test per project, in the configuration the
  editor is NOT using, or the build fails with MSB3027. The batch files detect this for you.
- **Line endings are LF, no BOM** (`.gitattributes`; `.bat` files are CRLF). `SourceEncodingTests`
  fails otherwise. On Windows, a Python script opening a file in text mode writes CRLF; pass
  `newline='\n'`.

## Running the extension from source

1. `cd client && npm ci && npm run compile`.
2. Build the server: `dotnet build server/src/GSCode.Server/GSCode.Server.csproj` (Debug).
3. Copy `client/.env.example` to `client/.env`. It points the debug session at
   `server/src/GSCode.Server/bin/Debug/net10.0/` (or Release).
4. Open the repository in VS Code and press F5: "Run Extension (Debug server)" or "(Release
   server)" from `.vscode/launch.json`. A second VS Code window opens with the extension loaded.
5. Logs: "GSCode" channel (client lifecycle) and "GSCode Server" channel (server stderr). Set
   `gscode.serverLogLevel` to `verbose` to see one line per analysis with its timing.

## Tests

Three suites plus a shared helper project, all under `server/tests/`. Which suite covers what, every
test class by keyword, and every environment variable: `server/tests/FOLDER.md`.

| Category | Command | Needs | Time |
|---|---|---|---|
| Unit (what CI runs) | `scripts\test.bat`, or `dotnet test <project> --filter "Category!=Corpus&Category!=Perf"` | nothing | 1–2 min |
| Corpus | `scripts\corpus.bat [all\|games…]` | `GSCODE_CORPUS_<GAME>` | minutes |
| Diagnostic sweep | `scripts\sweep.bat [all\|games…]` | same | ~30 s for cod4 + bo3 |
| Perf | `scripts\perf.bat [all\|games…]` | same; Release | minutes |
| Scale | `scripts\scale.bat 10000 50000` | `GSCODE_CORPUS_BO3`; GBs of disk | minutes per size |

**A corpus test with no corpus set passes without running.** The batch files refuse to run with no
corpus; when running `dotnet test` yourself, check the duration and the output.

Run corpus, sweep and perf on **cod4 and bo3** (the default), one per dialect family. `all` adds WaW,
MW2 and BO1 and is for changes that touch those dialects specifically.

A corpus run rewrites `server/tests/GSCode.Server.Tests/harvest/*.json` and the
`Api/<game>_stock_scripts.txt` lists. A diff there after an unrelated change means the committed
file was stale.

Reports (sweep, perf, lint budget, scale) are HTML/Markdown pages under `temp/`.
`GSCODE_SWEEP_REPORT` / `GSCODE_PERF_REPORT` move them.

### Writing a test

Use the shared setup in `server/tests/GSCode.Testing`: `TestWorkspace.Build(files)` for anything
needing a database, `HandlerWorkspace.BuildAsync(files)` for a handler, `TestParse.Analyze(source)`
for one file, `FakeFileSystem` + `TestPaths` instead of a real disk. Name a game or a real path only
when the test is about that game or path. The `standard-tests` skill has the rules.

### Verifying a change

| Change | Minimum before merging |
|---|---|
| Any code | unit tests for the touched projects |
| A diagnostic | its `*LintTests`, plus `scripts\sweep.bat` — an Error firing on stock scripts is a bug |
| Parser / grammar | `scripts\corpus.bat` (nothing throws, error budget, formatter round trip) |
| Formatter | `scripts\corpus.bat` (token stream preserved, idempotent, on every stock script) |
| `ScriptRecord` or the serializer | `RecordSerializerTests`, `RecordFormatCorpusTests`, version bump |
| A hot path (indexing, lints, completion) | `scripts\perf.bat` before and after; record it in `server/PERF.md` |
| A request handler or index | `scripts\scale.bat` when the change could add a walk over records |

Batch small fixes and run one corpus sweep at the end rather than one per commit.

## Game data (`server/tools/field-data`)

Builtin function libraries, engine object fields and radiant map keys are generated, not
hand-edited. Sources: `server/tools/field-data/sources/originals/` (verbatim files from game
installs) and `sources/curated/` (the editable JSON truth). Running the tool (`dotnet run` in that
folder) writes the bundled files in `server/src/GSCode.Workspace/Api/`. Layers and provenance:
`server/tools/field-data/FOLDER.md`. Procedure and what a regeneration must not drop:
`regenerate-game-data` skill.

BO3 macro data for the site lives in `data/macros/` (`SCHEMA.md` there).

## Packaging and release

**Version numbers.** The Marketplace allows no semver suffixes, so pre-releases and releases share
one `major.minor.patch` namespace, split the way VS Code documents it: **even minors are releases,
odd minors are pre-releases.** While stable is on 2.0.x, previews publish as 2.1.N; once stable moves
to 2.2.0, previews become 2.3.N. Never ship a full release on the odd minor the previews are using,
and take the next stable minor to the even number above the current preview minor, or pre-release
users are never moved onto it. `.github/workflows/prerelease.yml` states the same rule.

**Locally:**

1. Bump `<Version>` in `server/Directory.Build.props`, `version` in `client/package.json` and
   `client/package-lock.json`, and `extensionVersion` in `site/src/lib/data/site.ts`, together.
   Move `## Unreleased` in `client/CHANGELOG.md` under the new version, and update the release
   notes in `client/README.md`.
2. `scripts\build-vsix.bat`. It runs `npm run package`: type-check, `dotnet publish` the server in
   Release into `client/service/`, esbuild bundle, `vsce package`. Output:
   `client/gscode-<version>.vsix`. Nothing is published.
3. Install locally to check: `code --install-extension client\gscode-<version>.vsix`.

The local package publishes the working tree as it is, uncommitted changes included. Close any
editor running the Release server first.

**Publishing** is the GitHub workflows' job, on the upstream repository where the Marketplace
token lives:

- **Release:** push a tag `v<version>` matching `client/package.json` exactly
  (`.github/workflows/release.yml`). It checks the match, builds the VSIX (`build-vsix.yml`), then
  waits for a required reviewer to approve the `marketplace` environment before publishing. The
  VSIX is attached to the run while it waits, for a smoke test, and to the GitHub Release after.
- **Pre-release:** every push to `preview` or `preview/**` publishes a Marketplace pre-release
  automatically (`prerelease.yml`), versioned `<major>.<next odd minor>.<run number>` from
  `client/package.json`.

## CI

`.github/workflows/ci.yml`, on push to `main`, `next`, `next-major` and on pull requests, on
Windows: build the server solution, run the unit filter, `npm ci`, compile and lint the client. The
other three workflows are the publishing ones above. Read
the per-assembly test totals, not only the step colour: a crashed test host still prints "Passed!"
for the tests that finished.

## Repository conventions

- Commit titles start with a tag such as `[VC]` or `[DOCS]`, as `git log` shows.
- Open questions and backlog: `server/FOLLOWUPS.md` only.
- A new source file gets a section in its project's `FOLDER.md` in the same change.
