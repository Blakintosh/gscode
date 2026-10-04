using System.Globalization;
using GSCode.Core.Diagnostics;

namespace GSCode.Server.Tests.Samples;

/// <summary>
/// One expected diagnostic, read out of a sample script's own comments.
///
/// <see cref="Line"/> is zero-based to match <c>Diagnostic.Range</c>, and is -1 for an
/// expectation written as <c>expect-anywhere</c> — the form for a diagnostic that has no line to
/// stand above, such as one anchored on the first line of the file.
/// </summary>
internal sealed record SampleExpectation(GscDiagnosticCode Code, int Line, string Note)
{
    public bool Anywhere => Line < 0;
}

/// <summary>
/// Reads the <c>// expect</c> comments that ARE the specification for a sample script.
///
/// The expectation lives in the file it describes, one line above the code that produces it, so a
/// sample reads as a tutorial and is its own golden file at the same time. A sidecar list would say
/// the same thing while drifting from the script on every edit, and a snapshot file would say
/// nothing to a person reading it.
///
/// <code>
/// // expect 5008 — assigned and never read
/// unused_thing = 4;
/// </code>
///
/// A run of consecutive expectation comments all anchor to the first line below them that is not
/// itself one, which is how a single line carrying two diagnostics is written. Everything after the
/// code — an em dash, a colon, prose — is a note for the reader and is not matched against
/// anything; the CODE is the assertion.
/// </summary>
internal static class SampleExpectations
{
    private const string ExpectPrefix = "expect";
    private const string AnywhereMarker = "expect-anywhere";

    /// <summary>
    /// Every expectation in <paramref name="text"/>, in file order. Lines and codes only —
    /// severity and message are deliberately not asserted, since both are edited far more often
    /// than a rule's identity and pinning them would turn a wording change into a failing suite.
    /// </summary>
    public static IReadOnlyList<SampleExpectation> Parse(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');

        List<SampleExpectation> expectations = [];

        // Expectations gathered but not yet anchored: everything in the current run of comments,
        // waiting for the line of code they describe.
        List<(GscDiagnosticCode Code, string Note)> pending = [];

        for ( int index = 0; index < lines.Length; index++ )
        {
            string trimmed = lines[index].Trim();

            if ( !trimmed.StartsWith("//", StringComparison.Ordinal) )
            {
                foreach ( (GscDiagnosticCode code, string note) in pending )
                {
                    expectations.Add(new SampleExpectation(code, index, note));
                }

                pending.Clear();
                continue;
            }

            string body = trimmed[2..].Trim();

            if ( body.StartsWith(AnywhereMarker, StringComparison.Ordinal) )
            {
                foreach ( (GscDiagnosticCode code, string note) in Codes(body[AnywhereMarker.Length..]) )
                {
                    expectations.Add(new SampleExpectation(code, -1, note));
                }

                continue;
            }

            if ( body.StartsWith(ExpectPrefix, StringComparison.Ordinal) )
            {
                pending.AddRange(Codes(body[ExpectPrefix.Length..]));
            }

            // Any other comment is prose. It neither anchors a pending run nor breaks it, so a
            // sample may explain itself between the expectation and the code it describes.
        }

        return expectations;
    }

    /// <summary>
    /// The codes in one expectation comment's tail, e.g. <c> 5008, 5016 — both, on one line</c>.
    /// Reading stops at the first token that is not a number, which is what makes the trailing note
    /// free-form.
    /// </summary>
    private static List<(GscDiagnosticCode Code, string Note)> Codes(string tail)
    {
        string trimmed = tail.TrimStart(' ', '\t', ':');

        List<(GscDiagnosticCode, string)> found = [];
        int position = 0;

        while ( position < trimmed.Length )
        {
            int start = position;
            while ( position < trimmed.Length && char.IsDigit(trimmed[position]) )
            {
                position++;
            }

            if ( position == start )
            {
                break;
            }

            int value = int.Parse(trimmed[start..position], CultureInfo.InvariantCulture);
            found.Add(((GscDiagnosticCode)value, trimmed[position..].Trim()));

            while ( position < trimmed.Length && (trimmed[position] == ',' || trimmed[position] == ' ') )
            {
                position++;
            }
        }

        // The note is the whole tail after the last code, shared by every code on the line.
        if ( found.Count > 0 )
        {
            string note = found[^1].Item2;
            for ( int index = 0; index < found.Count; index++ )
            {
                found[index] = (found[index].Item1, note);
            }
        }

        return found;
    }
}
