# GSCode

A Visual Studio Code language extension providing IntelliSense for Call of Duty's scripting languages — GSC, CSC and GSH.

This repository holds two halves: the GSCode **language server** (C#), which speaks the
[Language Server Protocol](https://microsoft.github.io/language-server-protocol/), and the GSCode
**VSCode extension** (TypeScript), the client that runs it.

For the extension user guide — including setup, settings, commands, formatting, and in-source
pragmas — see the [client README](client/README.md). Release notes live there as well.

## Supported games

Black Ops III is the verified target and the most complete. Four earlier games are supported with
their capabilities checked against their own shipped scripts:

| Game | Dialect notes |
|---|---|
| Call of Duty 4 (2007) | `#include` merge, `maps\x::foo()` path calls, `///` ScriptDoc |
| World at War (2008) | as CoD4, plus client scripts (`.csc`) |
| Modern Warfare 2 (2009) | adds `foreach`, `childthread`, `call`, file-scope constants (`CONST = 4;` — not macros; no game before BO3 has a preprocessor) |
| Black Ops (2010) | as WaW, plus `#"hash strings"` |
| **Black Ops III (2015)** | `#using` namespaces, classes, `function`, `&` pointers, `/@ @/` ScriptDoc, headers, the preprocessor (`#define`, `#if`) |

Every other mainline game up to Black Ops 6 is present as a *core* — a nameable identity over the
shared base dialect, with its specifics left for a contributor to fill in. See
[server/GAME_PROFILES.md](server/GAME_PROFILES.md) for how a profile is defined and promoted.

The dialect is data, not branching: one `GameProfile` decides which keywords lex, which directives
exist, how functions resolve, and which engine data files load.

## Getting started

Open a folder of scripts and GSCode indexes it. Two optional settings tell it where the game's own
scripts live, so that includes and path calls resolve against them:

- `gscode.rawPath` — the game's raw script folder. Set this when the folder you have open is a mod
  or a loose set of scripts. Left empty, only the open workspace folders are indexed.
- `gscode.modsPath` — the folder holding your mods.

Both are derived from the game install where possible, and BO3 is the one game whose raw scripts sit
a level down (`share\raw`); every earlier game uses `raw` directly.

## Support and issue reporting

GSCode is an independent implementation of a GSC parser, so it may not have exact parity with the
game's compiler. The goal is to catch everything the compiler catches at build time, plus a range of
mistakes that otherwise only surface at runtime.

Before opening an issue, try **Developer: Reload Window**, wait for indexing to finish, and check
the active game plus `gscode.rawPath`, `gscode.modsPath`, and `gscode.raw.enabled`. If results still
look stale, run **GSCode: Clear Cache and Reindex**. For server or indexing problems, set
`gscode.serverLogLevel` to `info` or `verbose` and copy the relevant part of the **GSCode Server**
output. The [client README](client/README.md) has the full troubleshooting and cache-reset guide.

### Bug reports

If the compiler (Linker) reports an error that GSCode does not — or GSCode reports one on code that
compiles — that is a bug. Please file it on the
[issue tracker](https://github.com/Blakintosh/gscode/issues) with:

- a short title describing one problem;
- the smallest `.gsc`, `.csc`, or `.gsh` script that reproduces it;
- the expected result and the actual GSCode result, including the diagnostic code when available;
- the selected game, GSCode version or commit, VS Code version, .NET Runtime version, and OS;
- relevant `gscode.*` settings and whether the script is from the workspace, a mod, or raw files;
- reproduction steps and the relevant server log excerpt, with private paths or project names redacted.

Reports without a reproducing snippet will not be investigated. Do not attach proprietary game
files, unreleased assets, credentials, or an entire raw/mod installation; reduce the case to a
small text fixture instead. Search the existing issues first and add details to an existing report
when it describes the same behavior.

#### Copy/paste template

````markdown
### Summary

<!-- One sentence describing the problem. -->

### Game and file

- Game: `bo3`
- File type: `.gsc`

### Expected result

<!-- What the compiler, Linker, or GSCode should do. -->

### Actual result

<!-- What GSCode does instead, including the diagnostic code if available. -->

### Reproduction

<!-- Steps, followed by the smallest script that shows the problem. -->

```gsc
// minimal reproduction
```
````

### Questions and feature requests

Use the same [issue tracker](https://github.com/Blakintosh/gscode/issues) for questions and feature
requests when the answer is not covered by the [extension guide](client/README.md). For a question,
describe what you are trying to do, the script shape involved, and what you expected GSCode to show
or resolve. For a feature request, explain the problem it would solve, give a small example of the
desired workflow, and note which game or dialects it affects. Keep each issue focused on one topic.

### Contributing

Pull requests should include focused changes, tests for parser/server behavior where practical, and
README or release-note updates when user-visible behavior changes. Before submitting, run the
server build/tests and client compilation from [Building](#building). Corpus tests require local
game or mod-tools paths and are intentionally excluded from the normal command unless the relevant
`GSCODE_CORPUS_*` variables are set. Never commit game installations, generated corpus dumps, or
proprietary script data.

For security-sensitive reports, do not publish credentials, private source, or an exploitable
reproduction in a public issue; contact the repository owner privately through GitHub instead.

## Requirements

The language server requires the .NET 10 Runtime, available at
[Download .NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0). **You do not need the SDK.**

## Building

```
cd server && dotnet build GSCode.slnx && dotnet test --filter "Category!=Corpus&Category!=Perf"
cd client && npm ci && npm run compile
```

Warnings are errors. Two categories are excluded above, and both for the same reason: the corpus
sweep reads real game installs through `GSCODE_CORPUS_{COD4,WAW,MW2,BO1,BO3}` and, without those
set, silently sweeps nothing; the perf sweep needs the same installs and makes a second pass over
every script. This is the filter CI uses.
[server/ARCHITECTURE.md](server/ARCHITECTURE.md) is the map of the server, and each project carries a
`FOLDER.md` describing its own contents.

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
