// BO3 (Treyarch CSC) — the client-side showcase.
//
// Same grammar as the .gsc showcase, so this file does not repeat it. What it demonstrates is
// everything the extension does DIFFERENTLY once the world is the client:
//
//   * a different engine library. Completion, hover and signature help are answered from the
//     client API, and a server-only engine function is not offered here and does not resolve;
//   * a different set of Radiant keys. The client-side keys are offered in a .csc and hidden in a
//     .gsc, which is the whole reason the key data records a side at all;
//   * client-only #precache asset types, which are an error in a .gsc and correct here;
//   * separate import resolution. `#using gscode_target` finds gscode_target.csc
//     from this file and gscode_target.gsc from the .gsc — two files, one path;
//   * a SHARED header. gscode.gsh is inserted by both worlds, so a macro declared once has
//     references on both sides and rename has to move all of them.
//
// This file must produce ZERO diagnostics.

#using gscode_target;

#insert gscode.gsh;

#namespace gscode_client;

// Correct here, and 4006 in a .gsc.
#precache( "client_fx", "impacts/generic_impact" );
#precache( "client_model", "tag_origin" );

class client_thing
{
	var m_count;

	constructor()
	{
		m_count = 0;
	}

	destructor()
	{
	}

	function bump( amount )
	{
		m_count += amount;
		return m_count;
	}
}

/@
"Name: init()"
"Summary: The client-side entry point."
"Module: GSCode"
"CallOn: level"
@/
function autoexec init()
{
	level thread main();
}

function main()
{
	level endon( "game_ended" );

	client_state();
	shared_macros();
	imports();
}

// The engine library answering this file is the CLIENT one. Every name below is a client engine
// function; a server-only name in its place would be 5014 here even though it is correct in a .gsc.
function private client_state()
{
	thing = new client_thing();
	[[ thing ]]->bump( 1 );

	origin = ( 0, 0, 0 );
	scaled = vectorscale( origin, 64 );

	return scaled;
}

// The same macros as the .gsc side, out of the same header. Find-references on GSCODE_SQUARE
// returns uses in both worlds.
function private shared_macros()
{
	squared = GSCODE_SQUARE( 3 );
	clamped = GSCODE_CLAMPED( squared, GSCODE_MAX_COUNT );

	if ( GSCODE_IS_TRUE( clamped ) )
	{
		return clamped;
	}

	return 0;
}

// Resolves to gscode_target.csc, not to the .gsc of the same path.
function private imports()
{
	return gscode_target::client_target_helper( 1 );
}
