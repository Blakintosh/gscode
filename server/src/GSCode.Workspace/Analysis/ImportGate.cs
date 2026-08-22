using GSCode.Core.Diagnostics;
using GSCode.Parser;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// The precondition several lints share: an import that did not resolve makes the set of names
/// legally available in a file unknowable, so any rule about to say "this name matches nothing" or
/// "this name is not in scope" must stand down instead.
///
/// The two failures it covers are the same story told twice. An unresolved <c>#insert</c> takes its
/// macros with it, and an unexpanded macro is an ordinary identifier followed by an argument list —
/// indistinguishable from a call to a function nobody has; one missing <c>shared.gsh</c> produced
/// forty diagnostics against <c>IS_TRUE</c>, <c>VAL</c> and <c>SQR</c>, every one blaming the user
/// for a macro they did not write. An unresolved <c>#using</c> is the same story for a merge dialect,
/// where an included file's functions are called unqualified.
///
/// Which codes matter differs by rule, so the caller names them: a rule that already suppresses
/// itself on an unresolvable <c>#include</c> by other means does not need to ask about that one.
/// </summary>
internal static class ImportGate
{
    public static bool AnyUnresolved(ParseResult result, params GscDiagnosticCode[] codes)
    {
        foreach ( Diagnostic diagnostic in result.AllDiagnostics )
        {
            foreach ( GscDiagnosticCode code in codes )
            {
                if ( diagnostic.Code == code )
                {
                    return true;
                }
            }
        }

        return false;
    }
}
