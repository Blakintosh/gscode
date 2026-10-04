// BO3 (Treyarch GSC) — the second file.
//
// Half the language server only has an opinion once a workspace has more than one script:
// #using resolution, cross-file go-to-definition and find-references, the unused-import rules, and
// the "declared but not imported" error. This file is what gscode.gsc reaches for, and what
// gscode_lints.gsc deliberately fails to import.
//
// This file must produce ZERO diagnostics.

#namespace gscode_target;

/@
"Name: target_helper( <value> )"
"Summary: Adds one. Exists so another file has something to call."
"MandatoryArg: <value> : the number to add to"
"Example: gscode_target::target_helper( 1 );"
@/
function target_helper( value )
{
	return value + 1;
}

/@
"Name: target_never_imported()"
"Summary: Called by gscode_lints.gsc WITHOUT a #using, which is what raises 5026 there."
@/
function target_never_imported()
{
	return "reached";
}

/@
"Name: target_unused()"
"Summary: Nothing calls this. There is deliberately no 'unused function' rule — a script function
is the game's entry point far too often for absence of callers to mean anything."
@/
function target_unused()
{
	return 0;
}
