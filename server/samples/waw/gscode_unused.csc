// WaW (Treyarch CSC) — a client file that exists only to be included and ignored.
//
// gscode_lints.csc has an `#include` for this and calls nothing it declares, which is the whole of
// what 5012 reports. It has to be a real, resolvable CLIENT script: the .gsc of the same name is a
// different file to a .csc, so a server-side twin would not satisfy the include and the report
// would be 5009 instead.
//
// This file must produce ZERO diagnostics.

client_unused_from_elsewhere()
{
	return 1;
}
