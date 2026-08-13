using System.Collections.Frozen;
using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Api;

/// <summary>How far the bundled data can be trusted about one entry.</summary>
/// <remarks>
/// Present in every bundled library and discarded by the loader until now: Black Ops III's GSC file
/// alone carries 1,291 <c>high</c>, 684 <c>medium</c> and 80 <c>low</c>. It is the honest source for
/// <see cref="ScrImprecision.BuiltinUnverified"/> — a low-confidence declared type is not the same
/// fact as a verified one, and v1.5 shipped an entire second diagnostic code
/// (<c>ArgumentTypeMismatchUnverified</c>) because it had nowhere else to put the distinction.
/// </remarks>
public enum BuiltinConfidence
{
    Unstated = 0,
    Low,
    Medium,
    High,
}

/// <summary>One parameter of a builtin overload.</summary>
/// <param name="TypeText">The declared type as written, kept for display.</param>
/// <param name="Types">
/// The same thing parsed onto the lattice, once at load rather than re-switched per call.
/// <see cref="ScrTypeSet.None"/> means the spelling is one the lattice cannot express.
///
/// NOT DEAD, though nothing reads it: this is what v1.5's <c>ArgumentTypeMismatch</c> checks a call's
/// arguments against, and it is parsed here so that rule needs no plumbing when it is measured. Do
/// not delete it as unused — see FOLLOWUPS.md, which records that the blocker is the corpus sweep
/// rather than the data. <see cref="BuiltinFunction.Confidence"/> is the other half, and supplies
/// the severity split that v1.5 spent a whole second diagnostic code on.
/// </param>
/// <param name="IsVariadic">
/// The parameter pack. Spelled as the <c>vararg</c> DATA TYPE rather than the JSON's
/// <c>variadic</c> flag, which is present 55 times in BO3's GSC library and null in every one of
/// them — so the flag never carried the fact and the type spelling always did.
/// </param>
public sealed record BuiltinParameter(
    string Name,
    string Description,
    bool Mandatory,
    string TypeText,
    ScrTypeSet Types = ScrTypeSet.None,
    bool IsVariadic = false);

/// <summary>One overload of a builtin: the object it is called on (if any), its parameters, and its return.</summary>
/// <param name="ReturnTypes">The return type parsed onto the lattice; see <see cref="BuiltinParameter.Types"/>.</param>
/// <param name="CalledOnTypes">The type of the object this is called on, when the data states one.</param>
public sealed record BuiltinOverload(
    string? CalledOn,
    ImmutableArray<BuiltinParameter> Parameters,
    string ReturnTypeText,
    bool ReturnsVoid,
    ScrTypeSet ReturnTypes = ScrTypeSet.None,
    ScrTypeSet CalledOnTypes = ScrTypeSet.None);

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
    /// <summary>
    /// Exists only in a development build, so calling it outside a `/# #/` block breaks a
    /// shipped mod. Populated by the loader — see DevOnlyBuiltins for where the truth lives.
    /// </summary>
    public bool IsDevOnly { get; init; }

    /// <summary>How far this entry's declared types can be trusted. See <see cref="BuiltinConfidence"/>.</summary>
    public BuiltinConfidence Confidence { get; init; }

    /// <summary>True when any overload is called on an object (method-notation builtin).</summary>
    public bool IsMethod
    {
        get { return Overloads.Any(static overload => overload.CalledOn is not null); }
    }

    /// <summary>
    /// The return type across EVERY overload, as a union.
    ///
    /// The typer read overload zero and no further, with no agreement check — so a builtin whose
    /// overloads return different things was reported as returning whichever happened to be listed
    /// first. A union is both honest and, on this lattice, usable.
    /// </summary>
    public ScrTypeSet ReturnTypes
    {
        get
        {
            ScrTypeSet union = ScrTypeSet.None;
            foreach ( BuiltinOverload overload in Overloads )
            {
                // One overload the lattice cannot express makes the whole answer unknowable: the
                // call might take that overload.
                if ( overload.ReturnTypes == ScrTypeSet.None && !overload.ReturnsVoid )
                {
                    return ScrTypeSet.None;
                }

                union |= overload.ReturnTypes;
            }

            return union;
        }
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
