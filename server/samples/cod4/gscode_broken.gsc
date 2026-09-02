// CoD4 (Infinity Ward GSC) — the lexer and parser errors.
//
// Kept away from gscode_lints.gsc for the reason that file explains: a syntax error puts the parser
// into recovery, several lints stand down entirely on a file the parser could not read, and
// everything below the error is judged against a tree that is missing pieces.
//
// This dialect has no preprocessor, so there is no 2000 range here beyond the one diagnostic that
// says exactly that — and that one lives in gscode_lints.gsc, because it does not break the parse.

// ---------------------------------------------------------------------------
// A keyword from a later dialect, in a position the parser has no production
// for. This is the other half of 5025: where `vectorscale( … )` in
// gscode_lints.gsc is a call the parser accepts and the rule simply explains, a
// `foreach` header is not something the CoD4 grammar can read at all.
//
// 5025 still names it — the rule works from the WORD, not from a well-formed
// call — but it arrives buried in a cascade of syntax errors, and that is the
// difference worth seeing. It is also what a user porting a BO3 script actually
// gets, and the reason the two cases live in two different files: this cascade
// would take the rest of gscode_lints.gsc down with it.
// ---------------------------------------------------------------------------

keyword_from_a_later_dialect()
{
	items = [];
	items[ 0 ] = "a";

	// expect 3000 — expected ')' but found 'in'
	// expect 3000 — and ';'
	// expect 3000 — and ';'
	// expect 3000 — and ';'
	// expect 3003 — and then no statement at all
	// expect 5025 — the one diagnostic in the pile that says what is actually wrong
	// expect 5016 — `item` and `in` both read as variables nothing assigns
	// expect 5016
	foreach ( item in items )
	{
		items[ 1 ] = item;
	}
	// expect 3001 — recovery never gets back in step, so the closing brace has nothing to close
}

// ---------------------------------------------------------------------------
// 3000 range proper.
// ---------------------------------------------------------------------------

missing_semicolon()
{
	// expect 3014 — a statement ends with a semicolon
	value = 1

	return value;
}

bad_assignment_target()
{
	// expect 3012 — a literal is not somewhere a value can be stored
	1 = 2;

	// expect 3013 — an assignment in a condition is almost always a typo for ==
	if ( value = 1 )
	{
		return value;
	}
}

missing_expression()
{
	// expect 3002 — nothing to the right of the operator
	return 1 +;
}

// ---------------------------------------------------------------------------
// 1000 range — the lexer, and the block that never closes. Last, because
// nothing after it is read as anything.
// ---------------------------------------------------------------------------

unterminated_string()
{
	// expect 1000 — no closing quote before the end of the line
	// expect 3014 — so the statement never ends either
	return "this string never ends;
}

unterminated_block()
// expect 3007 — this brace is never closed, and neither is the file
{
	// expect 5008 — the one lint that still has something to say down here
	value = 1;
