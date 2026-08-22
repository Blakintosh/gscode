using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// The formatter's only code-moving operation, so the tests are mostly about what it refuses.
///
/// `#using` and `#precache` sort because nothing can observe their position. `#insert` and
/// `#define` do not, because an insert is spliced in textually and a define can be what the next
/// one depends on. And the whole pass stands down when a `#define` precedes an `#insert`, the one
/// arrangement where regrouping could lift an insert above a macro it needs — which no stock script
/// does, but a mod might.
///
/// A backslash-continued directive is one entry. The preprocessor ends a macro body at the first
/// newline not preceded by a backslash, so a separator dropped between the `\` and the line it
/// continues empties the macro and turns its body into top-level code.
/// </summary>
public class DirectiveSorterTests
{
    [Fact]
    public void UsingsAreGatheredAndSorted()
    {
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\zebra;\n#insert scripts\\shared\\shared.gsh;\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\apple;\n#using scripts\\zebra;\n\n#insert scripts\\shared\\shared.gsh;\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void GroupsComeOutInCanonicalOrderSeparatedByBlankLines()
    {
        string? sorted = DirectiveSorter.Sort(
            "#precache( \"model\", \"m\" );\n#namespace foo;\n#define BAR 0\n#using scripts\\a;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\a;\n\n#namespace foo;\n\n#define BAR 0\n\n#precache( \"model\", \"m\" );\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void InsertsKeepTheirRelativeOrder()
    {
        // Alphabetically zebra would come first. It must not: an insert is textual, so two of them
        // can disagree about a macro and the later one wins.
        string? sorted = DirectiveSorter.Sort(
            "#insert scripts\\zebra.gsh;\n#insert scripts\\apple.gsh;\n\nfunction f()\n{\n}\n");

        Assert.Null(sorted);
    }

    [Fact]
    public void DefinesKeepTheirRelativeOrder()
    {
        // BAZ is defined in terms of BAR, so sorting them would break the file.
        string? sorted = DirectiveSorter.Sort(
            "#define BAR 1\n#define BAZ BAR\n#define AAA 2\n\nfunction f()\n{\n}\n");

        Assert.Null(sorted);
    }

    [Fact]
    public void ADefineBeforeAnInsertStandsTheWholePassDown()
    {
        // Regrouping would hoist the insert above the macro. Refuse rather than guess.
        Assert.Null(DirectiveSorter.Sort(
            "#define GUARD 1\n#insert scripts\\shared\\shared.gsh;\n#using scripts\\zebra;\n#using scripts\\apple;\n\nfunction f()\n{\n}\n"));
    }

    [Fact]
    public void CommentsTravelWithTheDirectiveTheySitAbove()
    {
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\middle;\n// zebra matters\n#using scripts\\zebra;\n// apple matters\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "// apple matters\n#using scripts\\apple;\n#using scripts\\middle;\n// zebra matters\n#using scripts\\zebra;\n\nfunction f()\n{\n}\n",
            sorted);
    }

    // --- the comment run above the FIRST directive is a banner ---

    [Fact]
    public void AHuggingBannerStaysAboveTheBlockInsteadOfSplittingIt()
    {
        // It used to travel with the directive it hugged, so sorting carried a section header into
        // the middle of the very block it introduces.
        string? sorted = DirectiveSorter.Sort(
            "// ---------- directives ----------\n#using scripts\\zebra;\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "// ---------- directives ----------\n#using scripts\\apple;\n#using scripts\\zebra;\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void AHuggingBannerIsNotPushedOffTheBlockByABlankLine()
    {
        // The author wrote it touching the block; a banner separated by a blank keeps its blank.
        // Spacing is reproduced rather than normalised, so neither shape drifts into the other.
        string? hugging = DirectiveSorter.Sort(
            "// header\n#using scripts\\zebra;\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "// header\n#using scripts\\apple;\n#using scripts\\zebra;\n\nfunction f()\n{\n}\n",
            hugging);

        string? separated = DirectiveSorter.Sort(
            "// header\n\n#using scripts\\zebra;\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "// header\n\n#using scripts\\apple;\n#using scripts\\zebra;\n\nfunction f()\n{\n}\n",
            separated);
    }

    [Fact]
    public void AFileHeaderAndASectionHeaderBothStayAboveTheBlock()
    {
        // Two runs, one blank-separated and one hugging. Both are banners, and the blank between
        // them is where the author put it.
        string? sorted = DirectiveSorter.Sort(
            "// file header\n\n// section header\n#using scripts\\zebra;\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "// file header\n\n// section header\n#using scripts\\apple;\n#using scripts\\zebra;\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void AlreadyCanonicalTextIsLeftAlone()
    {
        // Returning null rather than an identical string means Format emits no edit at all.
        Assert.Null(DirectiveSorter.Sort(
            "#using scripts\\apple;\n#using scripts\\zebra;\n\n#namespace foo;\n\nfunction f()\n{\n}\n"));
    }

    [Fact]
    public void AFileWithNoDirectivesIsLeftAlone()
    {
        Assert.Null(DirectiveSorter.Sort("function f()\n{\n}\n"));
    }

    [Fact]
    public void DirectivesBelowTheBlockAreNotHoisted()
    {
        // Only the LEADING block is the sorter's business. A #precache sitting between functions
        // is someone's deliberate placement.
        string source = "#using scripts\\a;\n\nfunction f()\n{\n}\n\n#precache( \"model\", \"m\" );\n\nfunction g()\n{\n}\n";

        Assert.Null(DirectiveSorter.Sort(source));
    }

    [Fact]
    public void AMultiLineDefineStaysWithItsContinuation()
    {
        // A blank line between the '\' and the line it continues ends the macro: FOO becomes empty
        // and its body becomes a stray top-level statement.
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\b;\n#define FOO( a ) \\\n    a + 1\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\b;\n\n#define FOO( a ) \\\n    a + 1\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void AMultiLineDefineMovesAsOneUnitWhenItsGroupIsReordered()
    {
        // Both body lines have to travel with the #define when the group order puts it after the
        // #precache that preceded it.
        string? sorted = DirectiveSorter.Sort(
            "#precache( \"model\", \"m\" );\n#define FOO( a ) \\\n    a + \\\n    1\n#using scripts\\a;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\a;\n\n#define FOO( a ) \\\n    a + \\\n    1\n\n#precache( \"model\", \"m\" );\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void AContinuationSwallowedByABlankLineIsRejectedByTheSafetyGate()
    {
        // The gate joins a '\' to the next PHYSICAL line, so an inserted blank there changes a
        // logical line rather than disappearing into the blank-line exemption.
        string intact = "#define FOO( a ) \\\n    a + 1\n\nfunction f()\n{\n}\n";
        string severed = "#define FOO( a ) \\\n\n    a + 1\n\nfunction f()\n{\n}\n";

        Assert.False(DirectiveSorter.SameLines(intact, severed));
        Assert.True(DirectiveSorter.SameLines(intact, intact));
    }

    [Fact]
    public void AHeaderBannerSeparatedByABlankLineStaysAtTheTop()
    {
        // A banner divorced from the first import by a blank line describes the FILE, so sorting
        // must not carry it into the middle of the import block.
        string? sorted = DirectiveSorter.Sort(
            "// ============\n// t.gsc\n// ============\n\n#using scripts\\zebra;\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "// ============\n// t.gsc\n// ============\n\n#using scripts\\apple;\n#using scripts\\zebra;\n\nfunction f()\n{\n}\n",
            sorted);
    }

    // --- #include is the other dialect's #using ---

    [Fact]
    public void IncludesSortLikeUsings()
    {
        // Until #include was a group, `GroupOf` returned -1 for it and the block ended at the
        // first line of the file — so this pass did nothing at all on CoD4, WaW, MW2 and BO1.
        string? sorted = DirectiveSorter.Sort(
            "#include maps\\_zebra;\n#include maps\\_apple;\n\nmain()\n{\n}\n");

        Assert.Equal(
            "#include maps\\_apple;\n#include maps\\_zebra;\n\nmain()\n{\n}\n",
            sorted);
    }

    // --- the #insert / #define dependency chain ---

    [Fact]
    public void ADefineAboveAnInsertStandsTheWholePassDown()
    {
        // The one arrangement where regrouping could lift an insert above a macro it needs.
        Assert.Null(DirectiveSorter.Sort(
            "#define A 1\n#insert scripts\\x.gsh;\n\nfunction f()\n{\n}\n"));
    }

    [Fact]
    public void ADefineAboveAnInsertStandsDownEvenWithDirectivesBetweenThem()
    {
        // The guard is sticky rather than adjacent, so an intervening directive cannot slip a
        // define past it.
        Assert.Null(DirectiveSorter.Sort(
            "#define A 1\n#namespace foo;\n#precache( \"model\", \"m\" );\n#insert scripts\\x.gsh;\n\nfunction f()\n{\n}\n"));
    }

    [Fact]
    public void AnInsertAboveADefineKeepsThatOrder()
    {
        // The safe direction, and it must stay safe: a macro defined by the inserted header has to
        // still be defined before the #define that uses it.
        string? sorted = DirectiveSorter.Sort(
            "#namespace foo;\n#insert scripts\\x.gsh;\n#define A 1\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#insert scripts\\x.gsh;\n\n#namespace foo;\n\n#define A 1\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void ChainedDefinesKeepTheirOrder()
    {
        // `#define B A` depends on `#define A`. Group 3 is not sortable, so the chain survives
        // even though B sorts before A alphabetically.
        string? sorted = DirectiveSorter.Sort(
            "#namespace foo;\n#define B_MACRO 1\n#define A_MACRO B_MACRO\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#namespace foo;\n\n#define B_MACRO 1\n#define A_MACRO B_MACRO\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void APrecacheUsingAMacroEndsUpBelowTheDefine()
    {
        // A precache can name a macro, and group order puts every define above every precache. So
        // the one file this could rearrange is one where the macro was ALREADY used before it was
        // defined: the pass moves it toward correct, never away from it.
        string? sorted = DirectiveSorter.Sort(
            "#precache( \"model\", MODEL );\n#define MODEL \"tag_origin\"\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#define MODEL \"tag_origin\"\n\n#precache( \"model\", MODEL );\n\nfunction f()\n{\n}\n",
            sorted);
    }

    // --- a comment run owned by nothing ends the block ---

    [Fact]
    public void ACommentRunFollowedByABlankLineEndsTheBlock()
    {
        // It annotates neither the directive above it nor the one below, so there is no owner to
        // move it with. The imports above still sort; it and everything under it stay as written.
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\zebra;\n#using scripts\\apple;\n// section header\n\n#namespace foo;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\apple;\n#using scripts\\zebra;\n\n// section header\n\n#namespace foo;\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void ACommentedOutDirectiveIsNotGluedToTheNextRealOne()
    {
        // `_healthoverlay.gsc`'s shape: a disabled #precache written as a note under the imports.
        // It used to arrive glued to `#namespace`, having lost the blank line the author left.
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\zebra;\n#using scripts\\apple;\n//#precache( \"material\", \"overlay_low_health\" );\n\n#namespace foo;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\apple;\n#using scripts\\zebra;\n\n//#precache( \"material\", \"overlay_low_health\" );\n\n#namespace foo;\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void ACommentRunAboveCodeRatherThanADirectiveIsUnaffected()
    {
        // The far commoner shape — 38 of the 55 BO3 files with an orphan run — where the block was
        // already over. Ending at the blank has to leave these byte-identical to what they were.
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\zebra;\n#using scripts\\apple;\n// ---- utility ----\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\apple;\n#using scripts\\zebra;\n\n// ---- utility ----\n\nfunction f()\n{\n}\n",
            sorted);
    }

    // --- #using_animtree is positional and ends the block ---

    [Fact]
    public void AnAnimtreeDirectiveIsNotHoistedIntoTheUsingBlock()
    {
        // `#using_animtree` binds every `%anim` reference BELOW it until the next one, so its
        // position is its meaning. util_shared.gsc names "generic" at line 1530 and "all_player"
        // at 1551 and 1995; hoisting or sorting those would silently rebind every animation
        // between them.
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\z;\n#using scripts\\a;\n\n#namespace foo;\n\n#using_animtree( \"generic\" );\n\nfunction f()\n{\n}\n");

        // The usings still sort and the namespace still groups — the animtree simply stays below
        // both, where it was written, instead of being lifted into the using block.
        Assert.Equal(
            "#using scripts\\a;\n#using scripts\\z;\n\n#namespace foo;\n\n#using_animtree( \"generic\" );\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void DirectivesBelowAnAnimtreeAreLeftAlone()
    {
        // The block ENDS at the animtree rather than skipping over it: a directive below one is
        // below it for a reason this pass cannot check, and sorting the tail would step over the
        // very line that makes position matter.
        string? sorted = DirectiveSorter.Sort(
            "#using scripts\\z;\n#using scripts\\a;\n\n#using_animtree( \"generic\" );\n\n"
            + "#precache( \"model\", \"z\" );\n#precache( \"model\", \"a\" );\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "#using scripts\\a;\n#using scripts\\z;\n\n#using_animtree( \"generic\" );\n\n"
            + "#precache( \"model\", \"z\" );\n#precache( \"model\", \"a\" );\n\nfunction f()\n{\n}\n",
            sorted);
    }

    [Fact]
    public void TwoAnimtreesKeepBothTheirNamesAndTheirOrder()
    {
        // The shape from util_shared.gsc, reduced. Two DIFFERENT tree names is what proves the
        // directive is positional rather than declarative.
        string source =
            "#using scripts\\a;\n\n#using_animtree( \"generic\" );\n\nfunction f()\n{\n}\n\n"
            + "#using_animtree( \"all_player\" );\n\nfunction g()\n{\n}\n";

        Assert.Null(DirectiveSorter.Sort(source));
    }

    [Fact]
    public void SortingIsIdempotent()
    {
        string source =
            "// banner\n\n#precache( \"model\", \"b\" );\n#using scripts\\z;\n#using scripts\\a;\n"
            + "#define FOO( a ) \\\n    a + 1\n\nfunction f()\n{\n}\n";

        string once = DirectiveSorter.Sort(source)!;

        Assert.Null(DirectiveSorter.Sort(once));
    }

    [Fact]
    public void NoLineIsEverLostOrDuplicated()
    {
        string source =
            "#precache( \"model\", \"b\" );\n#precache( \"model\", \"a\" );\n#using scripts\\z;\n"
            + "#using scripts\\a;\n#namespace foo;\n\nfunction f()\n{\n\tx = 1;\n}\n";

        string sorted = DirectiveSorter.Sort(source)!;

        List<string> before = [.. source.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Order(StringComparer.Ordinal)];
        List<string> after = [.. sorted.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Order(StringComparer.Ordinal)];

        Assert.Equal(before, after);
    }
}
