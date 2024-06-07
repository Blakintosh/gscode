using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.NET;

[DataContract]
internal class CorrectedSemanticTokensOptions : SemanticTokensOptions
{
    //
    // Summary:
    //     Gets or sets a legend describing how semantic token types and modifiers are encoded
    //     in responses.
    [DataMember(Name = "legend")]
    public new CorrectedSemanticTokensLegend Legend { get; set; }

    //
    // Summary:
    //     Gets or sets a value indicating whether semantic tokens Range provider requests
    //     are supported.
    [DataMember(Name = "range")]
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public new SumType<bool, object>? Range { get; set; }

    //
    // Summary:
    //     Gets or sets whether or not the server supports providing semantic tokens for
    //     a full document.
    [DataMember(Name = "full")]
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public new SumType<bool, SemanticTokensFullOptions>? Full { get; set; }

    //
    // Summary:
    //     Gets or sets a value indicating whether work done progress is supported.
    [DataMember(Name = "workDoneProgress")]
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public new bool WorkDoneProgress { get; set; }
}

[DataContract]
public class CorrectedSemanticTokensLegend : SemanticTokensLegend
{
    //
    // Summary:
    //     Gets or sets an array of token types that can be encoded in semantic tokens responses.
    [DataMember(Name = "tokenTypes")]
    public new string[] TokenTypes { get; set; }

    //
    // Summary:
    //     Gets or sets an array of token modfiers that can be encoded in semantic tokens
    //     responses.
    [DataMember(Name = "tokenModifiers")]
    public new string[] TokenModifiers { get; set; }
}