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
