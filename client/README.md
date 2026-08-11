# GSCode

A Visual Studio Code language extension that provides IntelliSense support for Call of Duty's scripting languages — GSC, CSC and GSH.

GSCode helps you to find and fix errors before the compiler has to tell you, streamlining scripting. Additionally, it adds rich IntelliSense into your editor to support the scripting process.

Black Ops III is the verified target. Call of Duty 4, World at War, Modern Warfare 2 and Black Ops are also supported, each with its own dialect: which keywords exist, whether imports merge (`#include`) or name a namespace (`#using`), and which engine data loads. The status bar shows which game is active.

## Requirements

GSCode's language server requires the .NET 10 Runtime, available at [Download .NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0). **You do not need the SDK.**

## Using GSCode

Open a folder containing your scripts in VS Code. GSCode activates automatically for `.gsc`, `.csc`, and `.gsh` files:

- `.gsc` is a server-world script.
- `.csc` is a client-world script.
- `.gsh` is a shared header inserted into either world.

The extension defaults to the Black Ops III dialect (`gscode.game: "bo3"`). Select `cod4`, `waw`,
`mw2`, `bo1`, or `bo3` in Settings when working on another game. The active game is shown in the
status bar. Changing the game or raw/mod paths prompts you to reload the VS Code window; restarting
only the language server does not re-read those startup settings.

### Game files, raw scripts, and mods

GSCode can analyze only the open workspace, or resolve it against the game's own scripts and mods.
These settings control that behavior:

| Setting | Purpose |
| --- | --- |
| `gscode.raw.enabled` | Master switch for reading game raw files; defaults to `true`. |
| `gscode.rawPath` | Absolute path to the game's raw script folder. Leave empty for automatic discovery. |
| `gscode.modsPath` | Absolute path to the folder containing one subfolder per mod. |
| `gscode.rawFileWarningMode` | Warn when saving protected raw files: `off`, `stock` (default), or `all`. |

When a workspace is a mod or loose script folder, set `gscode.rawPath` so includes and path calls
resolve against the stock scripts. A mod overlays the raw folder: its copy wins for that mod, while
the raw copy is used as the fallback. Black Ops III normally uses `share/raw`; earlier supported
games use `raw` directly. GSCode does not modify stock files for you, and raw-file warnings are
there to help avoid editing the wrong copy.

### Indexing and analysis

The default `gscode.workspaceIndexingMode: "partial"` indexes signatures for workspace-wide
navigation, references, and completion. Use `"full"` for diagnostics across the whole index, or
`"off"` when only open files should be analyzed. `gscode.diagnostics.scope` controls which indexed
files publish diagnostics: `open`, `workspace` (default, your workspace/mod files), or `all`
(including stock raw files).

`gscode.enableWorkspaceCache` is enabled by default and stores analyzed scripts per workspace so
unchanged files can be restored quickly. If the cache becomes stale, run **GSCode: Clear Cache and
Reindex** from the Command Palette.

#### Cache location and manual reset

On Windows, the persistent cache is stored outside the repository at:

```text
%APPDATA%\gscode\cache\<hash>.db
```

This normally expands to `C:\Users\<you>\AppData\Roaming\gscode\cache`. SQLite may also create
`<hash>.db-wal` and `<hash>.db-shm` beside the database. The filename is an opaque 16-character
hash of the workspace folders and resolved raw/mod roots, so it does not contain the project name.
The cache contains analyzed records only; deleting it cannot delete your scripts.

On macOS and Linux, use the equivalent OS-specific ApplicationData directory; the final
`gscode/cache` layout and hashed database naming are the same.

The safest reset is **GSCode: Clear Cache and Reindex**, which closes this workspace's database,
removes its database and SQLite sidecars, and reloads VS Code. If you need to remove it manually:

1. Close VS Code windows using the workspace so the GSCode server releases the database.
2. Open `%APPDATA%\gscode\cache` in Explorer, or run `explorer "$env:APPDATA\gscode\cache"`.
3. Delete only this workspace's `<hash>.db`, `<hash>.db-wal`, and `<hash>.db-shm` files.
4. Reopen the workspace and wait for the cold index to finish.

Do not delete the entire cache directory unless you intentionally want to rebuild every workspace's
index. If you cannot identify the right hash, removing all `*.db`, `*.db-wal`, and `*.db-shm` files
in this directory after closing VS Code is safe for source files, but it resets all workspaces.

