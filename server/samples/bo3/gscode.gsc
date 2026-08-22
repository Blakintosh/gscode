// BO3 (Treyarch GSC) — the showcase.
//
// Every construct the BO3 dialect has, written the way the formatter would leave it, and correct:
// this file must produce ZERO diagnostics. That is what makes it the file to open when the question
// is "does hover still work", "is this token coloured right", "does completion offer the class's
// inherited methods" — anything underlined here is a false positive, not a mistake in the script.
//
// Its opposite numbers are gscode_lints.gsc (one deliberate finding per rule) and gscode_broken.gsc
// (the lexer, preprocessor and parser errors).

#using gscode_target;

#insert gscode.gsh;

#namespace gscode;

#precache( "fx", "impacts/generic_impact" );
#precache( "model", "tag_origin" );

#define LOCAL_BUILD 1

// The two branches below are excluded from the build, and the editor greys them out to say so.
// That is a Hint rather than a complaint — the only diagnostic a correct file is expected to carry,
// which is why the showcase declares it here instead of pretending the file is silent.
//
// Written as `expect-anywhere` because an anchored expectation would have to sit INSIDE the
// inactive branch, and the region the hint covers starts wherever the first line of that branch is.
// expect-anywhere 2013 — the #elif body
// expect-anywhere 2013 — the #else body
#if LOCAL_BUILD
#define BUILD_MODE "full"
#elif 0
#define BUILD_MODE "partial"
#else
#define BUILD_MODE "none"
#endif

// ---------------------------------------------------------------------------
// Classes — BO3 alone. `class`, `var`, `new`, `constructor`, `destructor` and
// the `->` method call all arrive together with the class system.
// ---------------------------------------------------------------------------

class base_thing
{
	var m_name;

	constructor()
	{
		m_name = "base";
	}

	destructor()
	{
	}

	function get_name()
	{
		return m_name;
	}
}

// Inheritance. Completion on a `derived_thing` offers `get_name` as well as `bump`, and
// go-to-definition on the inherited one lands in `base_thing` above.
class derived_thing : base_thing
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

// ---------------------------------------------------------------------------
// Functions — declaration modifiers, default parameters, by-reference
// parameters, and the vararg pack.
// ---------------------------------------------------------------------------

/@
"Name: init()"
"Summary: The autoexec entry point. Runs without anything calling it."
"Module: GSCode"
"CallOn: level"
"SPMP: both"
@/
function autoexec init()
{
	level.things = [];
	level thread main();
}

/@
"Name: main()"
"Summary: Calls every demonstration below, so nothing here is unreachable."
"Module: GSCode"
@/
function main()
{
	level endon( "game_ended" );

	literals();
	operators();
	control_flow();
	calls_and_pointers();
	classes();
	events();
	imports();
	macros();
	variadic( 1, 2, 3 );

	/#
	dev_only();
	#/
}

/@
"Name: helper( <first>, [second], <out_ref> )"
"Summary: A private function with a default parameter and a by-reference parameter."
"MandatoryArg: <first> : the left operand"
"OptionalArg: [second] : the right operand, 2 when omitted"
"MandatoryArg: <out_ref> : written through, not read"
"Example: helper( 1, 2, result );"
@/
function private helper( first, second = 2, &out_ref )
{
	out_ref = first + second;
	return out_ref;
}

// `...` is the parameter pack; the values arrive as `vararg`, which is a keyword on BO3 but reads
// as an ordinary value.
function variadic( ... )
{
	total = 0;
	foreach ( argument in vararg )
	{
		total += argument;
	}

	return total + vararg.size;
}

// ---------------------------------------------------------------------------
// Literals — one of every kind the lexer knows, all read back at the end so
// none of them is an unused local.
// ---------------------------------------------------------------------------

function private literals()
{
	const LOCAL_CONST = 10;

	integer = 42;
	real = 3.14;
	hex = 0xFF;
	text = "plain string";
	localized = &"MENU_QUIT";
	hashed = #"hashed_string";

	yes = true;
	no = false;
	nothing = undefined;

	vector = ( 0, 0, 1 );

	array = [];
	array[ 0 ] = "by index";
	array[ "key" ] = "by key";

	structure = spawnstruct();
	structure.field = LOCAL_CONST;
	structure.nested = BUILD_MODE;

	bundle = [];
	bundle[ 0 ] = integer;
	bundle[ 1 ] = real;
	bundle[ 2 ] = hex;
	bundle[ 3 ] = text;
	bundle[ 4 ] = localized;
	bundle[ 5 ] = hashed;
	bundle[ 6 ] = yes;
	bundle[ 7 ] = no;
	bundle[ 8 ] = nothing;
	bundle[ 9 ] = vector;
	bundle[ 10 ] = array;
	bundle[ 11 ] = structure;

	return bundle;
}

