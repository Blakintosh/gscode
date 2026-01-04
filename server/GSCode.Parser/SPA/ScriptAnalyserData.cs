using GSCode.Parser.SPA.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace GSCode.Parser.SPA;

/// <summary>
/// GSC symbol types. Not all are applicable in all contexts, e.g. namespaces on a stack frame
/// </summary>
file enum ScrSymbolType
{
    Unknown,
    Function,
    Variable,
    Namespace,
    Object
}
file record class ScrSymbol();

public class ScriptAnalyserData
{
    public string GameId { get; } = "t7";
    public string LanguageId { get; }

    public ScriptAnalyserData(string languageId)
    {
        LanguageId = languageId;
    }

    private static readonly Dictionary<string, ScrLibraryData> _languageLibraries = new();

    public static async Task<bool> LoadLanguageApiAsync(string url, string filePathFallback)
    {
        // Try to load from URL
        try
        {
            using HttpClient client = new();
            string json = await client.GetStringAsync(url);
            return LoadLanguageApiData(json);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load API from {Url}", url);
        }

        // Try to load from file
        try
        {
            string json = File.ReadAllText(filePathFallback);
            return LoadLanguageApiData(json);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load API from {FilePath}", filePathFallback);
        }
        return false;
    }

    private static bool LoadLanguageApiData(string source)
    {
        try
        {
            ScriptApiJsonLibrary library = JsonConvert.DeserializeObject<ScriptApiJsonLibrary>(source);

            // All built-ins are implicit, because they can be called without using the sys namespace.
            foreach (ScrFunction function in library.Api)
            {
                function.Namespace = "sys";
                function.Implicit = true;

                // Check for vararg parameters and set the Vararg flag on the overload
                foreach (ScrFunctionOverload overload in function.Overloads)
                {
                    if (overload.Parameters.Any(p => p.Type?.DataType?.Equals("vararg", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        overload.Vararg = true;
                    }
                }
            }

            if (_languageLibraries.TryGetValue(library.LanguageId, out ScrLibraryData? existingLibrary)
                && existingLibrary!.Library.Revision > library.Revision)
            {
                return false;
            }

            _languageLibraries[library.LanguageId] = new ScrLibraryData(library);
            Log.Information("Loaded API library for {LanguageId}.", library.LanguageId);
            return true;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to deserialize API library.");
        }
        return false;
    }

    public List<ScrFunction> GetApiFunctions(string? filter = null)
    {
        if (!_languageLibraries.TryGetValue(LanguageId, out ScrLibraryData? library))
        {
            Log.Error("No API library found for {LanguageId}", LanguageId);
            return [];
        }
        Log.Information("API library found for {LanguageId}, it has {Count} functions", LanguageId, library.Functions.Count);
        return library.Library.Api;
    }

    public ScrFunction? GetApiFunction(string name)
    {
        if (!_languageLibraries.TryGetValue(LanguageId, out ScrLibraryData? library))
        {
            Log.Error("No API library found for {LanguageId}", LanguageId);
            return null;
        }
        return library.Functions.TryGetValue(name, out ScrFunction? function) ? function : null;
    }
}

internal class ScrLibraryData
{
    public ScriptApiJsonLibrary Library { get; }
    public SortedList<string, ScrFunction> Functions { get; }

    public ScrLibraryData(ScriptApiJsonLibrary library)
    {
        Library = library;
        Functions = new SortedList<string, ScrFunction>(library.Api.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ScrFunction function in library.Api)
        {
            Functions.Add(function.Name, function);
        }
    }
}