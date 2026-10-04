// BO3 (Treyarch GSC) — a file that exists only to be imported and ignored.
//
// gscode_lints.gsc has a `#using` for this and never calls anything in it, which is the whole of
// what 5001 reports. It has to be a real, resolvable script: an import that does not resolve is
// 5009 instead, and one file serving both rules would let either cover for the other.
//
// This file must produce ZERO diagnostics.

#namespace gscode_unused;

function unused_from_elsewhere()
{
	return 1;
}