Cache records are restored only when the on-disk file content still matches. Changes to the server
build, bundled API data, or selected game invalidate old records automatically; no manual cleanup is
normally needed after an extension update. Set `gscode.enableWorkspaceCache` to `false` to disable
persistent caching entirely.

### In-source pragmas

Pragmas are GSCode directives carried inside comments. They suppress GSCode output; they do not
change what the game's compiler or Linker accepts. `disable` and `restore` are C#'s pair, chosen
because each word says which way it goes — which `on`/`off` stops doing as soon as two are nested:

```gsc
// #pragma disable 5014
foo_that_exists_only_in_a_custom_engine_build();
// #pragma restore 5014
```

Use the numeric diagnostic code shown in the Problems panel. Both `5014` and `gscode-5014` are
accepted. A `disable` applies from its comment onward until the matching `restore`; an unmatched
disable continues to the end of the file. The directives can be in line, block, or documentation
comments.

**Any code can be named, whatever severity it carries** — errors, warnings, information and hints
alike, and syntax errors as readily as lints. If you know C#'s `#pragma warning disable`, note that
this is wider than it: there an error cannot be suppressed at all, so do not assume one survives a
`disable` here. Suppressing an error hides the report and nothing else — a file whose syntax errors
are turned off still does not parse, and the features that need a parsed file (completion,
go-to-definition, rename) stay degraded with nothing on screen explaining it. Suppress an error only
when you know why it is wrong.

The C# spelling `#pragma warning disable 5014` is also accepted, so an early file written against it
keeps working. Prefer the short form: `warning` would suggest a narrowness this does not have.

To suppress every diagnostic in a region:

```gsc
// #pragma disable all
legacy_or_generated_code();
// #pragma restore all
```

To leave a hand-formatted region untouched while keeping diagnostics enabled, use the separate
`format` target:

```gsc
// #pragma disable format
        hand_formatted_code();
// #pragma restore format
```

`format` affects GSCode formatting only; it does not suppress diagnostics. Prefer a specific code
over `all` so new diagnostics are not hidden accidentally — and note that `all` means all, so an
`all` region hides the errors in it too.

GSCode 1.5's `// gscode ignore` is still accepted as a legacy alias. It suppresses every diagnostic
on the one line below the comment, at any severity — no more, and it opens no region:

```gsc
// gscode ignore
foo_that_exists_only_in_a_custom_engine_build();
```

`// gsc ignore` and the block form `/* gscode ignore */` work the same way; a block comment covers
the line below the line it closes on. Prefer `#pragma disable` in new code — it names the
code it suppresses and says where it stops.

### Commands and useful editor features

