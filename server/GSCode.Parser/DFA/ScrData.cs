using GSCode.Parser.AST;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA.Sense;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.DFA;


/// <summary>
/// Specification:
/// any         -   Value could be any type, including not being defined at all.
///                 As it's v. difficult to get any idea of things being defined given many entry points (autoexec),
///                 level/self/etc. properties cannot be assumed to exist or be known and hence in general, will take
///                 unknown as their interim data. Likewise, parameters too, until implicit typing is implemented.
/// void        -   Never holds a value. This is an internal type for GSCode to process and probably shouldn't show up
///                 in errors. It indicates that the thing it's being returned from does not & cannot represent a value.
/// bool        -   Boolean. These resolve to 0/1 at runtime anyway, but we'll keep them distinct.
/// int         -   Integer number value
/// float       -   Floating point number value
/// number      -   Number. Equivalent to int | float, but used to indicate that any number is expected.
/// string      -   Strings
/// istring     -   (probably a) Interned string, used for special strings like localisation.
/// array       -   Arrays of ScrData - how these will be handled rn is not 100% certain. Need to consider Dictionary of ScrData
///                 ... may become its own data structure (need to see how GSC enumerates mixed usage, if it even allows that)
/// vec3        -   3-value tuple
/// struct      -   Script structs - Dictionary mapping string keys to ScrData values
/// entity      -   Like structs but distinct as they can come with preset values. (Derivatives..? Player, AI, etc.)
/// object      -   Not a high priority. But will store type etc. and dictionary for properties.
/// undefined   -   Undefined will store a null data pointer. A null ScrData is not used to represent undefined to prevent ambiguous
///                 cases such as no default value for a parameter.
/// </summary>
[Flags]
internal enum ScrDataTypes : uint
{
    // Ambiguous
    Any = ~0u & ~Error, // All bits set to 1 (except error), signifies that it could be any type

    // No type
    Void = 0,     // All bits set to 0, signifies that it has no type

    // Value types
    Bool = 1 << 0,
    Int = 1 << 1,      // this may get changed to include bool
    Float = 1 << 2,
    Number = Int | Float, // int or float

    // Reference types
    String = 1 << 3,
    // ReSharper disable once InconsistentNaming
    IString = (1 << 4) | String, // falls back to being a regular string if not found
    Array = 1 << 5,
    Vec3 = 1 << 6,
    Struct = 1 << 7,
    Entity = (1 << 8) | Struct, // extension of the struct type
    Object = 1 << 9,

    // Misc types
    Hash = 1 << 10,
    AnimTree = 1 << 11,
    Anim = 1 << 12,

    // Undefined
    Undefined = 1 << 13,

    // Error marker
    Error = 1 << 60
}

internal enum ScrInstanceTypes
{
    Constant,
    Property,
    Variable,
    None
}

/// <summary>
/// Boxed container for all data types in GSC, used for data flow analysis
/// </summary>
/// <param name="Type">The type of the data</param>
/// <param name="Value">An associated value, which could be a constant or a structure for reference types</param>
/// <param name="ReadOnly">Whether the field or variable on which this is accessed on is read-only/constant</param>
internal record struct ScrData(ScrDataTypes Type, object? Value = default, bool ReadOnly = false)
{
    public static ScrData Void { get; } = new(ScrDataTypes.Void);
    public static ScrData Default { get; } = new(ScrDataTypes.Any);
    public static ScrData Error { get; } = new(ScrDataTypes.Error);

    public ScrStruct? Owner { get; set; } = null;
    public string? FieldName { get; set; } = null;

    public static ScrData Undefined()
    {
        return new(ScrDataTypes.Undefined, null, false);
    }

    /// <summary>
    /// Deep-copies this data instance for use in other basic blocks.
    /// </summary>
    /// <returns>A deep-copied ScrData instance</returns>
    public ScrData Copy()
    {
        object? value = default;

        // Clone the struct members, if any, if it's a struct type
        if (IsStructType(Type) && Value is ScrStruct structData)
        {
            value = structData.Copy();
        }

        return new ScrData(Type, value, ReadOnly);
    }

    public ScrData GetField(string name)
    {
        // This is probably a failsafe, a struct check should occur earlier and be handled appropriately.
        // TODO: or it just becomes cannot get member {name} of type {type} diagnostic when the caller receives void.
        if (!IsStructType(Type))
        {
            return Void;
        }

        // If the value is null, we know nothing about it... assume any
        // TODO: keep this, but prefer non-deterministic empty structs to null Value
        if (Value is not ScrStruct structData)
        {
            return Default;
        }

        return structData.Get(name);
    }

    /// <summary>
    /// Produces a ScrData instance that represents the union of possible forms that a 
    /// series of incoming BasicBlocks' OUT set values can take.
    /// </summary>
    /// <param name="incoming">The OUT set entries from all incoming neighbours for a given key</param>
    /// <returns></returns>
    public static ScrData Merge(params ScrData[] incoming)
    {
        // Deep-copy if only one source
        if (incoming.Length == 1)
        {
            return incoming[0].Copy();
        }

        // Perform type inference, establish whether we'll look at values too.
        ScrDataTypes type = ScrDataTypes.Void;
        bool isReadOnly = true;

        foreach (ScrData data in incoming)
        {
            type |= data.Type;

            // Short-circuit if we've already established it's any
            if (type == ScrDataTypes.Any)
            {
                return Default;
            }

            if (!data.ReadOnly)
            {
                isReadOnly = false;
            }
        }

        object? value = default;
        if (IsStructType(type))
        {
            ScrStruct[] dataStructs = new ScrStruct[incoming.Length];

            // Establish whether we can merge all struct contents
            for (int i = 0; i < incoming.Length; i++)
            {
                if (incoming[i].Value is not ScrStruct incomingStruct)
                {
                    return new(type, value, isReadOnly);
                }

                dataStructs[i] = incomingStruct;
            }

            // OK, proceed
            value = ScrStruct.Merge(dataStructs);
        }

        return new(type, value, isReadOnly);
    }

