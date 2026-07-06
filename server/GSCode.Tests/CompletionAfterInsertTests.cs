using GSCode.Data;
using GSCode.NET.LSP;
using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Linq;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Reproduces a reported bug: after an #insert directive, completions inside a function body
/// further down the host file degraded to file-scope-only (keywords + macros), instead of the
/// full function-scope completion set.
///
/// Root cause: #insert'd tokens are cloned with their real .gsh-native line/char positions
/// (needed for accurate macro go-to-definition), but DocumentTokensLibrary flattened every
/// token - host and inserted alike - into one array in document order and relied on it being
/// sorted by position for its binary search. Once the inserted file's own line numbers overlap
/// numerically with the host file's later lines, that invariant breaks: GetIndex(position) can
/// land on the wrong token, so IsInsideFunctionBlock's brace-depth walk (which starts from
/// whatever token GetIndex returns) miscounts and reports "not inside a function", falling back
/// to file-scope-only completions.
/// </summary>
public class CompletionAfterInsertTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _sharedPath;

    public CompletionAfterInsertTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "gscode_completion_insert_test_" + Guid.NewGuid().ToString("N"));
        string scriptsDir = Path.Combine(_rootDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        _sharedPath = Path.Combine(scriptsDir, "shared.gsh");

        // Real function declarations (not #define macros, which are fully consumed by the
        // preprocessor and leave no tokens behind) so their tokens are actually spliced into the
        // host's stream. Deliberately more lines than the host file has past the #insert
        // directive, so the inserted file's own line numbers (0..N) run past where the host's
        // subsequent lines resume - producing a genuine decrease in line number in document
        // order, not just an overlapping tie.
        File.WriteAllText(_sharedPath, string.Join('\n',
            Enumerable.Range(0, 20).Select(i => $"function helper{i}() {{ return {i}; }}")));
    }

    public void Dispose() => Directory.Delete(_rootDir, recursive: true);

    [Fact]
    public async Task CompletionInsideFunctionBody_AfterInsert_OffersFullFunctionScopeCompletions()
    {
        string hostPath = Path.Combine(_rootDir, "scripts", "host.gsc");
        string hostText = """
            #insert scripts\shared.gsh;

            function main()
            {
                level.x = 1;

            }
            """;
        File.WriteAllText(hostPath, hostText);

        var sm = new ScriptManager();
        var item = new TextDocumentItem
        {
            Uri = DocumentUri.FromFileSystemPath(hostPath),
            LanguageId = "gsc",
            Version = 1,
            Text = hostText
        };
        await sm.AddEditorAsync(item);
        var script = sm.GetParsedEditor(item.Uri.ToUri())!;

        // Cursor on the blank line inside main()'s body (line 5, 0-indexed).
        CompletionList? completions = await script.GetCompletionAsync(new Position(5, 0), default);

        Assert.NotNull(completions);
        // "if" is only ever offered inside a function body (ScriptKeywords.FileScope has no
        // control-flow keywords), so its presence proves IsInsideFunctionBlock resolved true.
        Assert.Contains(completions!.Items, c => c.Label == "if");
    }
}
