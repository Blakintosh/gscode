using Newtonsoft.Json.Linq;

namespace GSCode.Server.Configuration;

/// <summary>
/// Reads the client's initializationOptions payload ({ "gscode": { ... } }).
/// Every accessor tolerates a missing or malformed section and returns null instead.
/// </summary>
public static class InitializationOptionsReader
{
    /// <summary>
    /// Extracts gscode.serverLogLevel from the raw initialize options, or null when absent.
    /// </summary>
    public static string? ReadServerLogLevel(JToken initializationOptions)
    {
        JToken? gscodeSection = initializationOptions["gscode"];
        if ( gscodeSection is null )
        {
            return null;
        }

        return gscodeSection.Value<string>("serverLogLevel");
    }
}
