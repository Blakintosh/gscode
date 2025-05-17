using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using Serilog;
using System;

namespace GSCode.NET.LSP;


//public abstract class WatchedScript
//{
//    protected ScriptLexer Lexer { get; set; }
//    protected IScriptParser Parser { get; set; }
//    public Uri DocumentUri { get; protected set; }
//    public bool Busy { get; protected set; }
//    public Task? PrimaryProcessingTask { get; protected set; }
//    public Task? FullProcessingTask { get; protected set; }
//    public IEnumerable<IExportedSymbol>? ExportedSymbols { get; protected set; }

//    public abstract Task ParseAsync(string documentText);

//    public async Task<IEnumerable<IExportedSymbol>> IssueExportedSymbolsAsync()
//    {
//        if(Busy)
//        {
//            await PrimaryProcessingTask!;
//        }
//        return ExportedSymbols!;
//    }
//}

//public class WatchedEditor : WatchedScript
//{
//    public event Func<Uri, List<Uri>, Task<IEnumerable<IExportedSymbol>>> RequestDependencies;

//    public WatchedEditor(Uri documentUri, Func<Uri, List<Uri>, Task<IEnumerable<IExportedSymbol>>> requestDependencies)
//    {
//        Lexer = new(documentUri);
//        base.Parser = new ScriptParser(Lexer);
//        DocumentUri = documentUri;
//        RequestDependencies += requestDependencies;
//    }

//    public new ScriptParser Parser
//    {
//        get
//        {
//            return (ScriptParser) base.Parser;
//        }
//    }

//    public override sealed async Task ParseAsync(string documentText)
//    {
//        Busy = true;
//        FullProcessingTask = Task.Run(async () =>
//        {
//            PrimaryProcessingTask = Task.Run(async () =>
//            {
//                await Lexer.TokenizeAsync(documentText);
//                await Parser.Parse();
//            });

//            await PrimaryProcessingTask;

//            // From the dependencies, get the exported symbols for us to validate
//            ExportedSymbols = Parser.DefinitionsTable!.ExportedFunctions;
//            IEnumerable<IExportedSymbol> dependencySymbols = await RequestDependencies.Invoke(DocumentUri, Parser.DefinitionsTable!.Dependencies);

//            // Now bring that together with the local file symbols, to give the static analyser a full library to work with.

//            await Parser.FinishParsing(dependencySymbols);


//            Log.Information("Added and parsed dependency {uri}", DocumentUri);


//            // Run step to compare deferred symbols

//            // Get and resolve dependencies to Uris.
//            // CHECK that a file isn't trying to reference itself. If it does, give an error.
//            // await RequestDependencies(DocumentUri, [dependencies]);
//            // Run a final step that then checks the deferred symbols for their validity.
//        });

//        await FullProcessingTask;
//        Busy = false;
//    }

//    public async Task<List<Diagnostic>> GetDiagnosticsAsync()
//    {
//        if(Busy)
//        {
//            await FullProcessingTask!;
//        }

//        return Parser.IntelliSense!.Diagnostics;
//    }

//    internal async Task PushSemanticTokensAsync(SemanticTokensBuilder builder)
//    {
//        if (Busy)
//        {
//            await FullProcessingTask!;
//        }

//        builder.SemanticTokens = Parser.IntelliSense!.SemanticTokens;
//    }

//    public async Task<Hover?> GetHoverAsync(Position position)
//    {
//        if (Busy)
//        {
//            await FullProcessingTask!;
//        }

//        IHoverable? result = Parser.IntelliSense!.HoverLibrary.Get(position);
//        if(result is not null)
//        {
//            return result.GetHover();
//        }
//        return null;
//    }
//}

//public class WatchedDependency : WatchedScript
//{
//    public WatchedDependency(Uri documentUri)
//    {
//        Lexer = new(documentUri);
//        Parser = new DependencyParser(Lexer);
//        DocumentUri = documentUri;
//    }

//    public override sealed async Task ParseAsync(string documentText)
//    {
//        Busy = true;
//        PrimaryProcessingTask = Task.Run(async () =>
//        {
//            await Lexer.TokenizeAsync(documentText);
//            await Parser.Parse();

//            ExportedSymbols = Parser.DefinitionsTable!.ExportedFunctions;
//        });
//        await PrimaryProcessingTask;
//        Busy = false;
//    }
//}