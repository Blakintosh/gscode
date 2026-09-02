// BO3 (Treyarch GSH) — the header that is deliberately not a script.
//
// This is the one file in the samples whose point is mostly what is NOT reported.
//
// A .gsh is analysed leniently, and `ParseResult.Analyze` is where that happens: `lenient` is set
// for the header world alone, and it drops the WHOLE 3000 range along with the 4000-range
// extraction diagnostics. The reason is that a header is a fragment. It is inserted into the middle
// of whichever file includes it, so its text does not have to parse as a standalone script and
// usually does not — a header ending mid-expression is normal, not broken.
//
// The lexer and the preprocessor are NOT lenient, and that is the line worth pinning. A header is
// exactly where macros are declared, so the preprocessor's complaints about them are the ones a
// user most needs, and dropping those with the rest would silence the only world that has them.
//
// The .csc world has no file like this and does not need one: `Lexer.Lex` and `Parser.Parse` take
// the profile and never the language, so a client script's 1000/2000/3000 output is byte-identical
// to a server script's. GSH is the only world the parse forks on.

// ---------------------------------------------------------------------------
// Still reported — the preprocessor.
// ---------------------------------------------------------------------------

#define GSCODE_BROKEN_TWICE 1
// expect 2017 — already defined above
#define GSCODE_BROKEN_TWICE 2

// expect 2018 — the same macro parameter name twice
#define GSCODE_BROKEN_DUPLICATE( value, value ) ( value )

// expect 2006 — #insert resolves like a path, and this one does not exist
#insert gscode_no_such_header.gsh;

// expect 2010 — an #endif with no #if above it
#endif

// ---------------------------------------------------------------------------
// NOT reported — the parser.
//
// Every line below is a 3000-range error in a .gsc and silent here, and none
// of them carries an `expect`. That absence IS the assertion: the test fails
// on any diagnostic a sample did not declare, so if leniency ever stopped
// applying to headers, this file is what says so.
//
// The absence was checked rather than assumed. The same text under the .gsc
// extension reports 3001 at the first line below and 3007 at the last — so
// this file is silent because of the world it is in, not because the text
// happens to be harmless.
// ---------------------------------------------------------------------------

// A dangling operator and a bare statement, where a declaration is due. Both
// collapse into one 3001 in a script, since recovery resynchronises at the next
// declaration and there is not one.
1 +

value = 1;

// A block that never closes — which is exactly what a header carrying the top
// half of something looks like, and the shape leniency exists for. 3007 in a
// script.
function fragment()
{
