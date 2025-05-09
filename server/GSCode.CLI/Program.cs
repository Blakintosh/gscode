﻿using BenchmarkDotNet.Running;
using CommandLine;
using GSCode.NET.LSP;
using GSCode.Parser;
using GSCode.Parser.SPA;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

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

    static async Task Main(string[] args)
    {
        await CommandLine.Parser.Default.ParseArguments<Options>(args)
               .WithParsedAsync<Options>(async o =>
               {
                   ScriptManager scriptManager = new ScriptManager(new NullLogger<ScriptManager>());

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
                       IEnumerable<Diagnostic> diagnostics = await scriptManager.AddEditorAsync(documentItem);

                       Console.WriteLine("Diagnostics:");
                       foreach (var diagnosticsItem in diagnostics)
                       {
                           Console.WriteLine($"{diagnosticsItem.Message}");
                       }
                   }

                   //if (o.Benchmark)
                   //{
                   //    var summary = BenchmarkRunner.Run<Benchmarks>();
                   //}
               });
    }
}