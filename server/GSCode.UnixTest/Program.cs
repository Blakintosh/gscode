using System;
using System.IO;
using System.Text;
using System.Diagnostics;

class LspTest
{
    static void Main()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "GSCode.NET.dll",
            WorkingDirectory = @"../../../../GSCode.NET/bin/Debug/net8.0/",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.WriteLine("Failed to start process");
                return;
            }

            Console.WriteLine("Process started successfully");
            Thread.Sleep(5000);
            
            Console.WriteLine("Starting work");
            
            // Send initialize request
            SendLspMessage(process.StandardInput, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    processId = Process.GetCurrentProcess().Id,
                    rootUri = "file:///workspace",
                    capabilities = new { }
                }
            });

            // Read LSP responses properly
            var reader = new StreamReader(process.StandardOutput.BaseStream);
            while (!process.HasExited)
            {
                // Try to read Content-Length header
                string header = reader.ReadLine();
                Console.WriteLine($"Received header: {header}");

                if (string.IsNullOrEmpty(header)) continue;

                if (header.StartsWith("Content-Length: "))
                {
                    int contentLength = int.Parse(header.Substring("Content-Length: ".Length));
                    Console.WriteLine($"Expected content length: {contentLength}");

                    // Read the empty line
                    reader.ReadLine();

                    // Read the content
                    char[] buffer = new char[contentLength];
                    int read = reader.Read(buffer, 0, contentLength);
                    string content = new string(buffer);
                    
                    Console.WriteLine($"Read {read} chars of content:");
                    Console.WriteLine(content);
                }
                else
                {
                    // Log non-LSP output
                    Console.WriteLine($"Server output: {header}");
                }
            }

            string stderr = process.StandardError.ReadToEnd();
            if (!string.IsNullOrEmpty(stderr))
            {
                Console.WriteLine($"\nStdErr output: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    static void SendLspMessage(StreamWriter writer, object message)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        writer.WriteLine($"Content-Length: {bytes.Length}\r\n");
        writer.WriteLine(json);
        writer.Flush();
    }
}