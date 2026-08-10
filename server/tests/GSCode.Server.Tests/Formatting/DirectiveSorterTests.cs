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
            "// zebra matters\n#using scripts\\zebra;\n// apple matters\n#using scripts\\apple;\n\nfunction f()\n{\n}\n");

        Assert.Equal(
            "// apple matters\n#using scripts\\apple;\n// zebra matters\n#using scripts\\zebra;\n\nfunction f()\n{\n}\n",
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
