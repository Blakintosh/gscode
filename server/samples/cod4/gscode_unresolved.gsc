// CoD4 (Infinity Ward GSC) — the unresolvable include, alone in its own file.
//
// It has to be alone, and that is worth knowing rather than working around. The import lints share
// one resolution pass, and the pass reports NOTHING when any `#include` in the file failed to
// resolve: once an import is missing, every "unused include" and every "declared but not included"
// answer would be drawn from a scope that is known to be incomplete, and the file would be covered
// in errors pointing at calls instead of at the one line that is actually wrong.
//
// So 5009 suppresses 5012 and 5026, and a file demonstrating all three would demonstrate one. The
// other two live in gscode_lints.gsc, which includes nothing broken.
//
// The .csc side has the same arrangement for the same reason; see gscode_lints.csc.

// expect 5009 — no such script under the raw root
#include gscode_does_not_exist;

// What the import rules stand down on, the RESOLUTION rules still answer. The two are gated
// separately and on different evidence, so a file with a broken include is not a file the editor
// goes silent about.
calls_into_the_missing_script()
{
	// expect 5014 — matches no script function and no known engine function
	return does_not_exist_anywhere();
}
