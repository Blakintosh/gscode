using GSCode.Parser.SPA.Models;
using GSCode.Parser.SPA.Sense;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA
{
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

        private static readonly Dictionary<string, ScriptApiJsonLibrary> _languageLibraries = new();

        public static bool LoadLanguageApiLibrary(string source)
        {
            try
            {
                ScriptApiJsonLibrary library = JsonConvert.DeserializeObject<ScriptApiJsonLibrary>(source);

                if(_languageLibraries.TryGetValue(library.LanguageId, out ScriptApiJsonLibrary? existingLibrary)
                    && existingLibrary!.Revision > library.Revision)
                {
                    return false;
                }

                _languageLibraries[library.LanguageId] = library;
                Log.Information("Loaded API library for {0}.", library.LanguageId);
                return true;
            }
            catch(Exception e)
            {
                Log.Warning(e, "Failed to deserialize API library.");
            }
            return false;
        }
    }
}
