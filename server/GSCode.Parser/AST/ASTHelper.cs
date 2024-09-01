using GSCode.Data;
using GSCode.Parser.Data;
using Serilog;

namespace GSCode.Parser.AST
{
    internal class ASTHelper
    {
        public string ScriptFile { get; }
        public ParserIntelliSense Sense { get; }

        public ASTHelper(string scriptFile, ParserIntelliSense sense)
        {
            ScriptFile = scriptFile;
            Sense = sense;
        }

        public void AddDiagnostic(Range range, GSCErrorCodes code, params object?[] arguments)
        {
            Log.Warning("Error added: range {0} code {1}", range, code);

            Log.Warning(
                """
                --- Error added ---
                File: {6}
                Range: start @ ({0}, {1}), end @ ({2}, {3})
                Code: {4}
                Message: {5}
                """, 
            range.Start.Line, range.Start.Character, range.End.Line, range.End.Character, code,
                DiagnosticCodes.GetDiagnostic(range, DiagnosticSources.Ast, code, arguments).Message, ScriptFile);
            Sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(range, DiagnosticSources.Ast, code, arguments));
        }
    }
}
