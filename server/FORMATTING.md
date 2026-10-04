# GSC / CSC / GSH formatting guideline

What `Format Document` produces, and why each rule is what it is.

Every rule below was derived by measuring the shipped Treyarch scripts — 980 files, 397,111 lines
in BO3's raw folder — not by preference. Where the corpus is decisive the counts are
given and there is no setting. Where it is genuinely split, that is called out and the choice is
explained.

The formatter is **whitespace-only**, with one deliberate exception. It never inserts or deletes
code, and a token-stream equality gate rejects any output whose tokens differ from the input's. The
exception is directive sorting (§5), which moves lines by design and therefore runs outside that
gate with a safety check of its own.

---

## 1. Settled rules — the corpus decided

Most of these have no setting. The padding rows are the defaults of settings listed in §8, for
readers who share the corpus's majority but not every one of its habits.

| Rule | Corpus evidence |
|---|---|
| Line endings are the document's own — LF in stock, CRLF kept in a CRLF checkout | 396,131 LF, **0** CRLF |
| Indent with **tabs** | 247,613 tab-led indented lines vs 886 space-led |
| **Allman** braces — `{` on its own line | 50,485 own-line vs 36 same-line |
| `else` starts its own line, never `} else` | 7 cuddled in the entire corpus |
| No blank line immediately after `{` | 50,734 code vs 314 blank |
| One statement per line | 65 violations in 397,111 lines |
| A control-flow header split across lines keeps its breaks, and each continuation line aligns under the first character inside its `( `, in spaces after the header line's tabs | the shape stock writes long `&&` chains in, e.g. `util_shared.csc` |
| Outside a header, a line continuing an open `(` or `[` indents **one level** past the statement, however many are open | 438 indented vs 16 flush; the shape splits, see below |
| Spaces around assignment: `a = b` | 48,974 spaced vs 1,870 tight |
| A ternary's and a base class's `:` are spaced: `a ? b : c`, `class Foo : Bar` | 124 vs 7; 12 vs 0 |
| A `case` or `default` label is a line of its own, `:` tight | 2,453 alone vs 63 followed by a statement |
| A space after every comma: `f( a, b )` | 71,606 vs 4,180 |
| Call parentheses are **padded**: `foo( x )` (`padCallParens`) | 88,126 vs 14,274; and 473 files are internally consistent against 14 |
| Empty parentheses stay tight: `foo()` | 18,762 |
| Bracket interiors are **padded**: `a[ i ]` (`padBrackets`) | overridden — stock prefers tight 19,175 to 4,686 |
| A function pointer's `[[`/`]]` stay tight around a padded interior: `[[ ptr ]]` | overridden — stock prefers `[[ptr]]` 1,176 to 546 |
| A caller is set off from a pointer call: `self [[ ptr ]]()` — nested subscripts stay padded: `a[ b[ c ] ]` | 735 spaced vs 2 tight |
| A `\` that continues a directive is set off, and the line it continues onto indents one level | |
| One blank line between functions | 10,775 vs 1,490 |
| Trailing whitespace is stripped | stock carries it on 40,126 lines — 10% of the corpus |
| No maximum line width; lines are never reflowed | stock has no discipline here: 10,044 lines exceed 100 columns, 5,102 exceed 120 |

Two of these are **deliberate overrides of the corpus**, and the only ones in this document.

Stock writes indexes tight (`a[i]`, 19,175 against 4,686) and function pointers fully tight
(`[[ptr]]`, 1,176 against 546). We pad both interiors instead — `foo( a[ i ] )`, `[[ ptr ]]` — so
that one rule covers every bracketing construct rather than an asymmetry nobody can remember the
direction of.

Continuation lines are indented in stock, but not one way: 207 sit exactly one level deeper and
most of the rest are aligned under the open parenthesis. One level wins because it is expressible
in tabs alone and does not move every continuation line when the callee is renamed.

Adjacent brackets stay tight, which is what keeps `[[` and `]]` reading as the single token they
are rather than as a nested index, and leaves an empty array as `[]`.

## 2. Control-flow keywords — stock never settled them

`if ( x )` beats `if( x )` 20,382 to 10,429 overall. But per file, **270 files mix both forms
internally** against 242 that are consistent. `while`, `foreach` and `switch` even lean tight in raw
counts. Treyarch never made this decision, so measurement cannot make it for us.

**Chosen: spaced by default.** It is the overall majority, and it agrees with the call-paren
padding that *is* settled.

```gsc
if ( isdefined( x ) )
while ( i < 10 )
for ( i = 0; i < 10; i++ )
foreach ( key, value in a )
switch ( v )
```

Because stock is split, both halves are settings: `gscode.format.spaceBeforeControlParen` for the
keyword-to-paren gap (`if(` against `if (`) and `gscode.format.padParens` for the interior
(`( x )` against `(x)`), so all four combinations are reachable. Whichever you choose, the
formatter applies it everywhere — mixing the two forms is how stock became inconsistent.

## 3. Dev blocks and `case` labels

Two indentation choices the corpus splits on. Each has a default and a setting for the other answer.

### Dev blocks keep their surroundings' indentation

By default a `/# … #/` block does **not** introduce an indent level; its contents sit at the same
level as the `/#`. Corpus: 316 flush against 194 indented — a genuine split, so
`gscode.format.indentDevBlocks` indents the body one level for those who want the other answer.

This matters more than the margin suggests. A dev block is a runtime switch, not a scope —
when dev script is off the engine jumps over it — so indenting its body implies a nesting that does
not exist. Nested dev blocks likewise add nothing.

```gsc
function flop()
{
	my_code_is_here();

	/#
	debug_only_call();

	/#
	nested_devblock_weirdness();
	#/
	#/
}
```

### `case` labels

`case` and `default` indent one level inside their `switch`, and the statements under a label one
level further. Corpus: 2,012 indented against 517 flush. The minority is common enough to be a style
rather than an accident, so `gscode.format.indentCaseLabels` puts the labels in the switch's own
column instead.

A case body written as a braced block takes no extra level: the braces supply it. The `{`, the `}`
and anything after them in that case, a `break;` included, sit in the label's column. Stock does
this 59 times against 47 with the block indented, and in every such case the `break;` sits beside
the braces. `gscode.format.indentCaseBlocks` is the other 47: the block indents inside its label like
any other case body, and its statements sit two levels in from `case`.

```gsc
switch ( type )
{
	case "plane":
	case "helicopter":
	{
		return true;
	}
	default:
		return false;
}
```

## 4. Blank lines

One blank line is the convention — between functions, and between logical groups inside one. A
blank line is mostly authored punctuation, so the formatter preserves runs up to
`gscode.format.maxBlankLines` (2 by default) and collapses anything longer to that.

Corpus: 65,720 single-blank runs, 2,477 doubles, 152 triples, 21 longer.

The one place it **inserts** a blank line is after a closed block: the statement following a `}` —
another loop, an `if`, the next function or class member — gets one, so consecutive blocks never
run into each other. Stock puts one there 15,940 times against 3,012. A do-while's block closes at
its tail's `;`, not at its `}`. What continues the same construct stays directly under the `}`:
`else`, the `while` of a do-while, the next `case` label, a closer, `#/`, and the `break` after a
braced case body, which stock writes directly after its `}`. `maxBlankLines = 0` still wins.

A body written without braces is closed by its `;` instead, with the same exceptions — stock puts
a blank line after one 3,145 times against 526. Nested unbraced headers are legal and stay as
written; they share that one `;`, so the chain gets one blank line after its statement:

```gsc
if ( a )
	if ( b )
		c();

d();
```

```gsc
for ( i = 0; i < 10; i++ )
{
	a[ i ] = i;
}

foreach ( key, value in a )
{
	println( key );
}

do
{
	i--;
}
while ( i > 0 );

done();
```

## 5. Directive block

The dominant order is `#using` → `#insert` → `#namespace` → `#define` → `#precache`, but 80+ files
interleave `#using` and `#insert` freely, so this is a convention rather than a rule.

**The formatter groups and sorts them** (`gscode.format.sortDirectives`, on). This is its one
operation that moves code rather than whitespace, so it is fenced on three sides:

- `#using` and `#precache` are **sorted** alphabetically. A using is a namespace import resolved by
  the linker and a precache is a registration; neither can observe the other's position.
- `#insert` and `#define` **keep their relative order**. An insert is textual — the file's contents
  are spliced in where it sits — so two inserts can disagree about a macro, and a define can be what
  a later one depends on.
- The pass **stands down entirely** if a `#define` appears before an `#insert`, the one arrangement
  where regrouping could lift an insert above a macro it needs. No stock script does this; a mod
  might.

It also runs a line-multiset check on its own output, so a line can be moved but never dropped,
duplicated or edited — and it applies to **Format Document only**, never to range or on-type
formatting, where hoisting the whole file's header from under a partial edit would be startling.
A backslash-continued line is compared joined to what physically follows it, blank or not: a blank
line after a `\` is not a separator but a semantic edit, and the multiset check would otherwise be
blind to the one whitespace change this pass can make that changes meaning.

A directive continued with a trailing backslash owns every line of its continuation and moves as
one unit. That matters for `#define`: the preprocessor ends a macro body at the first newline not
preceded by a backslash, so a blank line between the `\` and the line it continues would empty the
macro and leave its body as top-level code.

Comments travel with the directive beneath them. A comment run separated from the first directive
by a blank line is a file banner rather than an annotation, and stays above the block instead of
being carried into the middle of it. Only the leading block is touched; a `#precache` sitting
between two functions is someone's deliberate placement and stays there.

Worth it: 498 of the 980 stock scripts are not in canonical order, and the same 498 have unsorted
`#using` lines.

```gsc
#using scripts\codescripts\struct;
#using scripts\shared\util_shared;

#insert scripts\shared\shared.gsh;

#namespace foo;

#define BAR 0

#precache( "string", "TEAM_GATHER_TEAM_STEALTH_ENTER" );
```

## 6. Consecutive alignment

`gscode.format.alignConsecutive` (on) lines up the operators of a run of consecutive assignments,
so every `=` sits in one column, one space past the longest left-hand side. A compound operator's
`=` shares that column and its leading characters hang to the left of it; when the longest side is
itself compound, the column moves right to make room.

```gsc
level.wasp_enabled          = true;
level.wasp_round_count_blah = 1;      // longest LHS sets the column
level.wasp_round_count     += 1;      // '=' in the column, '+' one to its left
```

Like directive sorting, this is a deliberate override of the corpus — the stock scripts align
almost nothing (2 assignments in 397,111 lines) — so it is a setting, and it runs as a whitespace
post-pass: it adds spaces only between a left-hand side and its operator, never touches a token, and
is idempotent. It re-lexes the text rather than scanning it, so a `=` inside a string, comment, or
`for` header is never mistaken for an operator.

Grouping: a blank line or a statement of a different kind ends a run; a comment on its own line is
transparent, and the assignments above and below it align together. A run of one is left at ordinary
single spacing. Runs are per indentation level, so a nested block aligns within itself.

Alignment is capped by `gscode.format.alignMaxPadding` (20). Caching a value and then writing a
deeply subscripted one would otherwise push the short name's `=` ninety columns out. When a run's
left-hand sides are further apart than the cap, the outlier — whichever of the widest and narrowest
is further from the rest — keeps a single space, and the others still align. Subscript and argument
columns that would need more than the cap are left as written. `0` removes the limit.

```gsc
nextID = level.releasedObjectives[ localClientNum ][ level.releasedObjectives[ localClientNum ].size - 1 ];
level.releasedObjectives[ localClientNum ][ level.releasedObjectives[ localClientNum ].size - 1 ] = undefined;
```

The same setting also aligns the **interior of subscripts and call arguments** when a run of
statements shares the same shape — the same base or callee, the same delimiters, the same arity:

```gsc
foo [ "bar"          ][ "key"   ] = "other";
bash[ "somethingelse" ][ "other" ] = "value";

register( "toplayer", PARASITE_ROUND_RING_FX  , VERSION_SHIP, 1, "counter" );
register( "world"   , "toggle_on_parasite_fog", VERSION_SHIP, 2, "int" );
```

It is one engine for both. Two lines share a group when their token *skeleton* is identical — the
same delimiters in the same order — and only the values in the slots differ. A slot followed by `]`,
`,` or `[` is aligned to its column's widest; a slot followed by `(` is an anchor (the **callee**,
which must match, so different functions do not align); a slot followed by `)`, `;` or an assignment
operator is free — which is why the **last argument and the right-hand side keep their natural
width**. The base of a subscript is aligned too, so `foo[ … ]` and `bash[ … ]` line up their `[`
even though the names differ. Subscript padding equalises the left-hand sides, so the operator then
lines up on top of it.

It applies to **all three** formatting requests. Directive sorting is the one that is Format Document
only, and the two are easy to confuse: both fragment handlers switch off `SortDirectives` and leave
alignment on, because hoisting a file's directive block out from under a partial edit would be
startling while re-aligning the group you are typing into is the point. On-type edits are then
clipped to the alignment group around the cursor, and range edits to the selection, so nothing
outside it moves.

## 7. Worked example

Everything above, applied:

```gsc
#using scripts\codescripts\struct;
#insert scripts\shared\shared.gsh;

#namespace foo;

#define BAR 0
#define BAZ( _x ) _x

#precache( "string", "TEAM_GATHER_TEAM_STEALTH_ENTER" );

class Boo
{
	var far;

	constructor()
	{
		far = 1;
	}

	destructor()
	{
	}

	function faz( value = 0 )
	{
		far = value;
	}
}

class Faz : Boo
{
	var far2;

	constructor()
	{
		far2 = 2;
	}

	function faz( value1 = 1, value2 = 2 )
	{
		Boo::faz( value1 );
		far2 = value2;
	}
}

function flop()
{
	boo_object = new Boo();
	[[boo_object]]->faz();

	a = [];
	for ( i = 0; i < 10; i++ )
	{
		a[ i ] = i;
	}

	foreach ( key, value in a )
	{
		println( "key is " + key + " and value is " + value + "\n" );
	}

	v = 1;
	switch ( v )
	{
		case 0:
			v2 = "0";
			break;

		default:
			v2 = "default";
			break;
	}
}
```

## 8. Settings

| Setting | Default | Effect |
|---|---|---|
| `editor.insertSpaces` | `false` for gsc/csc/gsh | Tabs. Arrives per request in the LSP payload |
| `editor.tabSize` | `4` | Columns per level; only meaningful when indenting with spaces |
| `gscode.format.padParens` | `true` | `if ( x )` against `if (x)` — the interior of control-flow and grouping parentheses |
| `gscode.format.padCallParens` | `true` | `foo( a )` against `foo(a)` — call and declaration parentheses. `()` stays tight |
| `gscode.format.padBrackets` | `true` | `a[ i ]` against `a[i]`. `[[ ptr ]]`'s outer brackets and `[]` stay tight |
| `gscode.format.spaceBeforeControlParen` | `true` | `if (` against `if(` — the keyword gap, not the interior (§2) |
| `gscode.format.maxBlankLines` | `2` | Longest run of blank lines preserved |
| `gscode.format.sortDirectives` | `true` | Group and sort the leading directive block. Format Document only |
| `gscode.format.alignConsecutive` | `true` | Align the operators of consecutive assignments. All three requests; on-type is clipped to the group around the cursor, range to the selection |
| `gscode.format.indentCaseLabels` | `true` | `case` labels one level inside their `switch` (§3) |
| `gscode.format.indentCaseBlocks` | `false` | Indent a braced case body inside its label (§3) |
| `gscode.format.indentDevBlocks` | `false` | Indent the body of a `/# … #/` dev block (§3) |
| `gscode.format.alignMaxPadding` | `20` | The most spaces alignment may add to one line; `0` for no limit (§6) |
| `gscode.format.fixCasing` | `true` | Lowercase keywords; give functions, namespaces and classes their declared spelling (§10) |

## 9. What the formatter will not do

- Reflow or wrap long lines. Stock has no width discipline and breaking a line changes how it reads.
- Reorder anything except the leading directive block, and that only under §5's conditions.
- Change the case of anything but keywords, function names, namespaces and class names (§10).
  Variables and fields keep the author's spelling, and macros are never recased.
- Touch the contents of strings, comments or `/@ @/` doc blocks.
- Emit output whose token stream differs from the input's, except by the casing fixes of §10. That
  gate is what makes the formatter safe to run on a 4,000-line stock file without reading the diff.

## 10. Casing

`gscode.format.fixCasing` (on) is the one setting that changes token text rather than whitespace.
GSC resolves keywords, functions, namespaces and classes case-insensitively, so it changes how code
reads, not what runs:

- Keywords are lowercased: `IsDefined( x )` becomes `isdefined( x )`, `WAIT` becomes `wait`.
- A function takes the spelling of what the call resolves to. A bare call resolves to a builtin
  before a script function of the same name, so it takes the builtin's documented spelling
  (`getplayers()` becomes `GetPlayers()`). A qualified call, a threaded call and a function
  reference (`&foo`) mean the script function, so they take its declaration's spelling, in this
  file or any other (`FOo()` becomes `foo()` when the script declares `function foo()`).
- A namespace takes the spelling of its `#namespace` directive (`Util::` becomes `util::`), and a
  class the spelling of its declaration, after `new`, before `::` and as a base class.
- A declaration's own name is the source of these spellings and is never changed. A name declared
  with two different spellings is left alone.

Stock shows the function rule in one file: `exploder_shared.gsc` declares `function earthquake()`,
calls the engine with a bare `Earthquake( … )`, and reaches its own function only qualified, as
`exploder::earthquake()`. `_zm.gsc` threads its own zero-argument `spawnSpectator()` although the
engine's `SpawnSpectator` takes two, which a builtin-first rule would reject — a threaded call
cannot mean a builtin.

**Macros match 1:1.** The preprocessor compares macro names exactly, so a macro use is never
recased and no fix may produce a macro's name; nothing inside a `#define` line is touched either.
A function-like macro is only a use where a `(` follows it. That is how stock's `DEFAULT( var, value )`
and the `default:` label live in one file: `DEFAULT( a, 1 )` stays the macro, and a `DEFAULT:` label
is the keyword and becomes `default:`.

The token gate checks the output against these fixes, and each may differ from the source only in
case.
