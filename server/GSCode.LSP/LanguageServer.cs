using System;
using System.Diagnostics;
using System.Threading.Tasks;
using GSCode.LSP.Handlers;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer;
using OmniSharp.Extensions.LanguageServer.Server;

namespace GSCode.LSP
{
    public class Program
    {
        static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }

        static async Task MainAsync(string[] args)
        {
            // while (!System.Diagnostics.Debugger.IsAttached)
            // {
            //    await Task.Delay(100);
            // }

            var server = await LanguageServer.From(options =>
                options
                    .WithInput(Console.OpenStandardInput())
                    .WithOutput(Console.OpenStandardOutput())
                    .WithLoggerFactory(new LoggerFactory())
                    .AddDefaultLoggingProvider()
                    .WithHandler<TextDocumentSyncHandler>()
                );

            throw new Exception("LSP");
            await server.WaitForExit;
        }
    }
}