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
| mw2    | `foreach` `in` · `childthread` `call` `thisthread` · `prof_begin` `prof_end` |
| bo1    | `prof_begin` `prof_end` |
| bo3    | `foreach` `in` · `.. ClassKeywords` · `do` `function` `autoexec` `private` `const` · `waitrealtime` `vectorscale` `profilestart` `profilestop` · `vararg` |
| *cores*| *(none — base exactly)* |

`childthread` and `call` are their own token kinds (`TokenKind.ChildThread` / `TokenKind.Call`), not
aliases of `thread`: `childthread foo()` parses as a threaded call, `call [[ ptr ]]( … )` as a
synchronous function-pointer call. They are gated by the set, so in BO3 — whose corpus uses `call`
as an ordinary variable ~69× — the word stays an identifier, which is exactly what keeps BO3 lexing
byte-identical.

`thisthread` is MW2's third addition there and a different shape again: it is the running thread as a
**value**, not a call modifier, so it parses as an identifier node wrapping the keyword token — the
`vararg` shape below. Grepping the games puts it in MW2 alone (5 uses in MW2, 0 in CoD4/WaW/BO1/BO3),
and every MW2 use reads it (`self.trackLoopThread = thisthread;`), so no shipped script uses the word
as a variable name for the keyword to take away.

---

## Category matrix (the 5 supported games)

Columns are the supported games; a core would be an all-base column (same as `cod4` except it ships
no bundled data).

### Imports & function pointers

