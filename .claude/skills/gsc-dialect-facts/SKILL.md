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

`attacker` and `amount` are **outputs** the engine fills in, not values being read. The first
argument is the event name and is a genuine read.

A rule that treats these as reads reports `other`, `attacker`, `damage` and `notetrack` across
half the codebase — that alone was 2,117 of the first 2,742 false positives the unassigned-variable
lint produced.

Parsed as a `CallNode` whose `Callee` is an `IdentifierNode` wrapping the **keyword token**, so the
token kind is what identifies it.

**`waittillmatch` does NOT bind, despite the name.** This skill said "same for waittillmatch" for a
while, and that sentence was itself the source of a bug: `AstSearch.IsWaittill` treated both
keywords identically, so `self waittillmatch( "single anim", matchname )` bound `matchname` as a
fake output instead of reading it. `waittillmatch`'s trailing argument is the value to MATCH
against the notify's own parameters — confirmed against the shipped scripts, where it is
overwhelmingly a string literal (`self waittillmatch( "stepanim", "gravity on" )` — hundreds of
call sites across cod4/waw/mw2), and a bind target can never be one. Its own doc string already
said "whose parameters match the given values" before the binding code disagreed with it.

## Subscripting an undefined variable CREATES it

```gsc
quotes[ quotes.size ] = "line one";
```

There is no declaration step. `a[ 0 ] = x` on an `a` that does not exist builds the array, and this
is how every array in the stock scripts is made. The base of an assignment target is therefore a
WRITE, however deeply subscripted — while the subscript expression itself is still a read.

## Structs and entities alias in EVERY game; only arrays fork

| kind | passed as | differs by dialect? |
|---|---|---|
| entity — a player, `ent = Spawn( "script_model" )` | reference | no |
| struct — `spawnstruct()`, BO3's `new Foo()` | reference | no |
| **array** | **reference on BO3, copied on every earlier game** | **yes** |
| int, float, bool, string, istring, hash, vector, undefined | value | no |

The trap is assuming the `ArraysPassedByReference` flag means references arrived in BO3. They did
not: structs and entities have always aliased, and `spawnstruct` appears in all five corpora (cod4
117 files, waw 173, mw2 190, bo1 363, bo3 177), so both kinds are everywhere in the same code.

What that means for a rule: a struct or entity parameter behaves the same in every dialect and
needs no thought, while an array parameter a callee MUTATES behaves differently between BO3 and the
earlier games. So the only question worth answering precisely is "is this an
array", and it has three answers rather than two — certainly, certainly not, and cannot tell. The
third is the one to escalate rather than guess, which is why `ScrValue` distinguishes `MustBe` from
`MayBe` instead of carrying a single confidence flag.

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

## BO3 file scope is an EXPRESSION position, not declarations-only

A macro invocation is a call, and BO3 invokes macros at column 0 — `REGISTER_SYSTEM( "aat",
&__init__, undefined )` appears 477 times in the shipped scripts, expanding (via
`scripts/shared/shared.gsh`) to `function autoexec __init__sytem__() { … }`. So the argument list of
that call is an expression sitting outside every function body: **510 function pointers and 467
`undefined`s** are written at file scope across the corpus.

Any rule that assumes "outside a body, only a declaration or a directive is legal" is wrong on those
977 sites. It is what made completion's file-scope list keywords-only, and what a legality filter
there would have kept hiding. Two consequences worth carrying: `undefined`, `true`, `false` and the
other expression atoms belong at file scope as much as `function` does, and a name completed there
takes no semicolon, since none of the 447 `REGISTER_SYSTEM` lines carries one.

The macro's own expansion is the only thing that decides whether it may stand alone there. Of BO3's
3,844 header macros exactly two are shaped like a top-level construct, so a shape test is not a
useful filter — the language does not stop anyone expanding any of them anywhere.

## Under `#include`, every same-named function shares one key

The merge dialects key a function as `(null, name)` — no namespace. CoD4's animscripts hold 1,230
`main()`s. Anything keyed by name must scope by REACHABILITY, and reachability includes path calls
(`maps\mp\_util::foo()`) which need no import at all.

