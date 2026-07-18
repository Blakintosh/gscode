using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GSCode.Core.Docs;

/// <summary>One documented argument from a ScriptDoc block.</summary>
public sealed record ScriptDocArgument(string Name, string Description, bool Optional);

/// <summary>
/// A parsed /@ ... @/ ScriptDoc block. The key-value line format matches the stock
/// convention: Name:, Summary:, Module:, CallOn:, SPMP:, MandatoryArg:, OptionalArg:,
/// Example:. Unrecognized content is preserved in RawText for fallback display.
/// </summary>
public sealed record ScriptDocComment
{
    /// <summary>The empty sentinel — consumers never null-check, they check IsNone.</summary>
    public static ScriptDocComment None { get; } = new() { RawText = "" };

    public string RawText { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Module { get; init; } = "";
    public string CallOn { get; init; } = "";
    public string Spmp { get; init; } = "";
    public ImmutableArray<ScriptDocArgument> Arguments { get; init; } = [];
    public ImmutableArray<string> Examples { get; init; } = [];

    [JsonIgnore]
    public bool IsNone
    {
        get { return ReferenceEquals(this, None) || (RawText.Length == 0 && Summary.Length == 0 && Name.Length == 0); }
    }

    private static readonly Regex s_keyValue = new(
        @"^\s*(?<key>\w+)\s*:\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Accepts "<arg> desc", "<arg>: desc", "[arg] desc", "[arg]: desc", or "arg desc".
    private static readonly Regex s_argument = new(
        @"^(?<name><[^>]+>|\[[^\]]+\]|[^:\s]+)\s*:?\s*(?<desc>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses the text of a doc block (delimiters included or not).</summary>
    public static ScriptDocComment Parse(string docBlockText)
    {
        string body = docBlockText.Trim();
        if ( body.StartsWith("/@", StringComparison.Ordinal) )
        {
            body = body[2..];
        }

        if ( body.EndsWith("@/", StringComparison.Ordinal) )
        {
            body = body[..^2];
        }

        string name = "";
        string summary = "";
        string module = "";
        string callOn = "";
        string spmp = "";
        ImmutableArray<ScriptDocArgument>.Builder arguments = ImmutableArray.CreateBuilder<ScriptDocArgument>();
        ImmutableArray<string>.Builder examples = ImmutableArray.CreateBuilder<string>();

        foreach ( string line in body.Split('\n') )
        {
            Match match = s_keyValue.Match(line.TrimEnd());
            if ( !match.Success )
            {
                continue;
            }

            string key = match.Groups["key"].Value.ToLowerInvariant();
            string value = match.Groups["value"].Value.Trim();

            switch ( key )
            {
                case "name":
                    name = value;
                    break;
                case "summary":
                    summary = value;
                    break;
                case "module":
                    module = value;
                    break;
                case "callon":
                    callOn = value;
                    break;
                case "spmp":
                    spmp = value;
                    break;
                case "mandatoryarg":
                case "optionalarg":
                {
                    Match argumentMatch = s_argument.Match(value);
                    if ( argumentMatch.Success )
                    {
                        string argumentName = argumentMatch.Groups["name"].Value.Trim('<', '>', '[', ']');
                        string description = argumentMatch.Groups["desc"].Value.Trim();
                        arguments.Add(new ScriptDocArgument(argumentName, description, Optional: key == "optionalarg"));
                    }

                    break;
                }
                case "example":
                    examples.Add(value);
                    break;
                default:
                    break;
            }
        }

        return new ScriptDocComment
        {
            RawText = body.Trim(),
            Name = name,
            Summary = summary,
            Module = module,
            CallOn = callOn,
            Spmp = spmp,
            Arguments = arguments.ToImmutable(),
            Examples = examples.ToImmutable(),
        };
    }
}
