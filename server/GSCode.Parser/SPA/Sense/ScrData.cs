using GSCode.Lexer.Types;
using GSCode.Parser.AST.Expressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Sense;

/// <summary>
/// Specification:
/// unknown     -   Value could be any type, including not being defined at all.
///                 As it's v. difficult to get any idea of things being defined given many entry points (autoexec),
///                 level/self/etc. properties cannot be assumed to exist or be known and hence in general, will take
///                 unknown as their interim data. Likewise, parameters too, until implicit typing is implemented.
/// void        -   Never holds a value. This is an internal type for GSCode to process and probably shouldn't show up
///                 in errors. It indicates that the thing it's being returned from does not & cannot represent a value.
/// int         -   Integer number value
/// float       -   Floating point number value
/// bool        -   Boolean. These resolve to 0/1 at runtime anyway, but we'll keep them distinct.
/// string      -   Strings
/// array       -   Arrays of ScrData - how these will be handled rn is not 100% certain. Need to consider Dictionary of ScrData
///                 ... may become its own data structure (need to see how GSC enumerates mixed usage, if it even allows that)
/// vector3d    -   3-value tuple
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
    Unknown = ~0u, // All bits set to 1, signifies that it could be any type

    // No type
    Void = 0,     // All bits set to 0, signifies that it has no type

    // Value types
    Int = 1 << 0,      // 00000001
    Float = 1 << 1,    // 00000010
    Bool = 1 << 2,     // 00000100

    // Reference types
    String = 1 << 3,   // 00001000
    Array = 1 << 4,    // 00010000
    Vector3d = 1 << 5, // 00100000
    Struct = 1 << 6,   // 01000000
    Entity = 1 << 7,   // 10000000
    Object = 1 << 8,   // 100000000

    // Undefined
    Undefined = 1 << 9, // 1000000000
}

internal enum ScrInstanceTypes
{
    Constant,
    Property,
    Variable,
    None
}

/// <summary>
/// Boxed container for all data types in GSC
/// </summary>
internal class ScrData
{
    public ScrDataTypes Type { get; set; }
    public object? Value { get; set; }
    public virtual ScrInstanceTypes Instance { get; } = ScrInstanceTypes.None;
    public virtual bool ReadOnly { get; protected init; } = false;
    public int SplitCount { get; set; } = 0;
    public bool CopyOnWrite => SplitCount > 0;

    public static ScrData Void { get; } = new() { Type = ScrDataTypes.Void };

    public static ScrData Default { get; } = new() { Type = ScrDataTypes.Unknown };

    protected ScrData() { }