Scope per REFERENCE, never per file: a path call names its file outright, and a bare name resolves
locally first. Filtering whole files was wrong twice.

## A function pointer is always SPELLED as one, and the spelling forks by dialect

```gsc
level.callback = &on_damage;          // BO3
level.callback = ::on_damage;         // the IW merge dialects
level.callback = maps\mp\_util::on_damage;
level.callback = handler;             // NOT a function pointer — reads a local
```

There is no form in which a bare identifier names a function. The sigil is the whole difference,
and a rule that treats `level.foo = bar` as binding `bar` the function jumps to whatever function
happens to share the local's name.

Measured over every `owner.field = …` write in two corpora, with the FLOW TYPER rather than the
syntax — so this covers `bar = &on_damage; level.cb = bar;`, the case that makes the naive rule
sound reasonable:

| | bo3 (980 files) | cod4 (894 files) |
|---|---|---|
| field writes with a value | 17,755 | 16,315 |
| `&foo` | 865 | 0 |
| `::foo` / `path\file::foo` | 0 | 185 |
| `new Foo()` | 9 | 0 |
| bare identifier | 2,301 | 1,426 |
| …of those, holding a function | **0** | **0** |
| every other shape | 14,580 | 14,704 |
| …of those, holding a function | **0** | **0** |

Not one of 3,727 bare-identifier writes holds a function. The top names written bare say why —
`weapon`, `self`, `player`, `angles`, `attacker`, `team`, `origin`, `node`, `value`, `textscale`.
Entity and struct data, which is what a field mostly is.

Note the two zeroes on the diagonal: bo3 has every `&` and no `::`, cod4 every `::` and no `&`.
Recognising only the BO3 spelling leaves every Infinity Ward game with no callback navigation at
all, while the suite stays green — `GameProfile` is the seam, and a corpus sweep over both families
is what catches it.

What this supports, and its limit: `FieldBinding` records the three sigil forms so
go-to-type-definition, go-to-implementation and both hierarchies can answer on a callback field.
It is deliberately syntactic, since the flow typer recovers nothing extra here (the two zero rows)
and typing an unopened file per request is not affordable.

## An undefined variable is not an error

Reading one yields `undefined` and the script runs on. So a mistake surfaces far from its cause,
which is what makes lints in this area valuable — and what makes a false positive so costly, since
there is no compiler to contradict it.

## `isdefined` is a KEYWORD, not a builtin

It is absent from the API library, so a rule consulting the library about it finds nothing. Several
other call-shaped keywords are the same: `notify`, `endon`, `waittill`, `assert`, `vectorscale`,
`prof_begin`/`prof_end`.

## A bare call resolves to a builtin first; a qualified or threaded one means the script

Builtins are the fallback after the current namespace only for a QUALIFIED call — `sys::` exists as
the explicit builtin form. A bare call whose name is both an engine function and a script function
resolves to the builtin first, whatever its case. Two shipped BO3 files show where the script one
is reached instead:

```gsc
// scripts\shared\exploder_shared.gsc   (#namespace exploder)
function earthquake()                                       // declared here, takes nothing
...
self thread exploder::earthquake();                         // its OWN: qualified
...
Earthquake( eq["magnitude"], eq["duration"], self.v["origin"], eq["radius"] );   // the ENGINE one: bare
```

```gsc
// scripts\zm\_zm.gsc
function spawnSpectator()                                   // declared here, takes nothing
...
self thread spawnSpectator();                               // its OWN, though BO3 also has
                                                            // SpawnSpectator( origin, angles )
```

The first is disambiguated by the namespace qualifier, not by the capital E. The second is a bare
name, but THREADED, and a builtin cannot be threaded — with builtin-first it would pass nothing
to a builtin that needs two arguments, and the file ships and works. So:

- bare call → the builtin, if one exists;
- qualified call (`ns::name`) → the script function; `sys::name` → the builtin;
- threaded call or function reference (`&name`) → the script function.

An earlier version of this entry read both files as "spelling decides", and a lint and a formatter
feature were built on it. Spelling is not the mechanism; check the call's shape.

General script-to-script resolution stays case-insensitive, as the rest of the codebase has it
(`FunctionSymbol.KeyName` is lowercase-canonical and matched ordinally).

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