| axis | cod4 | waw | mw2 | bo1 | bo3 |
|------|:----:|:---:|:---:|:---:|:---:|
| import style (`ImportStyle`) | `#include` | `#include` | `#include` | `#include` | `#using` |
| namespace-driven resolution (`ResolvesByNamespace`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| inline path calls (`HasInlinePathCalls`) — `maps\mp\_utility::foo()` | ✓ | ✓ | ✓ | ✓ | ✗ |
| function pointer (`FunctionPointerStyle`) | `::` | `::` | `::` | `::` | `&` |
| `#namespace` directive (`HasNamespaceDirective`) | ✗ | ✗ | ✗ | ✗ | ✓ |

`::` = a bare qualified name **is** the pointer (`level.f = maps\mp\_utility::foo;`, no parens);
parentheses would call it. `&` = BO3 makes the pointer explicit (`level.f = &foo;` / `&ns::foo`), and
a bare `ns::foo` is always a call.

**Import style and resolution are two claims, not one.** `ImportStyle` is purely lexical — whether
the directive is spelled `#using` or `#include` — and that is all the lexer, directive completion and
shape detection need. `ResolvesByNamespace` is the deeper question: whether a function's *identity*
carries its namespace. Under the merge model a file's functions join the caller's scope and are
reached by bare name, so the key drops the namespace; under the namespace model the call stays
qualified and two `main`s in different namespaces are two functions. What turns on the second one is
how a function is KEYED (`KeyNamespace`, and the extractor that builds the key), what completion may
offer bare, and which code actions apply. They coincide for every game today (a test asserts it), and
BO3 is the only game that is namespace-driven.

**A namespace does not pin a file, and scoping is not conditional on the dialect.** Go-to-definition
and reference narrowing used to skip BO3 entirely, on the theory that a namespace in the key already
made the answer unique. It does not: a namespace is shared freely across files, and the stock scripts
declare the same `#namespace` in an `mp` copy and a `zm` copy of the same script 565 times over (the
count is `AmbiguousFunctionLint`'s). So `globallogic_utils::get_time_remaining` names two
declarations, and only the asking file's `#using` list says which. Both dialect families therefore
narrow by the same rule — the file itself plus what it links against — and the only difference is
which directive spells "links against". `DatabaseQueries.LinkedScriptPaths` owns that one choice;
callers do not branch on the profile themselves.

A **class** name is the exception on BO3: it is global, named bare as `new Throttle()` or
`class Derived : Throttle`, with no `ns::Throttle` form — so a class key never carries a namespace on
either side, even there.

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
| `thisthread`, the running thread as a value | ✗ | ✗ | ✓ | ✗ | ✗ |
| file-scope constants `CONST = 4;` (`HasFileScopeConstants`) | ✗ | ✗ | ✓ | ✗ | ✗ |
| `...` parameter pack, read as `vararg` (`HasVarargBinding`) | ✗ | ✗ | ✗ | ✗ | ✓ |

`vararg` is a **keyword** on BO3 but appears in expression position — `foreach ( f in vararg )`,
`vararg.size` — so it parses as an identifier node wrapping the keyword token, the same shape the
callable keywords (`waittill`, `notify`) use. Being keyword-gated by the profile means a pre-BO3
script may still use `vararg` as an ordinary variable name.

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
| preprocessor: `#define`, `#if`/`#elif`/`#else`/`#endif` (`HasMacros`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| arrays passed by reference (`ArraysPassedByReference`) | ✗ | ✗ | ✗ | ✗ | ✓ |
| ScriptDoc style (`ScriptDocStyle`) | `///` | `///` | `///` | `///` | `/@ @/` |

`///` = the pre-BO3 form, `///ScriptDocBegin` / `///ScriptDocEnd` lines inside an ordinary `/* */`
comment (not `/# #/`, which is a dev block). BO3 uses `/@ … @/`. Hash strings and `.csc` are Treyarch
features — hence BO1 and BO3 have them and the Infinity Ward line has none. BO3 passes arrays **by
reference only**; earlier games copy by value, which changes aliasing analysis, not syntax.

`HasMacros` and `HasHeaders` coincide today and are still separate flags, because they are separate
claims: a header IS macros, but a dialect could define them in-file with nowhere to put them. What
the four earlier games have instead is `HasFileScopeConstants`, whose ALL_CAPS naming makes it look
convincingly like a macro. Measured before the flag was added: `#define` appears in exactly one file
per pre-BO3 game — always `maps/mp/gametypes/_hud.gsc`, inside a `/* */` block holding pasted C —
and the `#if` family in none of the four. A directive written against a game without the flag is
`gscode-2016`, raised by the preprocessor, which then processes it anyway; the reasoning for that is
on the code.

Note the animtree pair is genuinely universal and is not part of this: `#using_animtree` declares
the tree at file scope and `#animtree` names it inside a `UseAnimTree( … )` call, in all five games.

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
| cod4 | `cod4` | ✓ | 819 functions (791 documented, 20 reconstructed, 8 carrying neither flag), 297 radiant keys, 108 fields, 895 stock scripts |
| bo3  | `t7`   | ✓ | 2,191 functions + 803 client, including `t7_api_csc.json` |
| waw  | `waw`  | ✗ | 1,060 functions + 188 client functions, 360 radiant keys (11 client-only), 105 fields, 1,977 stock scripts |
| bo1  | `bo1`  | ✗ | 1,377 functions + 320 client functions, 466 radiant keys (126 client-only), 108 fields, 3,125 stock scripts |
| mw2  | `mw2`  | ✗ | 1,111 functions (CoD4's library plus 335 from its own corpus sweep), 367 radiant keys, 108 fields, 1,488 stock scripts |
| all cores | *(null)* | — | nothing; a workspace on that game loads no builtin data rather than another game's |

**These are the SHIPPED artifacts under `GSCode.Workspace/Api`, counted from them.** The curated
inputs under `tools/field-data/sources/curated/` state smaller figures for the same games — WaW's
client file records `serverLibrary: 891, kept: 154` and BO1's `751 / 156` — because the generator's
inheritance layer runs after them. Reading a count off the curated source is how this table came to
understate four of the five rows; count the artifact.

**MW2 is the game with no source at all.** No mod tools shipped, so there is no wordfile and no
documentation — only a `radiant/keys.txt`. Its names are CoD4's whole LIBRARY (not CoD4's wordfile:
the two differ, and taking the smaller one cost MW2 `abs` and produced 215 false include reports on
its own shipped scripts). The justification is a window rather than a lineage claim: CoD4's, WaW's
and BO1's wordfiles carry the same `CODSCRIPT /C7` list, so that list is the shared pre-BO3 Infinity
Ward one, and MW2 (2009) sits inside it.

That inference is then corrected by measurement. Sweeping MW2's own 1,479 shipped scripts found 335
engine functions CoD4 never knew; 91 of them are documented in Black Ops III's library and take its
entry, and the remaining 264 in the shipped file are RECONSTRUCTED from their call sites — parameter names are the
callers' own words, mandatory stops at the narrowest call seen, and a type is claimed only where the
spelling is the type. Those carry the `aiGenerated` flag. Reconstruction is safe here precisely
because MW2 does not set `HasReliableBuiltinSignatures`: `ArgumentCountLint` never judges a call
against a builtin signature for such a game, so a reconstruction reaches hover, completion and
signature help and can never become a diagnostic on someone's code.

**A game with no library of its own may borrow a sibling's NAMES** —
`EngineNameFallbackPrefix`, set only for MW2, which points at CoD4 one game earlier in the same
engine line. Names only, and the type enforces it: `BuiltinApiSet.EngineNamesFor` returns a
`FrozenSet<string>`, so a caller cannot render a borrowed signature by accident. `For()` still
returns this game's OWN library, empty when it ships none, because presenting a sibling's parameter
list as this game's would be a confident lie. MW2 keeps the setting now that it has a library of its
own, because that is what makes `HasTrustedEngineNames` true for it — the fallback is inert while
the library is non-empty, and the standing claim it encodes is still the one the include rule rests
on.

The distinction that makes borrowing safe is membership versus detail. A rule that must ask "could
this name be an engine function?" fails closed without an answer and therefore never runs at all on
MW2; a close sibling answers that question well enough, and being wrong costs silence — a name the
sibling has and this game does not is simply left unjudged.

Measured before it was set. With the gate lifted, MW2's 1,479 shipped scripts produce findings under
exactly one name; WaW and BO1 produce 204 and 387 under names their own libraries lack. So MW2
borrows and the other two stay gated: a second incomplete list does not add up to a trustworthy one.

`HasTrustedEngineNames` is the single predicate for "may a rule say a name is NOT an engine
function" — this game's library is complete, or it ships none and borrows. It exists because the
condition was once spelled three ways across two assemblies, and two of the three could disagree.

**`HasCompleteBuiltinLibrary` is a separate claim from `Verified`.** Verified is about the DIALECT —
proven against the game's own scripts. Completeness is about the FUNCTION LIST, and WaW's and BO1's
come from a mod-tools wordfile that is demonstrably partial: sweeping BO1's own scripts against it
found 529 names it lacked, because its wordfile is the CoD4-era list carried forward unchanged. Those
libraries are therefore used for completion, hover and signature help, but never to claim a name is
NOT an engine function — `BuiltinFunctionNotFound` stands down for them.

**Do not quote that 529 as current.** It is the figure `GameProfile.HasCompleteBuiltinLibrary`'s own
summary carries, and it was true when the flag was set; the library has grown since. The live count
is `tests/GSCode.Server.Tests/harvest/<game>_missing_builtins.json`, regenerated by
`BuiltinHarvestTests` on a corpus run — a number in prose here is a snapshot, and this paragraph
carried two different ones (620 and 529) at the same time before anybody noticed.

**`HasReliableBuiltinSignatures` is a third, narrower claim** — that the *parameters* on each entry
can be judged against, which is not implied by the name list being complete. BO3's come from a data
set built for the purpose; CoD4's are reconstructed from a wordfile plus documentation pages, and
WaW's and BO1's largely *inherit* CoD4's, making them a plausible signature for a related function
rather than a verified one for theirs. Measured rather than assumed: checking a call against the
mandatory count reported 4 findings across BO3's shipped scripts and 141 / 280 / 157 across CoD4's,
WaW's and BO1's, and `WrongBuiltinArgumentCount` is gated on it.

**Two games set it: BO3 and CoD4.** CoD4 did not at first — the 141 above is why — and it was
earned rather than granted: the 44 signatures behind those findings were corrected against the
documentation pages, which took CoD4 to ZERO across its 894 scripts, and the flag followed the
measurement. WaW's and BO1's still inherit, so they still do not set it. See the remark on
`SupportedProfiles.cs`'s CoD4 entry, which records the correction.

**A missing name is added on the inherited-sibling layer, cited both ways.** CoD4 lacked `Abs`;
WaW and BO1 lacked that plus `AddSpawnPoints`, `LookAtEntity`, `SetTeam`, `SetInvisibleToAll`,
`GetPerks` and `ClearSpawnPoints`. Each is documented by BO3, so the signature is carried from there
the way `Ceil` and `Floor` already were — and each remark also records the shipped-script evidence
that it is an engine function at all, since a carried signature says what it looks like, not that it
exists. `maps\mp\gametypes\_spawning.gsc` calls `AddSpawnPoints` while including only
`maps\mp\_utility` and `maps\mp\_geometry`, neither of which reaches a declaration, and the file
ships. That took WaW's unexplained names from 204 to 62 and BO1's from 387 to 6.

Add them BY HAND to both the curated source and the generated file. Regenerating without
`GSCODE_COD4_DOCS` set drops every documented signature CoD4 has, and a wholesale rewrite of the
curated file for a handful of entries destroys the reviewability of a build input.

Where the documentation is simply *wrong*, the correction goes in a curated override
(`tools/field-data/sources/curated/<prefix>_api_overrides.json`) that applies over every other layer,
because the generated api file is an artifact and an edit made there dies on the next run. WaW and
BO1 need no entries of their own — they inherit CoD4's corrected output.

Radiant keys carry a side (`both` or `client`). BO3 marks client keys with a `client` prefix inside
one `keys.txt`; WaW and BO1 instead split them across `keys.txt` and `clientkeys.txt`. Either way,
completion and hover offer client-side keys to `.csc` only.

### Stock-script lists come from the corpus plus what it references

`<prefix>_stock_scripts.txt` decides whether saving into the raw folder warns, so it has to tell a
file the game shipped from one the user wrote. `StockScriptListTests` generates it from any corpus
configured on the machine, using two sources: the extracted script tree, and every path an import or a
path-qualified call names that resolves to nothing on disk. The second is not a broken reference — the
game linked it, so the file shipped and this extraction is missing it, which is the same resolver
failure that drives `gscode-5009`.

That matters unevenly. CoD4 and WaW recover one file each; BO1 recovers 165, because its dump lacks
the WaW-era animscripts it inherited, several DLC map scripts (Silo, Golf Course, Moon), the frontend
client scripts and the model aliases. Being generous is the right error here: the list only decides
whether to warn before overwriting, so a wrong entry costs one warning while a missing one costs
silence on a stock file being clobbered.

### The client libraries are derived, except BO3's

Only `t7_api_csc.json` is a real source: Treyarch documented BO3's client VM, and it carries 513 names
that do not exist in `t7_api_gsc.json` at all. It is never generated or pruned.

WaW and BO1 have client scripts and no documentation for them, so until recently they shipped no
client API and every `.csc` file in those games loaded `BuiltinApi.Empty` — no hover, no signature
help, no completion. Theirs are now derived from their own server libraries by the field-data tool, in
two steps, both driven by evidence rather than assumption:

**Pruned to what is known to be client-side.** Most of a server library is not. A name is kept when
this game's own shipped `.csc` scripts call it, or when BO3's hand-documented client library lists it
— two independent kinds of evidence, and neither subsumes the other (28 names WaW's client scripts
call are absent from BO3's library; 66 BO3-backed names are never called in WaW's stock scripts). BO3
measures the cost of the stricter rule: restricted to names certainly client-side, its stock client
scripts exercise only 71.4% of them, so a corpus-only prune would discard about a third of the real
ones. WaW keeps 154 of 891, BO1 156 of 751 — the curated server library as it stood at the PRUNE, not
the shipped file, which the inheritance layer afterwards takes to 188 and 320.

**Corrected for the leading `localClientNum`.** Client scripts run one VM per splitscreen client, so a
client-side builtin acting on a particular screen takes the client index first — `VisionSetNaked( 0,
"vampire_low" )` against the server's `VisionSetNaked( "vampire_low" )`. `ClientArityHarvestTests`
finds these by measuring every `.csc` call against the server signature and reading the first argument
of the ones that overflow. Scored against BO3, where 224 entries already document the parameter, the
rule is right 31 times out of 31 with no false positives. Twelve names each on WaW and BO1.

**None of this is documentation-verified, and it cannot be.** A name being absent is not proof the
client VM lacks it, and a kept name is circumstantial. That is why `HasCompleteBuiltinLibrary` and
`HasReliableBuiltinSignatures` both stay false for these games: nothing derived here is ever allowed
to tell a user their code is wrong. The curated inputs
(`tools/field-data/sources/curated/<prefix>_csc_functions.json` and `<prefix>_csc_client_indexed.json`)
carry the same caveat and the per-name evidence.

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
| waw  |   1,976 |       0 |      8 (0.40%)     | 250 clean |
| mw2  |   1,479 |       0 |      7 (0.47%)     | 250 clean |
| bo1  |   2,960 |       0 |      2 (0.07%)     | 250 clean |
| bo3  |     980 |       0 |      2 (0.20%)     | 250 clean |

WaW and BO1 grew from 1,632 and 1,337 when the missing subfolders were merged in from the `t4`/`t5`
script sets. Their failure RATES moved with the file count rather than against it, which is the
point of recording a percentage as well as a count.

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
