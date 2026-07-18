using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;
using TextRange = GSCode.Core.Text.TextRange;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using LspDiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace GSCode.Server.Mapping;

/// <summary>
/// The ONLY place Core types and LSP protocol types meet. Core positions/ranges are
/// UTF-16 and zero-based like the protocol's, so every conversion is structural.
/// </summary>
public static class LspMapping
{
    public static LspPosition ToLsp(this Position position)
    {
        return new LspPosition(position.Line, position.Character);
    }

    public static Position ToCore(this LspPosition position)
    {
        return new Position(position.Line, position.Character);
    }

    public static LspRange ToLsp(this TextRange range)
    {
        return new LspRange(range.Start.ToLsp(), range.End.ToLsp());
    }

    public static TextRange ToCore(this LspRange range)
    {
        return new TextRange(range.Start.ToCore(), range.End.ToCore());
    }

    public static LspDiagnostic ToLsp(this GSCode.Core.Diagnostics.Diagnostic diagnostic)
    {
        return new LspDiagnostic
        {
            Range = diagnostic.Range.ToLsp(),
            Severity = (LspDiagnosticSeverity)(int)diagnostic.Severity,
            Code = new DiagnosticCode((int)diagnostic.Code),
            Source = "gscode",
            Message = diagnostic.Message,
        };
    }
}