    /// <summary>
    /// Returns whether this data instance is of type any.
    /// </summary>
    /// <returns></returns>
    public readonly bool IsAny()
    {
        return Type == ScrDataTypes.Any;
    }

    public string TypeToString()
    {
        if (Type == ScrDataTypes.Any)
        {
            return "?";
        }

        StringBuilder result = new();
        bool first = true;

        foreach (ScrDataTypes value in Enum.GetValues(typeof(ScrDataTypes)))
        {
            // Skip the "None" and "Unknown" values
            if (value == ScrDataTypes.Void || value == ScrDataTypes.Any)
                continue;

            if ((Type & value) == value)
            {
                if (!first)
                {
                    result.Append(" | ");
                }

                first = false;
                result.Append(value switch
                {
                    ScrDataTypes.Int => "int",
                    ScrDataTypes.Float => "float",
                    ScrDataTypes.Bool => "bool",
                    ScrDataTypes.String => "string",
                    ScrDataTypes.Array => "array",
                    ScrDataTypes.Vec3 => "vector",
                    ScrDataTypes.Struct => "struct",
                    ScrDataTypes.Entity => "entity",
                    ScrDataTypes.Object => "object",
                    ScrDataTypes.Undefined => "undefined",
                    _ => "any",
                });
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Gets whether the expression is of a type that can be boolean checked.
    /// </summary>
    /// <returns>true if it can be</returns>
    public bool CanEvaluateToBoolean()
    {
        return Type == ScrDataTypes.Int ||
            Type == ScrDataTypes.Bool ||
            Type == ScrDataTypes.Float ||
            Type == ScrDataTypes.String ||
            Type == ScrDataTypes.Array;
    }

    public bool? IsTruthy()
    {
        if (Value is not bool value)
        {
            return null;
        }

        return Type switch
        {
            ScrDataTypes.Int => (int)Value != 0,
            ScrDataTypes.Float => (float)Value != 0,
            ScrDataTypes.Bool => value,
            ScrDataTypes.String => (string)Value != "",
            ScrDataTypes.Array => true,
            ScrDataTypes.Vec3 => true,
            ScrDataTypes.Struct => true,
            ScrDataTypes.Entity => true,
            ScrDataTypes.Object => true,
            ScrDataTypes.Undefined => false,
            _ => null,
        };
    }

    public bool IsVoid()
    {
        return Type == ScrDataTypes.Void;
    }

    public bool IsNumeric()
    {
        return Type == ScrDataTypes.Int || Type == ScrDataTypes.Float;
    }

    public bool TypeUnknown()
    {
        return Type == ScrDataTypes.Any;
    }

    public bool ValueUnknown()
    {
        return Value is null || TypeUnknown();
    }

    /// <summary>
    /// Checks whether the data is of type(s) given in the parameters. They must belong to one up to all of these types
    /// but no types beyond this.
    /// If an instance of type Unknown is passed to the function, it will return true.
    /// </summary>
    /// <param name="types">The types to check</param>
    /// <returns></returns>
    public bool IsOfTypes(params ScrDataTypes[] types)
    {
        // Always assume true when Unknown
        if (TypeUnknown())
        {
            return true;
        }

        // Combine all the provided types into a single ScrDataTypes value.
        ScrDataTypes combinedType = 0;
        foreach (var type in types)
        {
            combinedType |= type;
        }

        // Return whether the bits are of the type specified
        return (Type & combinedType) == Type;
    }

    public readonly float? GetNumericValue()
    {
        if (Value is null)
        {
            return null;
        }

        if (Type == ScrDataTypes.Int)
        {
            return (int)Value;
        }
        else if (Type == ScrDataTypes.Float)
        {
            return (float)Value;
        }
        throw new InvalidOperationException("Cannot get numeric value of non-numeric type.");
    }

    public readonly T Get<T>()
    {
        if (Value is not T value)
        {
            throw new InvalidOperationException($"Cannot get value of type {typeof(T)} from type {Type}.");
        }

        return value;
    }

    public static ScrData FromDataExprNode(DataExprNode dataExprNode)
    {
        return new(dataExprNode.Type, dataExprNode.Value);
    }

    /// <summary>
    /// Returns whether this data instance is of type struct or entity.
    /// </summary>
    /// <returns></returns>
    private static bool IsStructType(ScrDataTypes type)
    {
        // Must be *exclusively* struct or entity, otherwise we're uninterested in cloning its data (as this should error on member access anyway).
        // TODO: This doesn't handle objects
        return type == ScrDataTypes.Struct || type == ScrDataTypes.Entity;
    }
}

internal record ScrParameter(string Name, Token Source, Range Range, bool ByRef = false, ExprNode? Default = null);
internal record ScrVariable(string Name, ScrData Data, int LexicalScope, bool Global = false);
// internal record ScrArguments(List<IExpressionNode> Arguments);
