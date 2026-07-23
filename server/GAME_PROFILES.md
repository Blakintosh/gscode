# Game profile worksheet

Fill in what each game supports. Every row maps to a `GameProfile` shell in
`src/GSCode.Core/GameProfile.cs`. Only **bo3** is confirmed today; the rest are `?` until someone
checks them against real scripts. Replace a `?` with the value, and when a whole row is done say so
and it gets promoted to a real profile with `Verified = true`.

## Legend

- **✓ / ✗** — has it / does not
- **?** — unknown, fill in
- **import** — `ns` = `#using` imports a namespace, calls stay qualified (`ns::foo`) · `inc` =
  `#include` merges functions into the file, calls are unqualified
- **fptr** — how a function pointer is written: `&` = `&foo` / `&ns::foo` (BO3 style) · `::` = a bare
  qualified name is the pointer, `maps\mp\_utility::foo` with no parens (IW / pre-BO3 style)
- **arr-ref** — array parameters passed by reference (✓) or copied by value (✗)
- **csc** = client scripts (`.csc`) · **gsh** = headers (`.gsh` / `#insert`) · **class** =
  `class`/`new`/`->` · **func** = the `function` keyword on declarations · **#ns** = the
  `#namespace` directive

## Matrix

| game   | studio      | year | csc | gsh | class | func | #ns | import | fptr | arr-ref |
|--------|-------------|-----:|:---:|:---:|:-----:|:----:|:---:|:------:|:----:|:-------:|
| cod4   | InfinityWard| 2007 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| waw    | Treyarch    | 2008 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw2    | InfinityWard| 2009 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bo1    | Treyarch    | 2010 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw3    | InfinityWard| 2011 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bo2    | Treyarch    | 2012 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| ghosts | InfinityWard| 2013 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| aw     | Sledgehammer| 2014 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| **bo3**| Treyarch    | 2015 |  ✓  |  ✓  |   ✓   |  ✓   |  ✓  |  ns    |  &   |    ✓    |
| iw     | InfinityWard| 2016 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| wwii   | Sledgehammer| 2017 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bo4    | Treyarch    | 2018 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw19   | InfinityWard| 2019 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bocw   | Treyarch    | 2020 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| vg     | Sledgehammer| 2021 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw22   | InfinityWard| 2022 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| mw23   | Sledgehammer| 2023 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |
| bo6    | Treyarch    | 2024 |  ?  |  ?  |   ?   |  ?   |  ?  |   ?    |  ?   |    ?    |

## The two axes you called out

**Function pointer (`fptr`).** BO3 made a pointer explicit with `&`: `level.f = &foo;` or
`&namespace::foo;`, and a bare `ns::foo` is always a call. Before BO3 the qualified name itself was
the pointer — `level.f = maps\mp\_utility::foo;` with **no** parentheses — and `::foo` referenced a
function in the same file. This is modelled as `FunctionPointerStyle` (`Ampersand` vs
`PathQualified`) on the profile. It is a per-dialect default now; when the parser fork lands (D2) it
can also be surfaced as a user setting to override per workspace, since some codebases mix eras.

**Arrays (`arr-ref`).** BO3 passes arrays to functions **by reference only** — a callee that mutates
an array changes the caller's. Earlier games copy arrays by value. This is `ArraysPassedByReference`
on the profile; it affects analysis (aliasing), not syntax.

## Other differences to record

You mentioned there are more (directives especially). Add them here as you find them, and we will
grow the capability set to match. Candidates already on the radar for the IW family:

- `#include` vs `#using` (captured as `import` above)
- No `function` keyword; a declaration is `name( args ) { }` (captured as `func`)
- No `#namespace`; a file's namespace is its path (captured as `#ns`)
- `#using_animtree( "name" )` — present in both families, so probably not a difference
- Any game-specific directives not in the T7 set (`#precache` variants, etc.) — list them per game:

| game | directive / feature | notes |
|------|---------------------|-------|
|      |                     |       |
