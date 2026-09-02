// BO3 (Treyarch GSH) — a preprocessor header.
//
// A .gsh is inserted textually by #insert, so it belongs to whichever world included it and is the
// one place a macro can be declared. Everything the extension does for macros is reachable from
// here: hover shows the expansion, go-to-definition from a use lands on the #define below,
// find-references crosses into both the .gsc and the .csc that insert this file, and rename moves
// every one of them together.
//
// This file must produce ZERO diagnostics.

#define GSCODE_MAX_COUNT 4

#define GSCODE_SQUARE( x ) ( ( x ) * ( x ) )

#define GSCODE_IS_TRUE( value ) ( isdefined( value ) && value )

// A macro whose body is a call. Hovering the USE in gscode.gsc shows this text; go-to-definition
// on the name inside it resolves as though it were written at the call site.
#define GSCODE_CLAMPED( value, high ) ( ( value ) > ( high ) ? ( high ) : ( value ) )
