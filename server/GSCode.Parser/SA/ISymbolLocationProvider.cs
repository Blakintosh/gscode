using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

/// <summary>
/// Interface for providing symbol location lookups from a global registry.
/// This allows DefinitionsTable to query a workspace-wide symbol database
/// without creating a circular dependency between Parser and NET projects.
/// </summary>
public interface ISymbolLocationProvider
{
    /// <summary>
    /// Finds a function location by namespace and name.
    /// </summary>
    /// <param name="ns">The namespace to search in, or null to search all namespaces.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The file path and range if found, null otherwise.</returns>
    (string FilePath, Range Range)? FindFunctionLocation(string? ns, string name);

    /// <summary>
    /// Finds a class location by namespace and name.
    /// </summary>
    /// <param name="ns">The namespace to search in, or null to search all namespaces.</param>
    /// <param name="name">The class name.</param>
    /// <returns>The file path and range if found, null otherwise.</returns>
    (string FilePath, Range Range)? FindClassLocation(string? ns, string name);

    /// <summary>
    /// Finds a function location by name only, searching across all namespaces.
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <returns>The file path and range if found, null otherwise.</returns>
    (string FilePath, Range Range)? FindFunctionLocationAnyNamespace(string name);

    /// <summary>
    /// Finds a class location by name only, searching across all namespaces.
    /// </summary>
    /// <param name="name">The class name.</param>
    /// <returns>The file path and range if found, null otherwise.</returns>
    (string FilePath, Range Range)? FindClassLocationAnyNamespace(string name);

    /// <summary>
    /// Gets the parameters for a function by namespace and name.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The parameter names if found, null otherwise.</returns>
    string[]? GetFunctionParameters(string ns, string name);

    /// <summary>
    /// Gets the flags for a function by namespace and name.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The flags if found, null otherwise.</returns>
    string[]? GetFunctionFlags(string ns, string name);

    /// <summary>
    /// Gets the documentation for a function by namespace and name.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The documentation if found, null otherwise.</returns>
    string? GetFunctionDoc(string ns, string name);
}
