// BO1 (Treyarch GSC) — the second file.
//
// The Infinity Ward dialects have no namespaces. `#include` MERGES a file's functions into the
// including file's scope, so everything here is called by bare name from gscode.gsc — and two files
// declaring `main` are two different functions only because nothing includes both.
//
// This file must produce ZERO diagnostics.

/*
///ScriptDocBegin
"Name: target_helper( <value> )"
"Summary: Adds one. Exists so another file has something to call."
"MandatoryArg: <value> : the number to add to"
"Example: result = target_helper( 1 );"
///ScriptDocEnd
*/
target_helper( value )
{
	return value + 1;
}

/*
///ScriptDocBegin
"Name: target_never_included()"
"Summary: Called by gscode_lints.gsc, which includes a different file. That is 5026."
///ScriptDocEnd
*/
target_never_included()
{
	return "reached";
}

/*
///ScriptDocBegin
"Name: target_by_path()"
"Summary: Reached as gscode_target::target_by_path(), which needs no #include at all."
///ScriptDocEnd
*/
target_by_path()
{
	return "by path";
}
