// BO3 (Treyarch CSC) — the client-side second file.
//
// The two worlds are separate namespaces of files, not one tree with two extensions: a `#using` in
// a .csc resolves to a .csc, and the same path from a .gsc resolves to the .gsc beside it. That is
// why gscode_target.gsc has a twin here rather than being reached from both.
//
// This file must produce ZERO diagnostics.

#namespace gscode_target;

/@
"Name: client_target_helper( <value> )"
"Summary: The client-side counterpart of gscode_target.gsc's target_helper."
"MandatoryArg: <value> : the number to add to"
@/
function client_target_helper( value )
{
	return value + 1;
}
