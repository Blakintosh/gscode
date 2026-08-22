// BO3 (Treyarch GSC) — the deliberate findings.
//
// One mistake at a time, each declared by the `// expect <code>` comment above it. The file still
// PARSES cleanly on purpose: a syntax error puts the parser into recovery, several rules stand down
// on a file the parser could not read, and everything below the error would be judged against a
// damaged tree. The lexer, preprocessor and parser errors live in gscode_broken.gsc for that reason.
//
// Read this as the catalogue of what the extension will tell you about your code, and of what each
// message actually means.

// Used below, so this import is live.
#using gscode;

// The same script imported twice. The SECOND one is the one reported.
// expect 5018 — already imported above
#using gscode;

// Imported and never used. A different complaint from 5018: this import is not a duplicate, it is
// simply dead.
// expect 5001 — nothing in this file uses anything it declares
#using gscode_unused;

// Note what is not HERE: 5009, the unresolvable import. It lives in gscode_unresolved.gsc, because
// a file with one missing import gets no answer from the other import rules at all — see that file
// for why. Putting it here would silently delete the 5001 case above.

#insert gscode.gsh;

#namespace gscode_lints;

// ---------------------------------------------------------------------------
// 4000 range — declarations and directives, decided from this file alone.
// ---------------------------------------------------------------------------

// expect 4000 — not one of BO3's precache asset types
#precache( "not_a_real_asset_type", "some/asset" );

// expect 4001 — #precache takes a type AND an asset
#precache( "fx" );

// Some asset types belong to the client. A .gsc cannot precache one, and a .gsh can precache
// anything, because a header does not know which world will insert it.
// expect 4006 — client_fx is a client-side type
#precache( "client_fx", "impacts/generic_impact" );

// Both ends of a cycle are reported, not just the one that closes it: neither class can be
// constructed, and which of the two is "the mistake" is not something the rule can know.
// expect 5021 — cycle_a inherits cycle_b, which inherits cycle_a
class cycle_a : cycle_b
{
	constructor()
	{
	}

	destructor()
	{
	}
}

// expect 5021 — and the same cycle seen from the other end
class cycle_b : cycle_a
{
	constructor()
	{
	}

	destructor()
	{
	}
}

class bad_members
{
	// A constructor runs from `new` and a destructor runs from the engine. Neither is ever handed
	// anything, so a parameter list on either is a misunderstanding rather than a style choice.
	// expect 4002 — a constructor cannot take parameters
	// expect 5020 — and the parameter is unread, which is the second thing wrong with it
	constructor( unwanted )
	{
	}

	// expect 4003 — a destructor cannot take parameters
	// expect 5020
	destructor( unwanted )
	{
	}
}

// expect 4007 — the same parameter name twice
function duplicate_parameters( value, value )
{
	return value;
}

// expect 4008 — the parameter pack has to be last
function vararg_not_last( ..., trailing )
{
	return trailing;
}

// expect 4005 — this name is already declared, above
function duplicate_parameters( other )
{
	return other;
}

// ---------------------------------------------------------------------------
// 5000 range — flow and the workspace.
// ---------------------------------------------------------------------------

function locals_and_flow()
{
	// expect 5008 — assigned, then never read
	unused_local = 4;

	// expect 5016 — read before anything assigns it
	total = never_assigned + 1;

	// expect 5031 — a division by a constant zero
	broken = total / 0;

	return broken;

	// Two findings on one line, and they are separate claims: nothing can reach the statement, and
	// the variable it assigns is never read. Each is written on its own line above.
	// expect 5015 — unreachable
	// expect 5008 — and unread
	unreachable = 1;
}

function switch_labels( value )
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

function constants()
{
	const FIXED = 1;

	// expect 5030 — a const is assigned once, where it is declared
	FIXED = 2;

	// expect 5029 — a const initialiser is evaluated at compile time, so it cannot call anything
	const COMPUTED = SpawnStruct();

	return FIXED + COMPUTED;
}

