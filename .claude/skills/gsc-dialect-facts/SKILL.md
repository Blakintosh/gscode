---
name: gsc-dialect-facts
description: GSC language facts that are easy to get wrong and expensive when you do. Read before writing any rule about variables, arrays, scope, or what a name means — every entry here was learned by a lint or feature being wrong on scripts that ship and work.
---

# GSC facts that break naive rules

Every item here cost a wrong diagnostic on shipped code. They are the assumptions a reader brings
from other languages that GSC does not honour.

## `waittill` BINDS its trailing arguments

```gsc
self waittill( "damage", attacker, amount );
```

`attacker` and `amount` are **outputs** the engine fills in, not values being read. Same for
`waittillmatch`. The first argument is the event name and is a genuine read.

A rule that treats these as reads reports `other`, `attacker`, `damage` and `notetrack` across
half the codebase — that alone was 2,117 of the first 2,742 false positives the unassigned-variable
lint produced.

Parsed as a `CallNode` whose `Callee` is an `IdentifierNode` wrapping the **keyword token**, so the
token kind is what identifies it.

## Subscripting an undefined variable CREATES it

```gsc
quotes[ quotes.size ] = "line one";
```

There is no declaration step. `a[ 0 ] = x` on an `a` that does not exist builds the array, and this
is how every array in the stock scripts is made. The base of an assignment target is therefore a
WRITE, however deeply subscripted — while the subscript expression itself is still a read.

## The Infinity Ward dialects have FILE-SCOPE constants

```gsc
attack_heli()
{
}

BRIDGE_COLLAPSE_SPEED = 1.0;      // between declarations, at file scope

collapsed_section_shakes()
{
    wait 6 * BRIDGE_COLLAPSE_SPEED;
}
```

Readable from every function in the file. Modelled as `FileScopeConstantNode`, gated on
`GameProfile.HasFileScopeConstants`. **These are not macros** — and their ALL_CAPS naming makes them
look convincingly like one. A per-function rule that ignores them reported 755 in MW2's scripts alone.

## No game before BO3 has a preprocessor

`#define` and the `#if`/`#elif`/`#else`/`#endif` chain arrived with the compiler that also brought
`#insert`. Gated on `GameProfile.HasMacros`, which only BO3 sets; writing one against an earlier
game is `gscode-2016 MacrosNotInDialect`.

Measured over the shipped scripts, because the file-scope constants above make the opposite easy to
believe: `#define` appears in exactly one file per pre-BO3 game — always
`maps/mp/gametypes/_hud.gsc`, inside a `/* */` block holding C source somebody pasted in — and the
`#if` family in none of the four at all. BO3 has 369 and 4.

The rule REPORTS and then expands anyway, and the lexer is deliberately left ungated so it can:
skipping would model the game's compiler more faithfully but would punish the case this is most
likely to be wrong about, a custom compiler that does accept macros. As it stands, suppressing 2016
leaves a working file.

## `#animtree` is an expression, not a file-scope directive

`#using_animtree( "generic" );` declares the tree at file scope; `#animtree` NAMES it, and only ever
as an argument — `self UseAnimTree( #animtree );`. Both exist in all five games. Across the five
corpora `#animtree` appears in 415 files and **not once at the start of a line**, which is why it
belongs in `GscKeywords.BodyDirectives` and not `TopLevelKeywords`. It was in the latter, and a
line-anchored grep is what made it look unused everywhere — measure this one without `^`.

## Under `#include`, every same-named function shares one key

The merge dialects key a function as `(null, name)` — no namespace. CoD4's animscripts hold 1,230
`main()`s. Anything keyed by name must scope by REACHABILITY, and reachability includes path calls
(`maps\mp\_util::foo()`) which need no import at all.

Scope per REFERENCE, never per file: a path call names its file outright, and a bare name resolves
locally first. Filtering whole files was wrong twice.

## An undefined variable is not an error

Reading one yields `undefined` and the script runs on. So a mistake surfaces far from its cause,
which is what makes lints in this area valuable — and what makes a false positive so costly, since
there is no compiler to contradict it.

## `isdefined` is a KEYWORD, not a builtin

It is absent from the API library, so a rule consulting the library about it finds nothing. Several
other call-shaped keywords are the same: `notify`, `endon`, `waittill`, `assert`, `vectorscale`,
`prof_begin`/`prof_end`.

## A script function shadows a builtin only when SPELLED the same

Builtins are the fallback after the current namespace — `sys::` exists as an explicit alias
precisely because a script function otherwise wins. But whether a declaration shadows an engine
function of the same name is decided by the SPELLING, not case-insensitively, and two shipped BO3
files settle it in opposite directions:

```gsc
// scripts\shared\exploder_shared.gsc
function earthquake()                                       // declared here, takes nothing
...
Earthquake( eq["magnitude"], eq["duration"], self.v["origin"], eq["radius"] );   // the ENGINE one
```

```gsc
// scripts\zm\_zm.gsc
function spawnSpectator()                                   // declared here, takes nothing
...
self thread spawnSpectator();                               // its OWN, though BO3 also has
                                                            // SpawnSpectator( origin, angles )
```

Both files ship and work, and no case-insensitive rule explains both — it either breaks the first
(four arguments to a nought-parameter function) or the second (nought arguments where two are
mandatory). The authors clearly wrote the distinction on purpose.

Scope it to THIS tie-break. General script-to-script resolution stays case-insensitive, as the rest
of the codebase has it (`FunctionSymbol.KeyName` is lowercase-canonical and matched ordinally).

A case-insensitive first attempt at the arity rule reported that `Earthquake` call as passing four
arguments to a nought-parameter function — an Error on a file that ships. The corpus caught it; a
reviewer would not have.

## ScriptDoc has two spellings

BO3 uses `/@ … @/` with its own token kind. Every earlier game fences a block inside an ordinary
`/* … */` comment with `///ScriptDocBegin` / `///ScriptDocEnd`, and wraps it in rows of `=`.
`GameProfile.ScriptDocStyle` records which.

## Before writing any rule about names

Sweep the corpus and read the top reported names before choosing a severity. If they share a shape
— all ALL_CAPS, all parameter-like, all array-like — that shape is a language fact the rule has not
learned yet, not a defect rate in code that shipped and works.
