using BenchmarkDotNet.Running;
using CommandLine;
using GSCode.Lexer;
using GSCode.NET.LSP;
using GSCode.Parser;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.CLI;

class Program
{
    public class Options
    {
        [Option('p', "parse", Required = false, HelpText = "Parses the provided file path.")]
        public string Parse { get; set; } = default!;

        [Option('b', "benchmark", Required = false, HelpText = "Runs a scene shared benchmark ( do not use).")]
        public bool Benchmark { get; set; }
    }

    static void Main(string[] args)
    {
        CommandLine.Parser.Default.ParseArguments<Options>(args)
               .WithParsed<Options>(o =>
               {
                   ScriptManager scriptManager = new ScriptManager();

                    if (o.Parse != null)
                    {
                        Console.WriteLine($"Parsing {o.Parse}...");
                        Uri documentUri = new Uri(o.Parse);
                        TextDocumentItem documentItem = new TextDocumentItem()
                        {
                            Uri = documentUri,
                            Text = File.ReadAllText(o.Parse)
                        };

                        // Adding to ScriptManager's cache and getting diagnostics
                        List<Diagnostic> diagnostics = scriptManager.AddEditorAsync(documentItem).Result;

                        Console.WriteLine("Diagnostics:");
                        foreach (var diagnosticsItem in diagnostics)
                        {
                            Console.WriteLine($"{diagnosticsItem.Message}");
                        }
                    }

                    if (o.Benchmark)
                    {
                        var summary = BenchmarkRunner.Run<Benchmarks>();
                    }
               });
    }
}