// BO3 (Treyarch GSC) — the lexer, preprocessor and parser errors.
//
// Kept away from gscode_lints.gsc because these three ranges are not like the rest: a syntax error
// puts the parser into recovery, and every construct below it is analysed against a tree that is
// missing pieces. Several lints stand down entirely on a file the parser could not read. Mixed
// together, most of the 5000 range would still look "tested" and would be proving nothing.
//
// So this file is deliberately unsalvageable, and the only thing asserted about it is which
// diagnostics it produces. Nothing imports it and nothing calls into it.

// ---------------------------------------------------------------------------
// 2000 range — the preprocessor.
// ---------------------------------------------------------------------------

// expect 2006 — #insert resolves like a path, and this one does not exist
#insert gscode_no_such_header.gsh;

// expect 2014 — #insert takes a header; this is a script
#insert gscode_target.gsc;

// expect 2003 — nothing to insert
#insert;

// Defined twice. The LATER definition is the one reported, because it is the one that silently
// replaces the other — the first definition is the one the author probably meant to keep.
#define GSCODE_TWICE 1
// expect 2017 — already defined above
#define GSCODE_TWICE 2

// expect 2018 — the same macro parameter name twice
#define GSCODE_DUPLICATE_PARAM( value, value ) ( value )

// expect 2010 — an #endif with no #if above it
#endif

// ---------------------------------------------------------------------------
// 3000 range — the parser.
// ---------------------------------------------------------------------------

function missing_semicolon()
{
	// expect 3014 — a statement ends with a semicolon
	value = 1

	return value;
}

function bad_assignment_target()
{
	// expect 3012 — a literal is not somewhere a value can be stored
	1 = 2;

	// expect 3013 — an assignment in a condition is almost always a typo for ==
	if ( value = 1 )
	{
		return value;
	}
}

function missing_expression()
{
	// expect 3002 — nothing to the right of the operator
	return 1 +;
}

// ---------------------------------------------------------------------------
// 1000 range — the lexer, and the unterminated blocks. Last on purpose: there
// is no recovering from a construct that never closes, so nothing after this
// point would be read as anything.
// ---------------------------------------------------------------------------

function unterminated_string()
{
	// A string that runs to the end of its line is two findings, not one: the string itself, and
	// the statement that consequently never reaches its semicolon.
	// expect 1000 — no closing quote before the end of the line
	// expect 3014 — so the statement never ends either
	return "this string never ends;
}

function unterminated_block()
// expect 3007 — this brace is never closed, and neither is the file
{
	// expect 5008 — the one lint that still has something to say down here
	value = 1;
