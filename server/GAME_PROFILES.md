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
thread  wait  waittill  waittillframeend  notify  endon  isdefined
assert  assertmsg  true  false  undefined
```

That is the whole base. Note what is **not** here: `foreach`/`in`, `do`, `const`, `function`, the
class words, `childthread`/`call`, and the BO3 intrinsics (`waittillmatch`, `waitrealtime`,
`vectorscale`, `profilestart`, `profilestop`) — each is an addition made by a specific game.

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
| cod4   | *(none — base exactly)* |
| waw    | *(none)* |
| mw2    | `foreach` `in` · `childthread` `call` |
| bo1    | *(none)* |
| bo3    | `foreach` `in` · `.. ClassKeywords` · `do` `function` `autoexec` `private` `const` · `waittillmatch` `waitrealtime` `vectorscale` `profilestart` `profilestop` |
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

Only BO3 has an install the extension can find on its own — the `TA_TOOLS_PATH` environment variable
plus the `share\raw` subfolder (and `mods`), all recorded on its profile. No other game ships that,
so **every non-BO3 game takes a user-defined raw path**: `gscode.rawPath` / `gscode.modsPath`, which
override the profile's env-var lookup. With none set, the workspace runs in workspace-only mode,
which is first-class and tested.

`RootEnvironmentVariable` / `RawSubfolder` / `ModsSubfolder` are therefore null on every profile but
BO3 — nothing hardcodes BO3's paths.

## Bundled data files (`DataFilePrefix`)

A profile with a `DataFilePrefix` ships bundled data — the builtin API, object fields, radiant keys,
and stock-script list — named from the prefix (`<prefix>_api_gsc.json`, `<prefix>_object_fields.json`,
`<prefix>_radiant_keys.json`, `<prefix>_stock_scripts.txt`). The client API is listed only when the
game has `.csc`.

| game | `DataFilePrefix` | ships data? |
|------|:----------------:|:-----------:|
| cod4 | `cod4` | ✓ (752 functions, 297 radiant keys, 108 fields, 894 stock scripts) |
| bo3  | `t7`   | ✓ (full T7 set, incl. `t7_api_csc.json`) |
| waw / mw2 / bo1 / all cores | *(null)* | ✗ — a workspace on that game loads no builtin data rather than BO3's |

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

BO3 is found through `%TA_TOOLS_PATH%\share\raw`. Each other game points at its own script root
through an environment variable, so no machine-specific path is committed and an absent corpus is a
no-op rather than a failure:

```
GSCODE_CORPUS_COD4   …\CoD4-Mod-Tools\raw
GSCODE_CORPUS_WAW    …\cod5-mod-tools\raw
GSCODE_CORPUS_MW2    …\IW4
GSCODE_CORPUS_BO1    …\Call of Duty Black Ops 42740\raw
```

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
