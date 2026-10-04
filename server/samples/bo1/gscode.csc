// BO1 (Treyarch CSC) — the client-side showcase.
//
// Same grammar as this game's .gsc showcase, so this file does not repeat it. What it demonstrates
// is everything the extension does DIFFERENTLY once the world is the client:
//
//   * a different engine library. Completion, hover and signature help are answered from the
//     client API, and a server-only engine function does not resolve here;
//   * a different set of Radiant keys. Treyarch's pre-BO3 games keep the client-side keys in a
//     SECOND file, clientkeys.txt, where BO3 marks them with a `client` prefix in one file — two
//     spellings of the same fact, and the reason the key data records a side at all;
//   * separate import resolution. `#include gscode_target` finds gscode_target.csc from
//     this file and gscode_target.gsc from the .gsc — two files, one path.
//
// There is no gscode_lints.csc for this game. The client-side rules all reason about engine
// functions, and Black Ops's client library is not marked complete or signature-reliable, so every one
// of them stands down here — see the gate note in gscode_lints.gsc. A lints file would assert that
// nothing is reported, which is not a fact about the client world.
//
// This file must produce ZERO diagnostics.

#include gscode_target;

/*
///ScriptDocBegin
"Name: main()"
"Summary: The client-side entry point."
"CallOn: level"
///ScriptDocEnd
*/
main()
{
	level endon( "game_ended" );

	client_state();
	includes();
}

client_state()
{
	origin = ( 0, 0, 0 );
	structure = spawnstruct();
	structure.origin = origin;

	return structure;
}

// Resolves to gscode_target.csc, not to the .gsc of the same path.
includes()
{
	return client_target_helper( 1 );
}
