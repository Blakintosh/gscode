// MW2 (Infinity Ward GSC) — the lexer and parser errors.
//
// Kept away from gscode_lints.gsc for the reason that file explains: a syntax error puts the parser
// into recovery, several lints stand down entirely on a file the parser could not read, and
// everything below the error is judged against a tree that is missing pieces.
//
// This dialect has no preprocessor, so there is no 2000 range here beyond the one diagnostic that
// says exactly that — and that one lives in gscode_lints.gsc, because it does not break the parse.

// ---------------------------------------------------------------------------
// A keyword from a later dialect, in a position the parser has no production
// for.
//
// The CoD4 sample uses `foreach` here. MW2 is the game that ADDED `foreach`, so
// that example is correct code in this dialect — which is the point of keeping
// one sample per game rather than one per family. `do`/`while` is MW2's version
// of the same situation: it is BO3's addition on the Treyarch line and has no
// production here, so the loop below is read as a call to a function named `do`
// followed by wreckage.
//
// This is what a user porting a BO3 script actually gets, and the reason the
// case lives here rather than in gscode_lints.gsc: the cascade would take the
// rest of that file down with it.
// ---------------------------------------------------------------------------

keyword_from_a_later_dialect()
{
	count = 4;

	// Fewer errors than the CoD4 case, and that is worse rather than better: `do` reads as an
	// ordinary identifier, so the line parses as a bare expression that is missing its semicolon,
	// the block below it becomes an unrelated block, and the `while` becomes a loop with an empty
	// body. The script compiles to something, and it is not the loop that was written.
	// expect 3014 — `do` is a name here, so the statement wants a semicolon
	// expect 5016 — and it reads as a variable nothing ever assigned
	do
	{
		count--;
	}
	while ( count > 0 );

	return count;
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
