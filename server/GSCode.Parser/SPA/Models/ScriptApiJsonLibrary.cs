using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Models
{
    internal class ScriptApiJsonLibrary
    {
        /// <summary>
        /// The language ID this API is for (gsc/csc).
        /// </summary>
        [JsonRequired]
        public required string LanguageId { get; set; }

        /// <summary>
        /// The game this is for (t7).
        /// </summary>
        [JsonRequired]
        public required string GameId { get; set; }

        /// <summary>
        /// The version of this API.
        /// </summary>
        [JsonRequired]
        public required int Revision { get; set; }

        /// <summary>
        /// The API functions this result is loading into the SPA.
        /// </summary>
        [JsonRequired]
        public required List<ScrFunction> Api { get; set; }
    }
}