// ---------------------------------------------------------------------------
// Operators — every one, including the identity comparisons `===` / `!==`
// that only Treyarch's line has.
// ---------------------------------------------------------------------------

function private operators()
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
	identical = left === right;
	unequal = left != right;
	not_identical = left !== right;
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
	left %= 3;
	left &= 1;
	left |= 2;
	left ^= 3;
	left <<= 1;
	left >>= 1;

	chosen = ( left > right ) ? "bigger" : "smaller";
	scaled = vectorscale( ( 0, 0, 1 ), 64 );

	if ( identical && not_identical && less && less_or_equal && greater && greater_or_equal
		&& both && either && inverted && unequal && isdefined( chosen ) && isdefined( scaled ) )
	{
		return added + subtracted + multiplied + divided + remainder
			+ bit_and + bit_or + bit_xor + shifted_left + shifted_right + bit_not + negated;
	}

	return 0;
}

// ---------------------------------------------------------------------------
// Control flow — `do`/`while` and `foreach` are both BO3 additions on this
// side of the family tree.
// ---------------------------------------------------------------------------

function private control_flow()
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

	while ( count < GSCODE_MAX_COUNT )
	{
		count++;
		if ( count == 2 )
		{
			continue;
		}
	}

	do
	{
		count--;
	}
	while ( count > 0 );

	for ( index = 0; index < GSCODE_MAX_COUNT; index++ )
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

	foreach ( item in items )
	{
		count += item.size;
	}

	foreach ( key, value in items )
	{
		count += key.size + value.size;
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
	waitrealtime 0.05;
	waittillframeend;

	assert( count > 0 );

	profilestart( "control_flow" );
	profilestop();

	return count;
}

// ---------------------------------------------------------------------------
// Calls, threads and function pointers — `&` is BO3's pointer spelling, and a
// bare `ns::name` is always a call here.
// ---------------------------------------------------------------------------

function private calls_and_pointers()
{
	plain = helper( 1, 2, undefined );
	qualified = gscode::helper( 1, 2, undefined );

	pointer = &helper;
	qualified_pointer = &gscode::helper;

	through_pointer = [[ pointer ]]( 1, 2, undefined );
	through_qualified = [[ qualified_pointer ]]( 1, 2, undefined );

	entity = spawnstruct();
	entity method_style();
	entity thread method_style();
	entity thread [[ pointer ]]( 1, 2, undefined );
	thread method_style();

	return plain + qualified + through_pointer + through_qualified;
}

function private method_style()
{
	return 1;
}

// ---------------------------------------------------------------------------
// Classes in use.
// ---------------------------------------------------------------------------

function private classes()
{
	base = new base_thing();
	derived = new derived_thing();

	[[ derived ]]->bump( 1 );

	return [[ base ]]->get_name() + [[ derived ]]->get_name();
}

// ---------------------------------------------------------------------------
// Notify, waittill and endon. `waittill`'s trailing names are the only place
// those variables come into existence — they are bound, not read.
// ---------------------------------------------------------------------------

function private events()
{
	level endon( "game_ended" );

	thread waiter();

	level waittill( "custom_event", first, second );
	level waittillmatch( "custom_event", 1 );

	return first + second;
}

function private waiter()
{
	wait 0.1;
	level notify( "custom_event", 1, 2 );
}

// ---------------------------------------------------------------------------
// The other file. Go-to-definition on either name below leaves this document.
// ---------------------------------------------------------------------------

function private imports()
{
	return gscode_target::target_helper( 1 );
}

// ---------------------------------------------------------------------------
// Macros from the inserted header. Hover shows the expansion; go-to-definition
// lands in gscode.gsh.
// ---------------------------------------------------------------------------

function private macros()
{
	squared = GSCODE_SQUARE( 3 );
	clamped = GSCODE_CLAMPED( squared, GSCODE_MAX_COUNT );

	if ( GSCODE_IS_TRUE( clamped ) )
	{
		return clamped;
	}

	return 0;
}

// ---------------------------------------------------------------------------
// Dev blocks. `/# … #/` is compiled out of a release build, so a function
// declared inside one may only be called from inside one.
// ---------------------------------------------------------------------------

/#
function dev_only()
{
	println( "dev build" );
}
#/
