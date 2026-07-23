# GSC / CSC / GSH formatting guideline

What `Format Document` produces, and why each rule is what it is.

Every rule below was derived by measuring the shipped Treyarch scripts — 980 files, 397,111 lines
under `%TA_TOOLS_PATH%\share\raw` — not by preference. Where the corpus is decisive the counts are
given and there is no setting. Where it is genuinely split, that is called out and the choice is
explained.

The formatter is **whitespace-only**, with one deliberate exception. It never inserts or deletes
code, and a token-stream equality gate rejects any output whose tokens differ from the input's. The
exception is directive sorting (§5), which moves lines by design and therefore runs outside that
gate with a safety check of its own.

---

## 1. Settled rules — no setting, the corpus decided

| Rule | Corpus evidence |
|---|---|
| Lines end with **LF** | 396,131 LF, **0** CRLF |
| Indent with **tabs** | 271,761 tab-indented lines vs 935 space-indented |
| **Allman** braces — `{` on its own line | 50,485 own-line vs 36 same-line |
| `else` starts its own line, never `} else` | 7 cuddled in the entire corpus |
| No blank line immediately after `{` | 50,734 code vs 314 blank |
| One statement per line | 65 violations in 397,111 lines |
| Spaces around assignment: `a = b` | 48,974 spaced vs 1,870 tight |
| A space after every comma: `f( a, b )` | 71,606 vs 4,180 |
| Call parentheses are **padded**: `foo( x )` | 88,126 vs 14,274; and 473 files are internally consistent against 14 |
| Empty parentheses stay tight: `foo()` | 18,762 |
| Bracket interiors are **padded**: `a[ i ]` | overridden — stock prefers tight 19,175 to 4,686 |
| A function pointer's `[[`/`]]` stay tight around a padded interior: `[[ ptr ]]` | overridden — stock prefers `[[ptr]]` 1,176 to 546 |
| `case` indents one level inside `switch` | 2,012 vs 517 |
| One blank line between functions | 10,775 vs 1,490 |
| Trailing whitespace is stripped | stock carries it on 40,126 lines — 10% of the corpus |
| No maximum line width; lines are never reflowed | stock has no discipline here: 10,044 lines exceed 100 columns, 5,102 exceed 120 |

Two of these are **deliberate overrides of the corpus**, and the only ones in this document.

Stock writes indexes tight (`a[i]`, 19,175 against 4,686) and function pointers fully tight
(`[[ptr]]`, 1,176 against 546). We pad both interiors instead — `foo( a[ i ] )`, `[[ ptr ]]` — so
that one rule covers every bracketing construct rather than an asymmetry nobody can remember the
direction of.

Adjacent brackets stay tight, which is what keeps `[[` and `]]` reading as the single token they
are rather than as a nested index, and leaves an empty array as `[]`.

## 2. Control-flow keywords — the one thing stock never settled

`if ( x )` beats `if( x )` 20,382 to 10,429 overall. But per file, **270 files mix both forms
internally** against 242 that are consistent. `while`, `foreach` and `switch` even lean tight in raw
counts. Treyarch never made this decision, so measurement cannot make it for us.

**Chosen: always spaced.** It is the overall majority, and it agrees with the call-paren padding
that *is* settled.

```gsc
if ( isdefined( x ) )
while ( i < 10 )
for ( i = 0; i < 10; i++ )
foreach ( key, value in a )
switch ( v )
```

The keyword-to-paren space is not configurable — mixing the two forms is how stock became
inconsistent in the first place. The *interior* padding is, via `gscode.format.padParens`.

## 3. Dev blocks keep their surroundings' indentation

A `/# … #/` block does **not** introduce an indent level; its contents sit at the same level as the
`/#`. Corpus: 316 flush against 194 indented.

This matters more than the margin suggests. A dev block is a compile-time switch, not a scope —
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

## 4. Blank lines

One blank line is the convention — between functions, and between logical groups inside one. The
formatter does not impose it, because a blank line is authored punctuation: it preserves runs up to
`gscode.format.maxBlankLines` (2 by default) and collapses anything longer to that.

Corpus: 65,720 single-blank runs, 2,477 doubles, 152 triples, 21 longer.

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

Comments travel with the directive beneath them. Only the leading block is touched; a `#precache`
sitting between two functions is someone's deliberate placement and stays there.

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
one space past the longest left-hand side. Compound operators start at that column and extend
rightward.

```gsc
level.wasp_enabled          = true;
level.wasp_round_count_blah = 1;      // longest LHS sets the column
level.wasp_round_count      += 1;     // '+' at the column, '=' one past
```

Like directive sorting, this is a deliberate override of the corpus — the stock scripts align
almost nothing (2 assignments in 397,111 lines) — so it is a setting, and it runs as a whitespace
post-pass: it adds spaces only between a left-hand side and its operator, never touches a token, and
is idempotent. It re-lexes the text rather than scanning it, so a `=` inside a string, comment, or
`for` header is never mistaken for an operator.

Grouping: a blank line or a statement of a different kind ends a run; a comment on its own line is
transparent, and the assignments above and below it align together. A run of one is left at ordinary
single spacing. Runs are per indentation level, so a nested block aligns within itself.

It applies to **Format Document only**, not range or on-type formatting — alignment is a property of
a group, not of the one line being edited.

**Not yet aligned:** the *interior* of subscripts and call arguments — `foo[ "a" ][ "bb" ]` lining
its `]` columns up, or consecutive `register( … )` calls lining their arguments up. That is the same
column-padding engine and is the next phase; today only the assignment operator itself is aligned.

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
| `gscode.format.padParens` | `true` | `if ( x )` against `if (x)` — the interior, not the keyword gap |
| `gscode.format.maxBlankLines` | `2` | Longest run of blank lines preserved |
| `gscode.format.sortDirectives` | `true` | Group and sort the leading directive block. Format Document only |
| `gscode.format.alignConsecutive` | `true` | Align the operators of consecutive assignments. Format Document only |

## 9. What the formatter will not do

- Reflow or wrap long lines. Stock has no width discipline and breaking a line changes how it reads.
- Reorder anything except the leading directive block, and that only under §5's conditions.
- Change identifier or keyword case. GSC is case-insensitive, so `Break` and `break` are the same
  token — but casing is the author's, and rewriting it would churn diffs for no semantic gain.
- Touch the contents of strings, comments or `/@ @/` doc blocks.
- Emit output whose token stream differs from the input's. That gate is what makes the formatter
  safe to run on a 4,000-line stock file without reading the diff.