- **GSCode: Show Server Output** opens the language-server log.
- **GSCode: Restart Language Server** restarts a wedged server or picks up a rebuilt server binary.
- **GSCode: Clear Cache and Reindex** deletes this workspace's cache and reloads the window.
- **GSCode: Open Documentation for Symbol** opens the matching API page on [gscode.net](https://www.gscode.net/). It is also bound to `Shift+F1` in GSC, CSC, and GSH files.

GSCode provides diagnostics, hover, completion, signature help, go-to-definition, references,
rename, document/workspace symbols, semantic tokens, folding, code lens, call/type hierarchy,
inlay hints, document links, formatting, and code actions. Formatting is whitespace-only and
refuses files with syntax errors; it verifies its output before applying edits.

For troubleshooting, set `gscode.serverLogLevel` to `info` or `verbose` and inspect the **GSCode
Server** output channel. `gscode.trace.server` can trace the LSP messages exchanged with VS Code.

### Troubleshooting checklist

| Symptom | What to check |
| --- | --- |
| `#using`, `#include`, or path calls cannot be resolved | Confirm `gscode.game`, `gscode.raw.enabled`, `gscode.rawPath`, and `gscode.modsPath`; reload the window after changing any of them. |
| Completions or references stop at the open file | Make sure `gscode.workspaceIndexingMode` is not `off`, then wait for the status bar to finish indexing. |
| Diagnostics appear only in some files | Check `gscode.diagnostics.scope`; `open` intentionally excludes closed files, while `all` includes stock raw scripts. |
| Results look stale after changing paths or game | Use **Developer: Reload Window**. If the index is still wrong, run **GSCode: Clear Cache and Reindex**. |
| The server appears stuck or silent | Open **GSCode: Show Server Output**, set `gscode.serverLogLevel` to `info` or `verbose`, and restart the language server. |
| The extension prompts for .NET | Install the .NET 10 Runtime, not just a VS Code extension or the .NET SDK. |

For the formatter's exact whitespace rules and options, see the repository's [formatting
guideline](../server/FORMATTING.md).

## Release Notes

### 2.0.0 (latest)

A complete ground-up rewrite of the language server and extension for speed, low memory use, and accuracy.

- Rebuilt the entire pipeline from scratch: a span-based lexer, a provenance-tracking preprocessor (`#define`/`#insert`/`#if`), a hand-written recursive-descent parser with error recovery, and symbol extraction — none of it ever throws, so a broken file still gets a full outline and diagnostics.
- Added first-class mod-tools support: `share/raw` plus every mod under `mods/` is indexed in isolation, mod folders overlay raw without crossing each other, and a workspace-only mode works with no game install at all.
- Centralised everything in one script database with structurally isolated GSC and CSC worlds and a shared GSH (header) store, backed by a persistent SQLite cache so cold starts restore unchanged files in seconds.
- Full modern LSP suite: live diagnostics, hover, completion, signature help, go-to-definition, find-all-references (including string/hash/localized/anim literals), document highlight, semantic tokens, folding, selection ranges, document/workspace symbols, code lens, rename, call and type hierarchy, inlay hints, document links, formatting, and code actions.
- Type-flow inference powers inferred-type inlay hints and local-variable hovers, seeded with engine object-field types.
- Formatting (whole document, selection, and on-type) is whitespace-only and corruption-proof: it refuses files with syntax errors and re-checks its own output so it can never alter your tokens.
- Code actions cover remove-duplicate-`#using` and add-missing-`#using`, backed by a namespace-usage lint.
- Macros defined in `.gsh` headers are first-class symbols with go-to-definition, references, and hover via token provenance.
- Added support for four earlier games — Call of Duty 4, World at War, Modern Warfare 2 and Black Ops — with each dialect's keywords, import style, function-pointer and ScriptDoc syntax, and bundled engine data driven by one game profile rather than by branching.
- Replaced `TA_TOOLS_PATH` with `gscode.rawPath` and `gscode.modsPath`, both derived from the game install where possible, so a mod or a loose folder of scripts resolves against the game's own scripts.
- Added snippets for the common constructs. The dialect-specific ones (`foreach`, `function`, `class`, `new`, `#using`, `#precache`, ScriptDoc) are served by the language server and are only offered where the selected game has the construct, rather than being offered everywhere with a note in the description. `#precache` is Black Ops III's alone, and its asset types are further split by world: a `.gsc` is offered only server-side types and a `.csc` gets the `client_*` family — a header (`.gsh`) sees both, since it is inserted into whichever world includes it.
- Expanded diagnostics: argument counts against builtin and script signatures, macro arity, unreachable code, unassigned and unused variables, duplicate imports, duplicate case labels, assignment used as a condition, inheritance cycles, `...` placement, and a missing semicolon reported at the end of the statement that is missing it.

For releases before 2.0, and for the full 2.0 entry including what the rewrite removed, see the
[changelog](CHANGELOG.md). New releases are written there; this section keeps the current one only.

## Reporting Issues

GSCode is an independent implementation of a GSC parser, so it may not have exact parity with the
game's compiler. If the compiler (Linker) reports an error that GSCode does not — or GSCode reports
one on code that compiles — that is a bug worth filing.

The [repository README](https://github.com/Blakintosh/gscode#bug-reports) has the full reporting
guide, including the details a report needs and a copy/paste template. The one requirement worth
repeating here: attach the smallest script that reproduces the problem. Reports without one will
not be investigated.

## Known Issues

- A macro hover shows the macro's own body with the call site's arguments substituted. Macros used
  *inside* that body are shown by name and are not themselves expanded, so one hover is one level
  deep.
- A macro body longer than 240 characters is truncated with an ellipsis. A hover is a glance, not a
  code listing; use go-to-definition to read the whole macro.
- Files that are not open report the problems found when they were indexed — syntax errors, unknown
  directives and precache mistakes. The cross-file lints (unused `#using`, private access, dev-block
  calls, read-only writes) need an open document, so a file can gain problems on being opened.

## Licence

GSCode is open-source software licenced under the GNU General Public License v3.0.

```
GSCode - Call of Duty GSC Language Extension
Copyright (C) 2026 Blakintosh

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
```

Please see [LICENSE.md](https://github.com/Blakintosh/gscode/blob/main/LICENSE.md) for details.
