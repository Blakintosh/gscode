using System.Collections.Frozen;
using System.Collections.Immutable;

namespace GSCode.Workspace.Api;

/// <summary>One parameter of a builtin overload.</summary>
public sealed record BuiltinParameter(string Name, string Description, bool Mandatory, string TypeText);

/// <summary>One overload of a builtin: the object it is called on (if any), its parameters, and its return.</summary>
public sealed record BuiltinOverload(
    string? CalledOn,
    ImmutableArray<BuiltinParameter> Parameters,
    string ReturnTypeText,
    bool ReturnsVoid);

/// <summary>
/// A builtin (engine) function. Builtins have NO namespace — resolution reaches them as
/// the fallback after the current namespace, and `sys::` is an explicit alias for them.
/// </summary>
public sealed record BuiltinFunction(
    string Name,
    string Description,
    ImmutableArray<BuiltinOverload> Overloads,
    string Example)
{
    /// <summary>True when any overload is called on an object (method-notation builtin).</summary>
    public bool IsMethod
    {
        get { return Overloads.Any(static overload => overload.CalledOn is not null); }
    }
}

/// <summary>The builtin library for one language, looked up case-insensitively by name.</summary>
public sealed class BuiltinApi
{
    private readonly FrozenDictionary<string, BuiltinFunction> _functions;

    /// <summary>An empty library (used when the API file is missing).</summary>
    public static BuiltinApi Empty { get; } = new(FrozenDictionary<string, BuiltinFunction>.Empty);

    public BuiltinApi(FrozenDictionary<string, BuiltinFunction> functions)
    {
        _functions = functions;
    }

    public int Count
    {
        get { return _functions.Count; }
    }

    public IEnumerable<BuiltinFunction> All
    {
        get { return _functions.Values; }
    }

    /// <summary>Finds a builtin by name (case-insensitive), or null.</summary>
    public BuiltinFunction? Find(string name)
    {
        return _functions.TryGetValue(name, out BuiltinFunction? function) ? function : null;
    }
}
