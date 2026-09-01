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
/// For the header half there is nothing to choose, so <see cref="MacrosLost"/> is the list rather
/// than each caller's memory of it.
/// </summary>
internal static class ImportGate
{
    /// <summary>
    /// Every way an <c>#insert</c> can fail to deliver its macros — the six the preprocessor
    /// abandons the splice on.
    ///
    /// Four callers each asked about <see cref="GscDiagnosticCode.InsertNotFound"/> alone, which is
    /// one of six and the only one anybody remembers. A header naming a script instead of a
    /// <c>.gsh</c> (2014), nested past the depth cap (2007), in a cycle (2008), written with an
    /// illegal path (2005) or with no path at all (2003) leaves exactly the same hole: the macros
    /// never arrive, and an unexpanded macro is an identifier followed by an argument list.
    ///
    /// <see cref="GscDiagnosticCode.InsertMissingSemicolon"/> is deliberately ABSENT, and it is the
    /// one that has to be read rather than assumed. The preprocessor reports it and carries on —
    /// every other case above ends in `return index` — so the macros DO arrive and a rule that stood
    /// down on it would go quiet over a punctuation slip.
    /// </summary>
    public static readonly GscDiagnosticCode[] MacrosLost =
    [
        GscDiagnosticCode.MissingInsertPath,
        GscDiagnosticCode.InvalidInsertPath,
        GscDiagnosticCode.InsertNotAHeader,
        GscDiagnosticCode.InsertTooDeep,
        GscDiagnosticCode.InsertNotFound,
        GscDiagnosticCode.InsertCycle,
    ];

    /// <summary>
    /// The <see cref="MacrosLost"/> set plus whatever else a caller's rule turns on.
    ///
    /// One pass over the diagnostics testing both lists, rather than concatenating them and calling
    /// <see cref="AnyUnresolved"/>: this runs per file per keystroke, and the concatenation was a
    /// fresh array each time to ask a question about six constants.
    /// </summary>
    public static bool AnyMacrosLost(ParseResult result, params GscDiagnosticCode[] alsoCodes)
    {
        foreach ( Diagnostic diagnostic in result.AllDiagnostics )
        {
            if ( Holds(MacrosLost, diagnostic.Code) || Holds(alsoCodes, diagnostic.Code) )
            {
                return true;
            }
        }

        return false;
    }

    private static bool Holds(GscDiagnosticCode[] codes, GscDiagnosticCode code)
    {
        foreach ( GscDiagnosticCode candidate in codes )
        {
            if ( candidate == code )
            {
                return true;
            }
        }

        return false;
    }

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
