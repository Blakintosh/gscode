// BO3 (Treyarch GSC) — the unresolvable import, alone in its own file.
//
// It is alone for ONE rule now, not three, and the narrowing is worth knowing. 5000 claims that
// nothing this script imports declares the namespace; a `#using` that failed to resolve is exactly
// the file that might have, so that rule reports nothing at all while one is missing — otherwise a
// single wrong line would cover the file in errors pointing at calls instead of at itself.
//
// 5001 and 5026 used to stand down on the same pass and no longer do. Whether an import is used
// depends on this file's references and on that import's own declarations, and a file nobody can
// read is neither of those, so the readable imports are judged and only the broken directive goes
// unjudged. It never enters the resolved list, which is why 5009 is still the only thing said about
// this line rather than 5009 and 5001 both.
//
// So the file stays alone, and 5000 is what it protects. That case lives in gscode_lints.gsc,
// which imports nothing broken.

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
