using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// ScriptDoc association measured over the real corpus.
///
/// This exists because the unit tests could not see the bug. They fed the parser unquoted
/// <c>Summary: …</c> lines, which parse fine — but every shipped script wraps each ScriptDoc line
/// in double quotes, and the key regex starts at <c>\w</c>, so a leading quote made it match
/// nothing. 15,226 of 15,231 functions parsed to an empty doc while the suite stayed green.
///
/// A floor rather than an exact count: the corpus is whatever mod-tools version is installed, so
/// the assertion has to survive a different one. Anything near zero means association broke again.
/// </summary>
[Trait("Category", "Corpus")]
public class ScriptDocCorpusTests
{
    private readonly ITestOutputHelper _output;

    public ScriptDocCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void StockDocBlocks_ReachTheFunctionsTheyDocument()
    {
        if ( !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: %TA_TOOLS_PATH%\\share\\raw not found.");
            return;
        }

        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();

        int functions = 0;
        int withSummary = 0;
        int withArguments = 0;

        foreach ( string path in CorpusFixture.Scripts() )
        {
            ParseResult result;
            try
            {
                result = CorpusFixture.Analyze(path, resolver, names);
            }
            catch ( Exception )
            {
                continue;
            }

            foreach ( FunctionSymbol function in result.Extraction.Functions )
            {
                functions++;

                if ( function.Doc.Summary.Length > 0 )
                {
                    withSummary++;
                }

                if ( function.Doc.Arguments.Length > 0 )
                {
                    withArguments++;
                }
            }
        }

        _output.WriteLine($"functions={functions} withSummary={withSummary} withArguments={withArguments}");

        // Measured at 499 and 443 against the shipped scripts.
        Assert.True(withSummary > 400, $"only {withSummary} functions carry a doc summary");
        Assert.True(withArguments > 350, $"only {withArguments} functions carry documented arguments");
    }
}
