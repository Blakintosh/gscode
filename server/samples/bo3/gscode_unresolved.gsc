// BO3 (Treyarch GSC) — the unresolvable import, alone in its own file.
//
// It has to be alone, and that is worth knowing rather than working around. The import lints share
// one resolution pass, and the pass reports NOTHING when any `#using` in the file failed to resolve:
// once an import is missing, every "unused import" and every "declared but not imported" answer
// would be drawn from a scope that is known to be incomplete, and the file would be covered in
// errors pointing at calls instead of at the one line that is actually wrong.
//
// So 5009 suppresses 5001 and 5026, and a file demonstrating all three would demonstrate one. The
// other two live in gscode_lints.gsc, which imports nothing broken.

// expect 5009 — no such script under the raw root
#using gscode_does_not_exist;

#namespace gscode_unresolved;

// What the import rules stand down on, the RESOLUTION rules still answer: the call below is
// qualified, so it names a script location and no engine function could have matched it. The two
// import rules go quiet; this one does not.
function calls_into_the_missing_script()
{
	// expect 5013 — nothing in the workspace declares it
	return gscode_does_not_exist::whatever();
}
