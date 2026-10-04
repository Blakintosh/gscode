// WaW (Treyarch CSC) — the client-side findings.
//
// The client world is not a different language. The grammar is the same grammar, the flow rules are
// the same code walking the same tree, and every diagnostic below would read identically in the
// .gsc — which is exactly why this file is worth having: it is the proof that the world does not
// quietly change the answer.
//
// What the world DOES change is which rules speak at all, and on this game the answer is blunt.
// Every client-side rule reasons about engine functions, and World at War's client library is not marked
// complete or signature-reliable, so all of them stand down:
//
//   * 5013 / 5014 — a call resolving to neither a client script function nor a client engine
//     function. This is the rule the client world exists to make interesting, since a SERVER-only
//     engine name should be as unknown here as a name nobody has. It cannot be shown until the
//     client library is trusted.
//   * 5023 — a client engine call's argument count, which needs signatures.
//   * 5019 — assigning the result of a client engine function that returns nothing.
//   * 5002 — a literal 0/1 passed where the client library declares a bool.
//
// The calls that would trip them are left below with no `expect`, so the day this game's client
// library is curated, this sample fails and asks to be brought up to date.
//
// There is no gscode_broken.csc, here or anywhere. The lexer and the parser do not know what a
// client script is — the world is decided from the extension, after the text is read — so a
// client-side copy of those errors would run the same code over the same input and assert the same
// thing twice.

// Included and never used.
// expect 5012 — nothing in this file calls anything it declares
#include gscode_unused;

// ---------------------------------------------------------------------------
// The rules that would need a trusted client library. No `expect` on any of
// them, on purpose — see the note above.
// ---------------------------------------------------------------------------

untrusted_library()
{
	NoSuchClientFunction();

	nothing = clearallcorpses();

	return nothing;
}

// ---------------------------------------------------------------------------
// Flow and locals, which do not care which world they are in.
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

	// `level` is the engine's on the client side too. The global object names come from the
	// profile, not from the world, so both worlds of a game share them.
	// expect 5035 — `level` is the engine's name
	// expect 5008 — and, taken as a local, it is written and never read
	level = items;

	return items;
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
// Suppression, in both spellings. Neither is world-specific.
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
