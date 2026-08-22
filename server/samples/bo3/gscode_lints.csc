// BO3 (Treyarch CSC) — the client-side findings.
//
// Deliberately NOT a copy of gscode_lints.gsc. The grammar and the flow rules are the same code
// running over the same tree, so repeating them here would double the maintenance and prove
// nothing. What is here is what only goes wrong in a .csc: the rules whose answer depends on which
// world the file belongs to.
//
// The lexer, preprocessor and parser errors have no client-side variant at all — the parser does
// not know what a client script is — so there is no gscode_broken.csc.

#insert gscode.gsh;

#namespace gscode_lints;

// Server-only asset types, from a client script. The mirror of 4006 in the .gsc: neither world may
// precache the other's assets, and the message names the side it belongs to.
// expect 4000 — "not_a_real_asset_type" is not a type in either world
#precache( "not_a_real_asset_type", "some/asset" );

function client_only_resolution()
{
	// Resolved against the CLIENT engine library. A name that exists only on the server side is as
	// unknown here as a name that does not exist at all — which is the point of keeping two
	// libraries rather than one union of both.
	// expect 5014 — no client engine function by this name
	NoSuchClientFunction();
}