function calls_that_do_not_resolve()
{
	// The two "not found" rules are a deliberate split. An UNQUALIFIED name could be either kind,
	// so it is judged against the engine library; a name qualified with a namespace can only be a
	// script function, and is judged against the workspace.
	// expect 5014 — matches no script function and no known engine function
	NoSuchEngineFunction();

	// expect 5013 — gscode declares no such function
	gscode::no_such_script_function();

	// Nothing here imports a file declaring the gscode_unresolved namespace, so the name means
	// nothing in this file even though the script exists and the function is right there in it.
	//
	// This is BO3's ONLY answer for an unreachable call to a real function. The Infinity Ward
	// dialects have a second one, 5026, for a name that is merged into scope by #include rather
	// than qualified — which cannot happen here, so that rule lives in the pre-BO3 samples.
	// expect 5000 — the namespace itself is not imported
	gscode_unresolved::calls_into_the_missing_script();

	// expect 5003 — gscode::helper is private, so only its own file may call it
	gscode::helper( 1, 2, undefined );

	// expect 5022 — gscode::main takes none
	gscode::main( 1 );

	// expect 5023 — the engine's Abs takes exactly one
	Abs();
}

function reads_and_writes()
{
	items = [];
	items[ 0 ] = "a";

	// expect 5005 — .size is computed by the engine
	items.size = 9;

	// A BARE global object name. Writing THROUGH one — level.things = [] — is how every script uses
	// them and is not reported; it is the assignment to the name itself that cannot mean anything.
	// expect 5035 — `level` is the engine's name
	// expect 5008 — and, taken as a local, it is written and never read
	level = items;

	// expect 5019 — AddDebugCommand returns nothing, so this assigns undefined
	nothing = AddDebugCommand( "cmd" );

	// expect 5002 — AllowAttack's parameter is declared bool; say so
	self AllowAttack( 1 );

	return nothing;
}

function bindings_and_results()
{
	// expect 5020 — bound by waittill and never read
	level waittill( "custom_event", unused_binding );

	// expect 5028 — a threaded call returns immediately; there is no result to take
	result = thread waiting_function();

	// expect 5032 — the value is computed and dropped, so the line does nothing
	1 + 1;

	// expect 5024 — `vararg` only exists inside a function declared with `...`
	count = vararg.size;

	return result + count;
}

function waiting_function()
{
	wait 0.05;
	return 1;
}

// ---------------------------------------------------------------------------
// Dev-only builtins. `/# … #/` is compiled out of a release build, and so are
// the engine functions that only exist in one — so calling one from release
// code is a call to something that will not be there.
// ---------------------------------------------------------------------------

function dev_only_calls()
{
	// expect 5006 — Print3d only exists in a development build
	Print3d( ( 0, 0, 0 ), "text", ( 1, 1, 1 ), 1, 1 );

	// The same call inside a dev block is correct, and is not reported.
	/#
	Print3d( ( 0, 0, 0 ), "text", ( 1, 1, 1 ), 1, 1 );
	#/
}

// ---------------------------------------------------------------------------
// Suppression. Every rule can be switched off, and both spellings are shown so
// that neither one quietly stops working. A suppressed diagnostic is not
// reported, so neither suppressed line below carries an `expect` comment.
//
// The pragma lives INSIDE a comment on purpose: GSC has no `#pragma` of its
// own, so a bare one would be a syntax error in the game's compiler. Anything
// the extension adds to the language has to be invisible to it.
// ---------------------------------------------------------------------------

function suppression()
{
	// #pragma disable 5008
	suppressed_by_pragma = 1;
	// #pragma restore 5008

	// gscode ignore
	suppressed_by_comment = 2;

	// The pragma is a REGION and was restored above, so this one is reported again.
	// expect 5008 — outside the disabled region
	reported_again = 3;
}
