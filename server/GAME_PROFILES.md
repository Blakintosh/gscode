# Game profiles — the dialect matrix

Every mainline Call of Duty from CoD4 (2007) to BO6 (2024) is a `GameProfile` in
`src/GSCode.Core/GameProfile.cs`, registered in `Profiles/SupportedProfiles.cs`. A profile is the
portability seam: every dialect difference — which words are keywords, how imports work, how a
pointer is written, where the raw scripts live — is reached through it, never through an inline
constant.

Profiles come in two grades:

- **Supported (5):** `cod4`, `waw`, `mw2`, `bo1`, `bo3`. Capabilities established from each game's
  real scripts (not the wordfile — the scripts are the ground truth). All five are also
  **Verified**: proven against the game's own script tree by the corpus gate, not merely filled in
  from a worksheet. See [Verification evidence](#verification-evidence).
- **Cores (13):** everything else in the lineage — `mw3`, `bo2`, `ghosts`, `aw`, `iw`, `wwii`,
  `bo4`, `mw19`, `bocw`, `vg`, `mw22`, `mw23`, `bo6`. A core is a **nameable identity over the
  shared base dialect**: it carries only `Id`/`ShortName`/`DisplayName`/`ReleaseYear`/`Family` (and
  `HasInlinePathCalls`, the base IW shape). Everything else matches the base until a contributor
  with that game's tools fills it in and promotes it. A core deliberately encodes **nothing
  game-specific** — a guess in the matrix is worse than a known blank.

> Because cores match the base, do not read their rows below as claims about those games. `bo2`
> shipped `.csc` and hash strings in reality, but its **core** has neither set — that is unfilled
> work, not a statement that BO2 lacks them.

---

## Keywords are data

The heart of the model. A profile carries a `Keywords` set (an `ImmutableArray<string>`), and a word
is a keyword **only if it is in that set** — otherwise the lexer leaves it an ordinary identifier, so
a script may use it as a name. The set is always built as `[.. BaseKeywords, … additions]`: the base
every CoD GSC dialect shares, plus that dialect's own additions on top.

The presence flags derive from the set rather than being set independently, so they can never drift
from what the lexer actually does:

```
HasClasses         => Keywords contains "class"
HasFunctionKeyword => Keywords contains "function"
HasForeach         => Keywords contains "foreach"
HasDoWhile         => Keywords contains "do"
```

### `BaseKeywords` — the true base (CoD4 / WaW / BO1, and every core)

```
if  else  for  while  switch  case  default  break  continue  return
thread  wait  waittill  waittillmatch  waittillframeend  notify  endon  isdefined
assert  assertmsg  true  false  undefined
```

That is the whole base. Note what is **not** here: `foreach`/`in`, `do`, `const`, `function`, the
class words, `childthread`/`call`, the profiler pair, and the BO3 intrinsics (`waitrealtime`,
`vectorscale`, `profilestart`, `profilestop`) — each is an addition made by a specific game.

`waittillmatch` IS in the base: it is used in every game from CoD4 to BO3 (205/218/245/57 uses in
CoD4/WaW/MW2/BO1). It was briefly mistaken for a missing builtin by the corpus harvest, which is
what a language feature looks like when it is absent from the keyword set.

### `ClassKeywords` — the class system, added as one group

```
class  var  new  constructor  destructor
```

Kept together because they all arrive with BO3's class system and none exists without it; a dialect
adds the whole feature with a single `.. ClassKeywords`. `autoexec`/`private` are **not** in this
group — they are function modifiers, so a dialect could have them without classes.

### Per-game keyword additions over the base

| game   | additions over `BaseKeywords` |
|--------|-------------------------------|
| cod4   | `prof_begin` `prof_end` |
| waw    | `prof_begin` `prof_end` |
| mw2    | `foreach` `in` · `childthread` `call` · `prof_begin` `prof_end` |
| bo1    | `prof_begin` `prof_end` |
| bo3    | `foreach` `in` · `.. ClassKeywords` · `do` `function` `autoexec` `private` `const` · `waitrealtime` `vectorscale` `profilestart` `profilestop` |
| *cores*| *(none — base exactly)* |

`childthread` and `call` are their own token kinds (`TokenKind.ChildThread` / `TokenKind.Call`), not
aliases of `thread`: `childthread foo()` parses as a threaded call, `call [[ ptr ]]( … )` as a
synchronous function-pointer call. They are gated by the set, so in BO3 — whose corpus uses `call`
as an ordinary variable ~69× — the word stays an identifier, which is exactly what keeps BO3 lexing
byte-identical.

---

## Category matrix (the 5 supported games)

Columns are the supported games; a core would be an all-base column (same as `cod4` except it ships
no bundled data).

### Imports & function pointers

| axis | cod4 | waw | mw2 | bo1 | bo3 |
|------|:----:|:---:|:---:|:---:|:---:|
| import style (`ImportStyle`) | `#include` | `#include` | `#include` | `#include` | `#using` |
| inline path calls (`HasInlinePathCalls`) — `maps\mp\_utility::foo()` | ✓ | ✓ | ✓ | ✓ | ✗ |
| function pointer (`FunctionPointerStyle`) | `::` | `::` | `::` | `::` | `&` |
| `#namespace` directive (`HasNamespaceDirective`) | ✗ | ✗ | ✗ | ✗ | ✓ |

`::` = a bare qualified name **is** the pointer (`level.f = maps\mp\_utility::foo;`, no parens);
parentheses would call it. `&` = BO3 makes the pointer explicit (`level.f = &foo;` / `&ns::foo`), and
a bare `ns::foo` is always a call.

### Loops, classes & declarations (all derived from the keyword set)

| axis | cod4 | waw | mw2 | bo1 | bo3 |
|------|:----:|:---:|:---:|:---:|:---:|
| `foreach ( x in coll )` (`HasForeach`) | ✗ | ✗ | ✓ | ✗ | ✓ |
| `do { … } while ( … )` (`HasDoWhile`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| classes `class`/`new`/`->` (`HasClasses`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| `function` keyword on decls (`HasFunctionKeyword`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| `const` keyword | ✗ | ✗ | ✗ | ✗ | ✓ |
| `autoexec` / `private` modifiers | ✗ | ✗ | ✗ | ✗ | ✓ |
| `childthread` / `call` | ✗ | ✗ | ✓ | ✗ | ✗ |
| file-scope constants `CONST = 4;` (`HasFileScopeConstants`) | ✗ | ✗ | ✓ | ✗ | ✗ |

**`foreach` is a family fork, not a timeline.** It is the Infinity Ward line's MW2 (2009) addition;
the Treyarch line does **not** get it until BO3. So BO1 (2010) has none despite being newer than
MW2 — grepping the games confirms it (0 uses in CoD4/WaW/BO1, 2,523 in MW2). Modelling it as a flat
"MW2 onward" would wrongly hand it to BO1.

### World objects (`GlobalObjectNames`)

| object | cod4 | waw | mw2 | bo1 | bo3 |
|--------|:----:|:---:|:---:|:---:|:---:|
| `self` `level` `game` `anim` | ✓ | ✓ | ✓ | ✓ | ✓ |
| `world` (`HasWorldObject`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| `classes` (with the class system) | ✗ | ✗ | ✗ | ✗ | ✓ |

`world` was added in BO3 and is present in the Treyarch games from then on; the earlier games and
the whole Infinity Ward line have `self`/`level`/`game`/`anim` but no `world` (pre-BO3 `world` hits
in the corpus are strings and comments).

### Other language features

| axis | cod4 | waw | mw2 | bo1 | bo3 |
|------|:----:|:---:|:---:|:---:|:---:|
| client scripts `.csc` (`HasClientScripts`) | ✗ | ✓ | ✗ | ✓ | ✓ |
| headers `.gsh` / `#insert` (`HasHeaders`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| hash strings `#"…"` (`HasHashStrings`) | ✗ | ✗ | ✗ | ✓ | ✓ |
| `#precache( "type", … )` directive (`HasPrecacheDirective`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| arrays passed by reference (`ArraysPassedByReference`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| ScriptDoc style (`ScriptDocStyle`) | `///` | `///` | `///` | `///` | `/@ @/` |

`///` = the pre-BO3 form, `///ScriptDocBegin` / `///ScriptDocEnd` lines inside an ordinary `/* */`
comment (not `/# #/`, which is a dev block). BO3 uses `/@ … @/`. Hash strings and `.csc` are Treyarch
features — hence BO1 and BO3 have them and the Infinity Ward line has none. BO3 passes arrays **by
reference only**; earlier games copy by value, which changes aliasing analysis, not syntax.

### Directives

Directives are gated by capability flags, **not** by the keyword set. `#include` is the IW import;
`#using`/`#namespace`/`#insert`/`#precache` are BO3. `#define`, `#using_animtree`, `#animtree`, and
the `#if`/`#elif`/`#else`/`#endif` preprocessor family exist across the whole lineage and are never
gated.

---

## Root discovery (where the raw scripts live)

**Configuration first, derivation second, workspace-only last.** Two settings say where the game is
— `gscode.rawPath` naming its raw script folder and `gscode.modsPath` naming the folder its mods live
under — and either one, when set to a folder that exists, is used verbatim.

Whatever is left unset is **derived**, by walking up from each workspace folder looking for the
install's own layout. That covers the ordinary case with no configuration at all: a mod at
`<install>\mods\my_mod` finds both roots, and so does opening the install itself, or the raw folder,
or anything beneath either. A mod checked out at `C:\work\my_mod` finds nothing, falls back to
workspace-only mode, and needs the settings — which is exactly why they exist.

The layout is per-game, and this is the one path fact a profile carries:

| | raw | mods |
|---|---|---|
| BO3 | `<install>\share\raw` | `<install>\mods` |
| every earlier game | `<install>\raw` | `<install>\mods` |

Nothing is read from the **environment**, which is the part that never generalised. BO3 once resolved
itself from `%TA_TOOLS_PATH%`, but that variable is set by Treyarch's mod tools and CoD4, WaW, MW2 and
BO1 ship nothing equivalent — so it was one mechanism serving one game while the rest went without.
Deriving from the workspace instead gives all five the same zero-configuration path, and keeps
**where you are editing** separable from **where the game is** whenever they genuinely differ.
`TA_TOOLS_PATH` still exists on a BO3 install; the extension simply does not read it.

The server log says which route each root took, since "why is it using *that* raw folder" is
otherwise unanswerable:

```
Roots: raw=…\share\raw (derived), mods=…\mods (derived), workspace folders=1
```

## Bundled data files (`DataFilePrefix`)

A profile with a `DataFilePrefix` ships bundled data — the builtin API, object fields, radiant keys,
and stock-script list — named from the prefix (`<prefix>_api_gsc.json`, `<prefix>_object_fields.json`,
`<prefix>_radiant_keys.json`, `<prefix>_stock_scripts.txt`). The client API is listed only when the
game has `.csc`.

| game | `DataFilePrefix` | complete? | ships |
|------|:----------------:|:---------:|-------|
| cod4 | `cod4` | ✓ | 819 functions (792 documented, 19 reconstructed), 297 radiant keys, 108 fields, 894 stock scripts |
| bo3  | `t7`   | ✓ | the full T7 set, including `t7_api_csc.json` |
| waw  | `waw`  | ✗ | 891 functions (781 inherited from CoD4, 110 its own), 360 radiant keys (11 client-only), 105 fields |
| bo1  | `bo1`  | ✗ | 751 functions (all inherited from CoD4), 466 radiant keys (126 client-only), 108 fields |
| mw2 / all cores | *(null)* | — | nothing; a workspace on that game loads no builtin data rather than another game's |

**`HasCompleteBuiltinLibrary` is a separate claim from `Verified`.** Verified is about the DIALECT —
proven against the game's own scripts. Completeness is about the FUNCTION LIST, and WaW's and BO1's
come from a mod-tools wordfile that is demonstrably partial: sweeping BO1's own scripts against it
finds 620 names it lacks, because its wordfile is the CoD4-era list carried forward unchanged. Those
libraries are therefore used for completion, hover and signature help, but never to claim a name is
NOT an engine function — `BuiltinFunctionNotFound` stands down for them.

Radiant keys carry a side (`both` or `client`). BO3 marks client keys with a `client` prefix inside
one `keys.txt`; WaW and BO1 instead split them across `keys.txt` and `clientkeys.txt`. Either way,
completion and hover offer client-side keys to `.csc` only.

---

## Verification evidence

`Verified` is earned, not asserted. The bar, enforced by `GameCorpusTests` (and `CorpusTests` for
BO3):

1. **No crashes** — every script in the game's tree analyses without throwing.
2. **Parse budget** — lex/parse errors under 1% of files.
3. **Formatter round-trip** — over a 250-file sample per game, reflow changes no token, and a second
   format is a fixed point.

Measured over the games' own script trees:

| game | scripts | crashes | lex/parse failures | formatter sample |
|------|--------:|--------:|-------------------:|-----------------:|
| cod4 |     894 |       0 |      2 (0.22%)     | 250 clean |
| waw  |   1,632 |       0 |      4 (0.25%)     | 250 clean |
| mw2  |   1,479 |       0 |      7 (0.47%)     | 250 clean |
| bo1  |   1,337 |       0 |      1 (0.07%)     | 250 clean |
| bo3  |     980 |       0 |      2 (0.20%)     | 250 clean |

Every remaining failure was inspected and is a **genuinely malformed file** that no compiler would
accept either — not a grammar gap:

- `animscripts\traverse\stairs_up.gsc` / `stairs_down.gsc` (cod4, mw2) — statements missing their
  terminating `;`.
- `maps\ber1_amb.gsc` (waw) — an unterminated `/*`; `maps\_ai_supplements.gsc` (waw) — a stray `*/`
  left by nested block comments.
- `maps\mp\mp_airfield_amb.gsc`, `mp_kwai_amb.gsc` (waw) — `include maps\mp\_utility;` written
  without the leading `#`.
- `xmodelalias\alias_*.gsc` (mw2) — autogenerated with `.` characters where newlines belong.
- `maps\mp\gametypes\_clientids.gsc`, `_hardpoints.gsc`, `maps\mp\_menus.gsc` (mw2) — valid content
  followed by a NUL padding byte (`_hardpoints.gsc` is a single NUL).
- `maps\_menus.gsc` (bo1) — an unclosed block.

### Configuring the corpora

Every game points at its own script root through one environment variable, named for the game and
set to the raw folder itself. No machine-specific path is committed, and an absent corpus is a no-op
rather than a failure, so the suite stays runnable for anyone without the tools:

```
GSCODE_CORPUS_COD4   …\CoD4-Mod-Tools\raw
GSCODE_CORPUS_WAW    …\cod5-mod-tools\raw
GSCODE_CORPUS_MW2    …\IW4
GSCODE_CORPUS_BO1    …\Call of Duty Black Ops 42740\raw
GSCODE_CORPUS_BO3    …\Call of Duty Black Ops III\share\raw
```

BO3 used to be the exception here too, located from `%TA_TOOLS_PATH%` with `share\raw` appended by
the fixture. It now follows the same convention as the other four.

### Dialect gaps this exposed

The sweep paid for itself immediately — three parser bugs that only the pre-BO3 dialects hit:

- **Dev blocks could not hold functions.** `ParseDevBlockDeclarations` accepted only `function`/`class`
  declarations, so the Infinity Ward games' keyword-less `/# drawDebug() { … } #/` failed on the
  block's first function and took every later declaration with it. This alone was most of the
  failures in cod4, waw and mw2.
- **Anim references were an allowlist.** `%walk` was treated as an anim reference only after
  `= ( , : ? return`, so `if ( deathanim != %dying_crawl_death_v2 )` lexed `%` as modulo. The rule is
  now stated as its complement — `%` divides only when the token to its left can end an operand.
- **Keywords could not be field names.** `self.wait` / `spawner.Wait` was rejected, though a keyword
  is a perfectly good field name and nothing is ambiguous in member position.

All three are dialect-neutral fixes; BO3's corpus result was unchanged at 2/980 (byte-identical).

## Promoting a core

To turn a core into a supported profile:

1. Grep ~15 real scripts from that game's `maps/mp/gametypes/` folder to confirm each axis (scripts
   over wordfile).
2. Replace the `Core(…)` call with an explicit `new() { … }` in `SupportedProfiles.cs`, set
   `Supported = true`, and add its keyword additions and capability flags.
3. Point `GSCODE_CORPUS_<GAME>` at the game's script root and run the corpus gate. Fix what it finds.
4. Only when all three properties hold, set `Verified = true` and add the game's row to the
   [evidence table](#verification-evidence).

Add rows here for the new game and update the tables.