    protected ScrData(ScrData from, bool deepCopy = false)
    {
        Type = from.Type;
        Value = from.Value;

        if(deepCopy)
        {
            if(Value is not null)
            {
                switch (Type)
                {
                    case ScrDataTypes.Struct:
                    case ScrDataTypes.Entity:
                        if (Value is ScrStruct scrStruct)
                        {
                            Value = scrStruct.DeepCopy(this);
                        }
                        break;
                }
            }
        }
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

    public void MarkSplit()
    {
        SplitCount++;
        if (Value is not null)
        {
            switch (Type)
            {
                case ScrDataTypes.Struct:
                case ScrDataTypes.Entity:
                    if (Value is ScrStruct scrStruct)
                    {
                        scrStruct.MarkSplit();
                    }
                    break;
            }
        }
    }

    public void UnmarkSplit()
    {
        SplitCount = Math.Max(SplitCount - 1, 0);
        if (Value is not null)
        {
            switch (Type)
            {
                case ScrDataTypes.Struct:
                case ScrDataTypes.Entity:
                    if (Value is ScrStruct scrStruct)
                    {
                        scrStruct.UnmarkSplit();
                    }
                    break;
            }
        }
    }

    public string TypeToString()
    {
        if (Type == ScrDataTypes.Unknown)
        {
            return "?";
        }

        StringBuilder result = new();
        bool first = true;

        foreach (ScrDataTypes value in Enum.GetValues(typeof(ScrDataTypes)))
        {
            // Skip the "None" and "Unknown" values
            if (value == ScrDataTypes.Void || value == ScrDataTypes.Unknown)
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
                    ScrDataTypes.Vector3d => "vec3d",
                    ScrDataTypes.Struct => "struct",
                    ScrDataTypes.Entity => "entity",
                    ScrDataTypes.Object => "object",
                    ScrDataTypes.Undefined => "undefined",
                    _ => "?",
                });
            }
        }

        return result.ToString();
    }

    public bool? IsTruthy()
    {
        if(Value is not bool value)
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
            ScrDataTypes.Vector3d => true,
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

    public bool TypeUnknown()
    {
        return Type == ScrDataTypes.Unknown;
    }

    public bool ValueUnknown()
    {
        return Value is null || TypeUnknown();
    }

    public ScrData GetIndex(object index)
    {
        if(Type != ScrDataTypes.Array)
        {
            throw new InvalidOperationException("Cannot get an index of a non-array type");
        }

        Dictionary<object, ScrData> arrayMembers = (Dictionary<object, ScrData>)Value!;

        if(index is not int && index is not string)
        {
            throw new InvalidOperationException($"{index} is not a valid array indexer.");
        }

        return arrayMembers[index];
    }

    public ScrData GetMember(string memberName)
    {
        if(Type != ScrDataTypes.Struct && 
            Type != ScrDataTypes.Entity &&
            Type != ScrDataTypes.Object &&
            Type != ScrDataTypes.Array)
        {
            return Void;
        }

        switch(Type)
        {
            case ScrDataTypes.Struct:
            case ScrDataTypes.Entity:
                // we can't make type-safe assumptions on an entity or struct.
                if(Value is not ScrStruct scrStruct)
                {
                    return Default;
                }

                ScrProperty member = scrStruct.GetMember(memberName);
                return member;
            case ScrDataTypes.Object:
                // Will add this at another point
                return Default;
            case ScrDataTypes.Array:
                if(memberName != "size")
                {
                    return Void;
                }

                return ScrProperty.ArraySize(this);
        }

        return ScrProperty.Undefined(memberName, this);
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

    public float? GetNumericValue()
    {
        if(Value is null)
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
        else
        {
            throw new InvalidOperationException("Cannot get numeric value of non-numeric type.");
        }
    }

    public T Get<T>()
    {
        if(Value is not T value)
        {
            throw new InvalidOperationException($"Cannot get value of type {typeof(T)} from type {Type}.");
        }

        return value;
    }
}

internal class ScrConstant : ScrData
{
    public override ScrInstanceTypes Instance { get; } = ScrInstanceTypes.Constant;
    public override bool ReadOnly { get; protected init; } = true;

    public ScrConstant(ScrDataTypes type, object? value = null)
    {
        Type = type;
        Value = value;

        // currently: used for arrays, which don't provide any type safety - this might change if type annotations are added.
    }

    public ScrConstant(Token sourceToken)
    {
        switch(sourceToken.Type)
        {
            case TokenType.Number:
                NumberTypes numberType = (NumberTypes) sourceToken.SubType!;
                switch (numberType)
                {
                    case NumberTypes.Int:
                        Type = ScrDataTypes.Int;
                        Value = int.Parse(sourceToken.Contents);
                        break;
                    case NumberTypes.Float:
                        Type = ScrDataTypes.Float;
                        Value = float.Parse(sourceToken.Contents);
                        break;
                    case NumberTypes.Hexadecimal:
                        Type = ScrDataTypes.Int;
                        Value = int.Parse(sourceToken.Contents[2..], System.Globalization.NumberStyles.HexNumber);
                        break;
                }
                break;
            case TokenType.ScriptString:
                StringTypes stringType = (StringTypes) sourceToken.SubType!;
                ReadOnlySpan<char> stringValue = sourceToken.Contents;

                int startIndex = 0;
                int length = stringValue.Length;

                switch (stringType)
                {
                    case StringTypes.SingleQuote:
                    case StringTypes.DoubleQuote:
                        startIndex = 1;
                        length -= 2;
                        break;
                    case StringTypes.SingleCompileHash:
                    case StringTypes.DoubleCompileHash:
                    case StringTypes.SinglePrecached:
                    case StringTypes.DoublePrecached:
                        startIndex = 2;
                        length -= 3;
                        break;
                }

                stringValue = stringValue.Slice(startIndex, length);

                Type = ScrDataTypes.String;
                Value = stringValue.ToString();
                break;
            case TokenType.Keyword:
                KeywordTypes keywordType = (KeywordTypes) sourceToken.SubType!;
                switch (keywordType)
                {
                    case KeywordTypes.True:
                        Type = ScrDataTypes.Bool;
                        Value = true;
                        break;
                    case KeywordTypes.False:
                        Type = ScrDataTypes.Bool;
                        Value = false;
                        break;
                    case KeywordTypes.Undefined:
                        Type = ScrDataTypes.Undefined;
                        break;
                    case KeywordTypes.AnimTree:
                    case KeywordTypes.Anim:
                        Type = ScrDataTypes.Unknown;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(keywordType), "Unknown keyword type.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sourceToken), "Unknown token type.");  
        }
    }
}

internal class ScrProperty : ScrData
{
    public override ScrInstanceTypes Instance { get; } = ScrInstanceTypes.Property;
    public override bool ReadOnly { get; protected init; }
    public string Name { get; }
    public ScrData Owner { get; }

    protected ScrProperty(string name, ScrData owner) 
    { 
        Name = name;
        Owner = owner;
    }

    public ScrProperty(string name, ScrData owner, ScrData value, bool readOnly = false, bool deepCopy = false) : base(value, deepCopy)
    {
        Name = name;
        Owner = owner;
        ReadOnly = readOnly;
    }

    public ScrProperty DeepCopy(ScrData owner)
    {
        return new(Name, owner, this, ReadOnly, true);
    }

    public static ScrProperty ArraySize(ScrData owner)
    {
        return new("size", owner)
        {
            Type = ScrDataTypes.Int,
            ReadOnly = true
        };
    }

    public static ScrProperty Undefined(string name, ScrData owner)
    {
        return new(name, owner)
        {
            Type = ScrDataTypes.Undefined
        };
    }

    public new static ScrProperty Default(string name, ScrData owner)
    {
        return new(name, owner)
        {
            Type = ScrDataTypes.Unknown
        };
    }

    public static ScrProperty Merge(ScrData owner, ScrProperty first, ScrProperty second)
    {
        // If either is unknown, the result is unknown
        if (first.Type == ScrDataTypes.Unknown || second.Type == ScrDataTypes.Unknown)
        {
            return new ScrProperty(first.Name, owner)
            {
                Type = ScrDataTypes.Unknown
            };
        }

        // If either is void, the result is void
        if (first.Type == ScrDataTypes.Void || second.Type == ScrDataTypes.Void)
        {
            return new ScrProperty(first.Name, owner)
            {
                Type = ScrDataTypes.Void
            };
        }

        // If the types are different, merge the types, don't attempt to merge values
        // Or equally if values are unknown.
        if (first.Type != second.Type || first.ValueUnknown() || second.ValueUnknown())
        {
            return new ScrProperty(first.Name, owner)
            {
                Type = first.Type | second.Type
            };
        }

        // If the types are the same, merge the values
        ScrProperty result = new(first.Name, owner)
        {
            Type = first.Type
        };

        switch(first.Type)
        {
            case ScrDataTypes.Struct:
            case ScrDataTypes.Entity:
                if(first.Value is ScrReservedStruct)
                {
                    result.Value = ScrReservedStruct.Merge(owner, (ScrReservedStruct)first.Value!, (ScrReservedStruct)second.Value!);
                    break;
                }
                result.Value = ScrStruct.Merge(owner, (ScrStruct)first.Value!, (ScrStruct)second.Value!);
                break;
            
        }

        return result;
    }
}

internal class ScrVariable : ScrData
{
    public override ScrInstanceTypes Instance { get; } = ScrInstanceTypes.Variable;
    public string Name { get; }
    public int Depth { get; }

    protected ScrVariable(string name, int depth)
    {
        Name = name;
        Depth = depth;
    }

    public ScrVariable(string name, ScrData value, int depth, bool isConstant = false) : base(value)
    {
        Name = name;
        Depth = depth;
        ReadOnly = isConstant;
    }

    public static ScrVariable Undefined(string name)
    {
        return new(name, 0)
        {
            Type = ScrDataTypes.Undefined
        };
    }

    public static ScrVariable Merge(ScrVariable first, ScrVariable second)
    {
        // If either is unknown, the result is unknown
        if(first.Type == ScrDataTypes.Unknown || second.Type == ScrDataTypes.Unknown)
        {
            return new ScrVariable(first.Name, first.Depth)
            {
                Type = ScrDataTypes.Unknown
            };
        }

        // If either is void, the result is void
        if(first.Type == ScrDataTypes.Void || second.Type == ScrDataTypes.Void)
        {
            return new ScrVariable(first.Name, first.Depth)
            {
                Type = ScrDataTypes.Void
            };
        }

        // If the types are different, merge the types, don't attempt to merge values
        // Or equally if values are unknown.
        if(first.Type != second.Type || first.ValueUnknown() || second.ValueUnknown())
        {
            return new ScrVariable(first.Name, first.Depth)
            {
                Type = first.Type | second.Type
            };
        }

        // If the types are the same, merge the values
        ScrVariable result = new(first.Name, first.Depth)
        {
            Type = first.Type
        };

        switch (first.Type)
        {
            case ScrDataTypes.Struct:
            case ScrDataTypes.Entity:
                if (first.Value is ScrReservedStruct)
                {
                    result.Value = ScrReservedStruct.Merge(result, (ScrReservedStruct)first.Value!, (ScrReservedStruct)second.Value!);
                    break;
                }
                result.Value = ScrStruct.Merge(result, (ScrStruct)first.Value!, (ScrStruct)second.Value!);
                break;

        }

        return result;
    }
}

internal class ScrArguments : ScrData
{
    public List<IExpressionNode> Arguments { get; init; }
}

internal class ScrParameter
{
    /// <summary>
    /// Name of the parameter.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Parameter data type (which isn't known normally)
    /// </summary>
    public ScrDataTypes Type { get; init; }

    /// <summary>
    /// The node to begin evaluation on default value.
    /// </summary>
    public IExpressionNode? DefaultNode { get; init; }

    /// <summary>
    /// The text range of the parameter.
    /// </summary>
    public Range Range { get; init; }
}