# samples — one worked example per game, per language world

Hand-written scripts that show the whole surface of a dialect and of the diagnostics, checked
against their own comments by `SampleScriptTests` in `GSCode.Server.Tests`. They are meant to be
OPENED: this is what you load to see hover, completion, go-to-definition, semantic tokens and every
rule firing on one page, and what a screenshot of the extension is taken from.

They are also the golden corpus for those things. A demo file with nothing asserting its output
stops being true within two releases and nobody notices, so the demo and the fixture are one
artifact rather than two that drift apart.

No game install is needed. Each game's folder IS its raw root, indexed through the real
`WorkspaceIndexer` over a `PhysicalFileSystem`, so `#using`/`#include`/`#insert` resolution and the
cross-file lints are exercised exactly as the editor exercises them.

## Layout

One folder per game, flat, and that folder IS the raw root. Every script therefore has a
single-segment path, and a directive reads `#include gscode_target` rather than
`#include maps\mp\gscode_target`.

| game | root | worlds sampled |
|---|---|---|
| `cod4` | `cod4/` | `.gsc` |
| `waw`  | `waw/`  | `.gsc` `.csc` |
| `mw2`  | `mw2/`  | `.gsc` |
| `bo1`  | `bo1/`  | `.gsc` `.csc` |
| `bo3`  | `bo3/`  | `.gsc` `.csc` `.gsh` |

The one thing flatness changes is how a PATH CALL is judged. `gscode_no_such_file::whatever()` has
a single segment, so it could name a file or a namespace, and the answer is 5013 — a function no
script location declares. A multi-segment `maps\mp\gscode_no_such_file::whatever()` is
unambiguously a path and is reported against the path instead, as 5009. The pre-BO3 lints files
carry a note at that line; it is the only place the layout is visible in a diagnostic.

Which worlds a game owes is not a list kept here — it is `GameProfile.ScriptExtensions`, and
`EveryLanguageWorldTheGameHasIsSampled` reads the profile rather than this table. A game that gains
`HasClientScripts` fails that test until its `.csc` showcase exists, and a `.csc` under a game
without the flag fails it from the other side.

## The four roles

Errors and demonstration fight each other, so they are separated rather than interleaved.

| file | job |
|---|---|
| `gscode.*` | the showcase. Produces ZERO diagnostics, so it stays the file where "does hover still work" is a fair question |
| `gscode_lints.*` | one deliberate 4000/5000-range finding at a time. Still parses cleanly, so every rule is judged against a complete tree |
| `gscode_broken.*` | the 1000/2000/3000 ranges |
| `gscode_target.*`, `gscode_unused.*`, `gscode_unresolved.gsc` | the second and third files the cross-file rules need in order to have an opinion at all |

`gscode_unresolved.gsc` exists in every game and is always alone with its broken import, for the
reason in the last section.

`gscode_broken` is kept apart because a syntax error puts the parser into recovery, and several
lints stand down entirely on a file the parser could not read — `ExpressionStatementLint` says so in
its own summary. Mixed together, most of the 5000 range would still look tested and would be proving
nothing.

BO3 is the only game with a `.gsh`. Every game with client scripts has a full `.csc` set —
showcase, lints, target and unused — but WaW's and BO1's client lints files pin only the
world-agnostic rules, and leave the calls that need a trusted client library in place with no
`expect`.

**There is no `gscode_broken.csc`, and there IS a `gscode_broken.gsh`.** The two follow from one
line, `lenient` in `ParseResult.Analyze`:

- `Lexer.Lex` and `Syntax.Parser.Parse` take the PROFILE and never the language, so a client
  script's 1000/2000/3000 output is byte-identical to a server script's. A `.csc` copy of the broken
  file would run the same code over the same input and assert the same thing twice.
- `.gsh` is the one world the parse forks on. `lenient` is set for headers alone and drops the whole
  3000 range together with the 4000-range extraction diagnostics, because a header is a FRAGMENT —
  it is inserted into the middle of another file and does not have to parse standalone. The lexer
  and preprocessor stay strict, which matters most there, since a header is where macros are
  declared.

`bo3/gscode_broken.gsh` pins both halves: four preprocessor diagnostics it must still report, and
three parser errors it must not. The second half is an ABSENCE, and absences are the assertions that
rot quietly — so it was checked rather than assumed. The same text under a `.gsc` extension reports
3001 and 3007, which is what makes the silence evidence about the world rather than about the text.

## Writing an expectation

The specification lives in the sample, one line above the code that produces it.

```gsc
// expect 5008 — assigned and never read
unused_thing = 4;
```

A run of consecutive `expect` comments all anchor to the first line below them that is not one,
which is how a line carrying two diagnostics is written. Ordinary prose comments may sit inside the
run. `expect-anywhere` drops the line requirement, for a diagnostic with no line to stand above.

Only the CODE is asserted. Severity and message are edited far more often than a rule's identity,
and pinning them would turn a wording change into a failing suite.

The test asserts BOTH directions. A missing diagnostic is a rule that regressed; an EXTRA one is the
more valuable half, since the showcase files expect nothing at all and any finding there is a false
positive caught on code written to be correct.

## Adding a rule to the samples

Put the case in the game where it can actually fire, and say in the file why that is the game.
Several rules cannot be shown everywhere:

- **5026 and 5012** are the `#include` model's, and cannot exist under `#using`. They live in the
  pre-BO3 samples; BO3's answer to the same mistake is 5000.
- **4000/4001/4006, 4002/4003, 5021, and the whole 2000 range** need `#precache`, classes and the
  preprocessor, so they are BO3's alone. CoD4's file pins 2016 instead — the diagnostic that says
  the preprocessor is not in this dialect.
- **5013/5014/5019/5023/5025** stand down on WaW, MW2 and BO1, whose bundled libraries are not
  marked complete or signature-reliable. Their lints files carry a gate note and leave the calls in
  place with no `expect`, so that curating one of those libraries FAILS the sample and asks for it
  to be updated. That is the only reminder a gate like this ever gets.
- **5006** needs a builtin the data marks dev-only. CoD4's library carries an explicit
  `"devOnly": false` on every entry, which beats the fallback list in `DevOnlyBuiltins`, so only
  BO3 can pin it today.

One rule cannot share a file with the others: an unresolvable import stands down the rule that
claims nothing imported declares a name, so **5009** lives alone in each game's
`gscode_unresolved.gsc`. Moved back into `gscode_lints`, it would silently delete **5000** there
(**5026** on the pre-BO3 games) and the suite would still be green.

That list used to include 5001 and 5012, and no longer does. An unreadable import cannot change
whether a DIFFERENT import is used — that answer is drawn from this file's references and from that
import's own declarations — so the broken directive goes unjudged and its siblings are judged. Only
the "nothing declares this" rules still need the whole pass, because an unreadable file is exactly
the counterexample to them.

`bo3/gscode_lints.gsc` also carries the macro form of 5000: a `#define` whose body reaches into
another namespace, invoked far below. Nothing on the invocation line spells the namespace, the
import is required all the same, and the Error lands on the macro's name because that is the only
text on screen. Every call rule in the 5000 range judges an expansion that way.
