// BO1 (Treyarch GSC) — the deliberate findings.
//
// The same arrangement as the BO3 lints file: one mistake at a time, each declared by the
// `// expect <code>` comment above it, and the file still parses cleanly so every rule is judged
// against a complete tree.
//
// What is different is which rules can fire at all. Two whole families are missing here because the
// dialect has nothing for them to be about — no #precache, so no 4000/4001/4006; no classes, so no
// 5021 and no 4002/4003 — and two exist ONLY here, because they are about the include model that
// BO3 replaced. Those two are 5012 and 5026, and they are the reason this file is not a translation
// of the BO3 one.

// Included and never used. The `#include` twin of BO3's 5001.
// expect 5012 — nothing in this file calls anything it declares
#include gscode_unused;

// Note what is NOT included: gscode_target. That is what 5026 below rests on.

// ---------------------------------------------------------------------------
// The dialect boundary. These are not mistakes in another game.
// ---------------------------------------------------------------------------

// The preprocessor arrives with BO3. Reported once for the file rather than once per directive, and
// the macro still EXPANDS — telling the author the directive does not exist while silently dropping
// its effect would produce a second, invented error at every use.
// expect 2016 — #define is not in this dialect
#define NOT_IN_THIS_DIALECT 1

main()
{
	// `vectorscale` is one of BO3's intrinsics. Here it is an ordinary identifier, so this line
	// reads as a call to a function that does not exist.
	//
	// Before this rule it was reported as a missing ENGINE function, which sent people looking for
	// a builtin that never existed in any game. Naming the real problem — a word that is a keyword
	// in a LATER dialect — is the whole of what 5025 adds.
	scaled = vectorscale( ( 0, 0, 1 ), 64 );

	// A word that is a keyword in a later dialect is not always a call. `foreach` is Infinity Ward's
	// MW2 addition, and writing one here is a SYNTAX error rather than a diagnostic about the
	// dialect — the parser has no production for it, so there is nothing left to name. That case
	// lives in gscode_broken.gsc, where a cascade cannot take the rest of this file with it.

	return scaled;
}

// ---------------------------------------------------------------------------
// What is NOT reported here, and why it is our data rather than the dialect.
//
// Black Ops is a Supported game whose bundled engine library is not marked complete
// or signature-reliable — the wordfile it was built from lists names without
// arity, and the list itself is known to be short. Every rule that reasons
// about ENGINE functions therefore stands down on this game, so that the editor
// never blames a user for a hole in our data:
//
//   * 5025 — a word that is a keyword only in a later dialect. It reaches the
//     user through the same resolution path, so it is gated with the rest.
//   * 5026 — a name declared in a file this one does not include. A real engine
//     function absent from our data would otherwise be reported as a missing include.
//   * 5014 — a call matching no script function and no known engine function.
//   * 5023 — an engine call's argument count, which needs signatures to check.
//
// The calls below are left in place, uncommented and carrying no `expect`, so
// that the day Black Ops's library is curated this sample fails and asks to be
// brought up to date. That is the only reminder a gate like this ever gets.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// The include model.
// ---------------------------------------------------------------------------

calls_that_do_not_resolve()
{
	// Declared in gscode_target.gsc, which this file does not include. The analyser can
	// find it — hover and go-to-definition both work on this line — but the game's compiler will
	// not link the call, so the script does not load. That gap is what the rule exists for.
	target_never_included();

	// Nothing anywhere declares this, which is a different story and a different code.
	NoSuchEngineFunction();

	// A path call names its file outright, so it needs no #include.
	//
	// What it is reported AS depends on how many segments the path has, and the samples sit at the
	// boundary. A multi-segment path — maps\mp\gscode_no_such_file::whatever() — is unambiguously
	// a path, so a missing one is reported against the PATH, once for the file rather than once per
	// call. A single segment is not: with the samples rooted flat, `gscode_no_such_file` could be a
	// file or a namespace, and the call is judged as a function that no script location declares.
	//
	// Both answers are defensible and only one of them names the actual problem, so this line is
	// worth keeping an eye on.
	// expect 5013 — no script location declares it
	gscode_no_such_file::whatever();

	abs();
}

// ---------------------------------------------------------------------------
// Flow and locals — the same rules as every other dialect.
// ---------------------------------------------------------------------------

locals_and_flow()
{
	// expect 5008 — assigned, then never read
	unused_local = 4;

	// expect 5016 — read before anything assigns it
	total = never_assigned + 1;

	// expect 5031 — a division by a constant zero
	broken = total / 0;

	return broken;

	// expect 5015 — unreachable
	// expect 5008 — and unread
	unreachable = 1;
}

switch_labels( value )
{
	switch ( value )
	{
		case 1:
			break;
		// expect 5017 — the same label twice; the second can never be taken
		case 1:
			break;
		// expect 5011 — a case label has to be constant at compile time
		case value:
			break;
		default:
			break;
		// expect 5027 — a switch has one default
		default:
			break;
	}

	return value;
}

reads_and_writes()
{
	items = [];
	items[ 0 ] = "a";

	// expect 5005 — .size is computed by the engine
	items.size = 9;

	// expect 5035 — `level` is the engine's name
	// expect 5008 — and, taken as a local, it is written and never read
	level = items;

	// expect 5019 — ClearAllCorpses returns nothing, so this assigns undefined
	nothing = clearallcorpses();

	return nothing;
}

bindings_and_results()
{
	// expect 5020 — bound by waittill and never read
	level waittill( "custom_event", unused_binding );

	// expect 5028 — a threaded call returns immediately; there is no result to take
	result = thread waiting_function();

	// expect 5032 — the value is computed and dropped, so the line does nothing
	1 + 1;

	return result;
}

waiting_function()
{
	wait 0.05;
	return 1;
}

// ---------------------------------------------------------------------------
// Dev-only builtins — 5006 — cannot be shown on this game, and the reason is
// our data rather than the dialect.
//
// The rule reports an engine function that only exists in a development build
// being called from release code, and the call below is exactly that: Print3d
// is dev-only in every game that has it. It is not reported here because
// cod4_api_gsc.json carries an explicit "devOnly": false on every entry, and an
// explicit false beats the fallback list in DevOnlyBuiltins. The BO3 library
// marks two entries dev-only, so the BO3 lints file is where 5006 is pinned.
//
// Left here, uncommented and unexpected-free, so that the day BO1's data is
// curated this sample fails and asks to be updated.
// ---------------------------------------------------------------------------

dev_only_calls()
{
	print3d( ( 0, 0, 0 ), "text", ( 1, 1, 1 ), 1, 1 );

	/#
	print3d( ( 0, 0, 0 ), "text", ( 1, 1, 1 ), 1, 1 );
	#/
}

// ---------------------------------------------------------------------------
// Declarations.
// ---------------------------------------------------------------------------

// expect 4007 — the same parameter name twice
duplicate_parameters( value, value )
{
	return value;
}

// expect 4005 — this name is already declared, above
duplicate_parameters( other )
{
	return other;
}

// ---------------------------------------------------------------------------
// Suppression, in both spellings.
// ---------------------------------------------------------------------------

suppression()
{
	// #pragma disable 5008
	suppressed_by_pragma = 1;
	// #pragma restore 5008

	// gscode ignore
	suppressed_by_comment = 2;

	// expect 5008 — outside the disabled region
	reported_again = 3;
}
