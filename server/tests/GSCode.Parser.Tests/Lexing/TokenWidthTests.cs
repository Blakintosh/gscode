using System.Runtime.CompilerServices;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

/// <summary>
/// How WIDE the two token structs are, pinned.
///
/// This is not style policing. Both structs are stored one per token in a per-file array, and the
/// large-object-heap threshold is 85,000 bytes — so the width decides how long a script can be
/// before its token array is allocated on a heap that is collected only at gen2 and never compacted
/// unless asked. A cold index of Black Ops 1 (2,960 files, 34 MB of source) was measured holding a
/// 1,035 MB heap of which 887 MB was free space, and array churn at these widths is why.
///
/// Growing either struct is therefore a real cost paid across every file in the workspace, not a
/// rounding error. If a field genuinely has to be added, change the number here deliberately and
/// re-run the memory probe — do not adjust it to make a build go green.
///
/// The numbers below are what the layout costs today.
/// </summary>
public class TokenWidthTests
{
    /// <summary>
    /// Kind, Start, Length and a four-int TextRange, with no reference fields and no padding.
    /// At this width a token array reaches the LOH at roughly 3,000 tokens.
    /// </summary>
    [Fact]
    public void Token_IsTwentyEightBytes()
    {
        Assert.Equal(28, Unsafe.SizeOf<Token>());
    }

    /// <summary>
    /// The preprocessed token, the larger of the two and the one that dominates. It was 80 bytes
    /// while Provenance was inlined, reaching the LOH at ~1,060 tokens; at 40 it reaches it at
    /// ~2,120, so twice as much of a workspace stays off that heap.
    /// </summary>
    [Fact]
    public void PToken_IsFortyBytes()
    {
        Assert.Equal(40, Unsafe.SizeOf<PToken>());
    }

    /// <summary>
    /// Provenance used to be 48 of those 80 bytes — more than the rest of the token put together —
    /// because two nullable TextRanges cost 20 bytes each (16 plus a flag, padded).
    ///
    /// It answers "where did this token come from", which is a property of an EXPANSION SITE, of
    /// which a file has a handful, so it is now held by reference and every token written in the
    /// root file shares one instance. Inside a PToken it costs a pointer.
    /// </summary>
    [Fact]
    public void Provenance_IsOnePointerInsideAToken()
    {
        Assert.Equal(IntPtr.Size, Unsafe.SizeOf<Provenance>());
    }
}
