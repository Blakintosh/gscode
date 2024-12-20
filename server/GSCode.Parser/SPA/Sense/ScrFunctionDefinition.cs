﻿using GSCode.Parser.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Sense
{
    /// <summary>
    /// SPA IntelliSense component for defined functions
    /// </summary>
    internal sealed class ScrFunctionDefinition
    {
        /// <summary>
        /// The name of the function
        /// </summary>
        [JsonRequired]
        public required string Name { get; set; }

        /// <summary>
        /// The description for this function
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// The entity that this function is called on
        /// </summary>
        public string? CalledOn { get; set; }

        /// <summary>
        /// The parameter list of this function, which may be empty
        /// </summary>
        [JsonRequired]
        public required List<ScrFunctionParameter> Parameters { get; set; }

        /// <summary>
        /// An example of this function's usage
        /// </summary>
        public string? Example { get; set; }

        /// <summary>
        /// The flags list of this function, which may be empty
        /// </summary>
        public required List<string> Flags { get; set; }

        private string? _cachedDocumentation = null;

        /// <summary>
        /// Yields a documentation hover string for this function. Generated once, then cached.
        /// </summary>
        public string Documentation 
        { 
            get
            {
                if(_cachedDocumentation is string documentation)
                {
                    return documentation;
                }

                string calledOnString = CalledOn is string calledOn ? $"{calledOn} " : string.Empty;

                _cachedDocumentation = 
                    $"""
                    ```gsc
                    {calledOnString}function {Name}({GetCodedParameterList()})
                    ```
                    ---
                    {GetDescriptionString()}
                    {GetParametersString()}
                    {GetFlagsString()}
                    """;
                 
                return _cachedDocumentation;
            }
        }

        private string GetDescriptionString()
        {
            if(Description is string description)
            {
                return $"""
                    {description}

                    ---
                    """;
            }
            return string.Empty;
        }

        private string GetCodedParameterList()
        {
            if(Parameters.Count == 0) 
            {
                return string.Empty;
            }

            List<string> parameters = new();
            foreach(ScrFunctionParameter parameter in Parameters)
            {
                if(parameter.Mandatory.HasValue && parameter.Mandatory.Value)
                {
                    parameters.Add(parameter.Name);
                    continue;
                }
                // Defaults?
            }

            return string.Join(", ", parameters);
        }

        private string GetParametersString()
        {
            string calledOnString = CalledOn is string calledOn ? $"Called on: `<{calledOn}>`\n" : string.Empty;

            string parametersString = string.Empty;
            if(Parameters.Count > 0)
            {
                parametersString = "Parameters:\n";
                foreach(ScrFunctionParameter parameter in Parameters)
                {
                    string parameterNameString = (parameter.Mandatory.HasValue && parameter.Mandatory.Value) ? $"<{parameter.Name}>" : $"[{parameter.Name}]";

                    parametersString += $"* `{parameterNameString}` {parameter.Description ?? string.Empty}\n";
                }
            }
            else if(calledOnString == string.Empty)
            {
                return string.Empty;
            }

            return $"{calledOnString}{parametersString}\n---";
        }

        private static string GetLabelForFlag(string flag)
        {
            return flag switch
            {
                "autogenerated" => "_This documentation was generated from Treyarch's API, which may contain errors._",
                "broken" => "**Do not use this function as it is broken.**",
                "deprecated" => "**This function is deprecated and should not be used.**",
                "useless" => "_This function serves no purpose for modders._",
                _ => throw new ArgumentOutOfRangeException(flag)
            };
        }

        private string GetFlagsString()
        {
            List<string> flags = new();
            foreach(string flag in Flags)
            {
                flags.Add(GetLabelForFlag(flag));
            }

            return string.Join('\n', flags);
        }
    }

    /// <summary>
    /// SPA IntelliSense component for defined functions' parameters
    /// </summary>
    internal sealed class ScrFunctionParameter
    {
        // TODO: this currently is nullable due to issues within the API, it'd be better to enforce not null though asap
        /// <summary>
        /// The name of the parameter
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// The description for this parameter
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Whether the parameter is mandatory or optional
        /// </summary>
        public required bool? Mandatory { get; set; } 

        // Type: May be implemented in future
    }
}
