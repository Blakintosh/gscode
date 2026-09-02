// WaW (Treyarch GSC) — a file that exists only to be included and ignored.
//
// gscode_lints.gsc has an `#include` for this and calls nothing it declares, which is the whole of
// what 5012 reports — the `#include` twin of BO3's 5001.
//
// This file must produce ZERO diagnostics.

unused_from_elsewhere()
{
	return 1;
}
