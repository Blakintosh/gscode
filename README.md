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
| Modern Warfare 2 (2009) | adds `foreach`, `childthread`, `call`, file-scope constants |
| Black Ops (2010) | as WaW, plus `#"hash strings"` |
| **Black Ops III (2015)** | `#using` namespaces, classes, `function`, `&` pointers, `/@ @/` ScriptDoc, headers |

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

## Reporting issues

GSCode is an independent implementation of a GSC parser, so it may not have exact parity with the
game's compiler. The goal is to catch everything the compiler catches at build time, plus a range of
mistakes that otherwise only surface at runtime.

If the compiler (Linker) reports an error that GSCode does not — or GSCode reports one on code that
compiles — that is a bug. Please file it on the
[issue tracker](https://github.com/Blakintosh/gscode/issues) with the expected result **and a script
that reproduces it**. Reports without a reproducing snippet will not be investigated.

## Requirements

The language server requires the .NET 10 Runtime, available at
[Download .NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0). **You do not need the SDK.**

## Building

```
cd server && dotnet build GSCode.slnx && dotnet test --filter "Category!=Corpus"
cd client && npm ci && npm run compile
```

Warnings are errors. The corpus tests are excluded above because they read real game installs
through `GSCODE_CORPUS_{COD4,WAW,MW2,BO1,BO3}`; without those set they silently sweep nothing.
[server/ARCHITECTURE.md](server/ARCHITECTURE.md) is the map of the server, and each project carries a
`FOLDER.md` describing its own contents.

## Licence

GSCode is open-source software licenced under the GNU General Public License v3.0.

```
GSCode - Black Ops III GSC Language Extension
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
