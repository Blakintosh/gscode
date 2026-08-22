// CoD4 (Infinity Ward GSC) — the showcase.
//
// Every construct the CoD4 dialect has, and correct: this file must produce ZERO diagnostics.
//
// Read it beside server/samples/bo3/gscode.gsc to see what the dialect actually
// costs. This is the true base of the language, and most of what people think of as GSC arrived
// later:
//
//   * no `function` keyword — a declaration is a name, a parameter list and a brace;
//   * no `#using`, no `#namespace`, no `#insert`, no `#precache`, no `#define` and no `#if`. The
//     preprocessor does not exist in this dialect, and a `#define` here is reported as such;
//   * no classes, no `new`, no `->`;
//   * no `foreach` — that is Infinity Ward's MW2 addition, and Treyarch's line does not get it
//     until BO3. Iteration goes through GetArrayKeys;
//   * no `do`/`while`, no `const`, no `autoexec`/`private`, no `vararg`;
//   * `::` is the function pointer, and a bare qualified name IS the pointer — parentheses would
//     call it. BO3 spells the same thing `&foo`;
//   * a path may be written inline at a call: gscode_target::target_by_path(), which
//     reaches a file this one never included;
//   * ScriptDoc is `///ScriptDocBegin` inside an ordinary block comment, not BO3's `/@ … @/`.

#include gscode_target;

/*
///ScriptDocBegin
"Name: main()"
"Summary: Calls every demonstration below, so nothing here is unreachable."
"CallOn: level"
"SPMP: MP"
///ScriptDocEnd
*/
main()
{
	level endon( "game_ended" );

	literals();
	operators();
	control_flow();
	calls_and_pointers();
	events();
	includes();

	/#
	dev_only();
	#/
}

/*
///ScriptDocBegin
"Name: helper( <first>, <second> )"
"Summary: Adds two numbers. There are no default parameters in this dialect."
"MandatoryArg: <first> : the left operand"
"MandatoryArg: <second> : the right operand"
///ScriptDocEnd
*/
helper( first, second )
{
	return first + second;
}

// ---------------------------------------------------------------------------
// Literals. No hash strings — `#"…"` is a Treyarch feature and arrives with
// BO1 — and no `const`.
// ---------------------------------------------------------------------------

literals()
{
	integer = 42;
	real = 3.14;
	hex = 0xFF;
	text = "plain string";
	localized = &"MENU_QUIT";

	yes = true;
	no = false;
	nothing = undefined;

	vector = ( 0, 0, 1 );

	array = [];
	array[ 0 ] = "by index";
	array[ "key" ] = "by key";

	structure = spawnstruct();
	structure.field = 10;

	bundle = [];
	bundle[ 0 ] = integer;
	bundle[ 1 ] = real;
	bundle[ 2 ] = hex;
	bundle[ 3 ] = text;
	bundle[ 4 ] = localized;
	bundle[ 5 ] = yes;
	bundle[ 6 ] = no;
	bundle[ 7 ] = nothing;
	bundle[ 8 ] = vector;
	bundle[ 9 ] = array;
	bundle[ 10 ] = structure;

	return bundle;
}

// ---------------------------------------------------------------------------
// Operators. No `===` / `!==`: identity comparison is Treyarch's.
// ---------------------------------------------------------------------------

operators()
{
	left = 7;
	right = 3;

	added = left + right;
	subtracted = left - right;
	multiplied = left * right;
	divided = left / right;
	remainder = left % right;

	bit_and = left & right;
	bit_or = left | right;
	bit_xor = left ^ right;
	shifted_left = left << 1;
	shifted_right = left >> 1;
	bit_not = ~left;
	negated = -left;

	equal = left == right;
	unequal = left != right;
	less = left < right;
	less_or_equal = left <= right;
	greater = left > right;
	greater_or_equal = left >= right;

	both = ( left > 0 ) && ( right > 0 );
	either = ( left > 0 ) || ( right > 0 );
	inverted = !equal;

	left++;
	left--;
	left += 1;
	left -= 1;
	left *= 2;
	left /= 2;

	if ( unequal && less && less_or_equal && greater && greater_or_equal && both && either
		&& inverted && isdefined( left ) )
	{
		return added + subtracted + multiplied + divided + remainder
			+ bit_and + bit_or + bit_xor + shifted_left + shifted_right + bit_not + negated;
	}

	return 0;
}

// ---------------------------------------------------------------------------
// Control flow. No `foreach` and no `do`/`while`; an array is walked through
// its keys.
// ---------------------------------------------------------------------------

control_flow()
{
	count = 0;

	if ( count == 0 )
	{
		count = 1;
	}
	else if ( count == 1 )
	{
		count = 2;
	}
	else
	{
		count = 3;
	}

	if ( count > 0 )
		count--;

	while ( count < 4 )
	{
		count++;
		if ( count == 2 )
		{
			continue;
		}
	}

	for ( index = 0; index < 4; index++ )
	{
		if ( index == 3 )
		{
			break;
		}
	}

	for ( ;; )
	{
		break;
	}

	items = [];
	items[ "first" ] = "a";
	items[ "second" ] = "b";

	keys = getarraykeys( items );
	for ( index = 0; index < keys.size; index++ )
	{
		count += items[ keys[ index ] ].size;
	}

	switch ( count )
	{
		case 0:
		case 1:
			count = 10;
			break;
		case "string_case":
			count = 20;
			break;
		default:
			count = 30;
			break;
	}

	wait 0.05;
	waittillframeend;

	assert( count > 0 );

	prof_begin( "control_flow" );
	prof_end( "control_flow" );

	return count;
}

// ---------------------------------------------------------------------------
// Calls, threads and pointers. `::` without parentheses IS the pointer.
// ---------------------------------------------------------------------------

calls_and_pointers()
{
	plain = helper( 1, 2 );

	pointer = gscode_target::target_helper;
	through_pointer = [[ pointer ]]( 1 );

	by_path = gscode_target::target_by_path();

	entity = spawnstruct();
	entity method_style();
	entity thread method_style();
	entity thread [[ pointer ]]( 1 );
	thread method_style();

	return plain + through_pointer + by_path.size;
}

method_style()
{
	return 1;
}

// ---------------------------------------------------------------------------
// Notify, waittill and endon — unchanged since the first game.
// ---------------------------------------------------------------------------

events()
{
	level endon( "game_ended" );

	thread waiter();

	level waittill( "custom_event", first, second );
	level waittillmatch( "custom_event", 1 );

	return first + second;
}

waiter()
{
	wait 0.1;
	level notify( "custom_event", 1, 2 );
}

// ---------------------------------------------------------------------------
// The included file. `target_helper` is called by BARE name: #include merges
// it into this file's scope rather than qualifying it.
// ---------------------------------------------------------------------------

includes()
{
	return target_helper( 1 );
}

// ---------------------------------------------------------------------------
// Dev blocks — present from the first game.
// ---------------------------------------------------------------------------

/#
dev_only()
{
	println( "dev build" );
}
#/
