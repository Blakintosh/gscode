using GSCode.Data.Models;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
    Int = 1 << 1 | Bool,      // this may get changed to include bool
    Float = 1 << 2,
    Number = Int | Float, // int or float

    // Reference types
    String = 1 << 3,
    // ReSharper disable once InconsistentNaming
    IString = (1 << 4) | String, // falls back to being a regular string if not found
    Array = 1 << 5,
    Vector = 1 << 6,
    Struct = 1 << 7,
    Entity = 1 << 8,
    Object = 1 << 9,

    // Misc types
    Hash = 1 << 10,
    AnimTree = 1 << 11,
    Anim = 1 << 12,
    Function = 1 << 13,
    FunctionPointer = 1 << 14,

    // Undefined
    Undefined = 1 << 15,

    // UInt64
    UInt64 = 1 << 16,

    // Error marker
    Error = 1 << 60
}

internal enum ScrDataSubTypeKind
{
    ObjectInstance,
    Entity,
    FunctionReference
}

internal interface IScrDataSubType
{
    public ScrDataSubTypeKind Kind { get; }
}

internal record class ScrDataObjectInstanceType : IScrDataSubType
{
    public ScrDataSubTypeKind Kind => ScrDataSubTypeKind.ObjectInstance;
    public int ClassId { get; init; }
}

internal record class ScrDataEntityType : IScrDataSubType
{
    public ScrDataSubTypeKind Kind => ScrDataSubTypeKind.Entity;
    public ScrEntityTypes EntityType { get; init; }
}

internal record class ScrDataFunctionReferenceType : IScrDataSubType
{
    public ScrDataSubTypeKind Kind => ScrDataSubTypeKind.FunctionReference;
    public required ScrFunction Function { get; init; }
}

internal record struct ScrSetFieldFailure(
    ScrDataTypes? IncompatibleBaseTypes,
    ImmutableList<ScrEntitySetFieldFailureInfo>? EntityFailures,
    bool ArraySizeReadOnly = false
);

internal record struct ScrEntitySetFieldFailureInfo(
    ScrEntityTypes EntityType,
    ScrEntitySetFieldResult Reason
);

internal static class ScrDataTypeNames
{
    public const string Any = "any";
    public const string Void = "void";
    public const string Bool = "bool";
    public const string Int = "int";
    public const string Float = "float";
    public const string Number = "number";
    public const string String = "string";
    public const string IString = "istring";
    public const string Array = "array";
    public const string Vector = "vector";
    public const string Struct = "struct";
    public const string Entity = "entity";
    public const string Object = "object";
    public const string Hash = "hash";
    public const string AnimTree = "animtree";
    public const string Anim = "anim";
    public const string Function = "function";
    public const string FunctionPointer = "function*";
    public const string Undefined = "undefined";

    public static string TypeToString(ScrDataTypes type)
    {
        if (IsAny(type))
        {
            return ScrDataTypeNames.Any;
        }

        StringBuilder result = new();
        bool first = true;

        foreach (ScrDataTypes value in Enum.GetValues(typeof(ScrDataTypes)))
        {
            // Skip the "None" and "Unknown" values
            if (value == ScrDataTypes.Void || value == ScrDataTypes.Any)
            {
                continue;
            }

            if ((type & value) == value)
            {
                // Skip the Error marker type
                if (value == ScrDataTypes.Error)
                {
                    continue;
                }
                // Skip base types when superset/union types are present
                // Number is Int | Float, so skip Int and Float if Number is present
                if (value == ScrDataTypes.Int && (type & ScrDataTypes.Number) == ScrDataTypes.Number)
                {
                    continue;
                }
                if (value == ScrDataTypes.Float && (type & ScrDataTypes.Number) == ScrDataTypes.Number)
                {
                    continue;
                }
                // Int is a superset of Bool, so skip Bool if Int is present
                if (value == ScrDataTypes.Bool && (type & ScrDataTypes.Int) == ScrDataTypes.Int)
                {
                    continue;
                }
                // IString is a superset of String, so skip String if IString is present
                if (value == ScrDataTypes.String && (type & ScrDataTypes.IString) == ScrDataTypes.IString)
                {
                    continue;
                }

                if (!first)
                {
                    result.Append(" | ");
                }

                first = false;
                result.Append(value switch
                {
                    ScrDataTypes.Int => ScrDataTypeNames.Int,
                    ScrDataTypes.Float => ScrDataTypeNames.Float,
                    ScrDataTypes.Number => ScrDataTypeNames.Number,
                    ScrDataTypes.Bool => ScrDataTypeNames.Bool,
                    ScrDataTypes.String => ScrDataTypeNames.String,
                    ScrDataTypes.IString => ScrDataTypeNames.IString,
                    ScrDataTypes.Array => ScrDataTypeNames.Array,
                    ScrDataTypes.Vector => ScrDataTypeNames.Vector,
                    ScrDataTypes.Struct => ScrDataTypeNames.Struct,
                    ScrDataTypes.Entity => ScrDataTypeNames.Entity,
                    ScrDataTypes.Object => ScrDataTypeNames.Object,
                    ScrDataTypes.Hash => ScrDataTypeNames.Hash,
                    ScrDataTypes.AnimTree => ScrDataTypeNames.AnimTree,
                    ScrDataTypes.Anim => ScrDataTypeNames.Anim,
                    ScrDataTypes.Function => ScrDataTypeNames.Function,
                    ScrDataTypes.FunctionPointer => ScrDataTypeNames.FunctionPointer,
                    ScrDataTypes.Undefined => ScrDataTypeNames.Undefined,
                    _ => ScrDataTypeNames.Any,
                });
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Returns whether this data instance is of type any.
    /// </summary>
    /// <returns></returns>
    private static bool IsAny(ScrDataTypes type)
    {
        // "Any" is our unknown-type marker. We also treat "Any except undefined" as unknown,
        // which is how flow analysis can represent "unknown but definitely defined".
        return type == ScrDataTypes.Any ||
               type == (ScrDataTypes.Any & ~ScrDataTypes.Undefined);
    }
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
internal record struct ScrData
{
    /// <summary>
    /// The type of the data, which could be a single type or a union of types.
    /// </summary>
    public ScrDataTypes Type { get; init; }

    /// <summary>
    /// Whether the field or variable on which this is accessed on is read-only/constant.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// The boolean value of the data, if known.
    /// </summary>
    public bool? BooleanValue { get; }

    /// <summary>
    /// When <see cref="Type"/> is <see cref="ScrDataTypes.Struct"/> or <see cref="ScrDataTypes.Entity"/>,
    /// this set contains the sub-types that this data instance can be, if known.
    /// </summary>
    public ImmutableHashSet<IScrDataSubType>? SubTypes { get; }

    public ScrData(ScrDataTypes type, bool readOnly = false)
    {
        Type = type;
        BooleanValue = type switch
        {
            // Always truthy
            ScrDataTypes.Array => true,
            ScrDataTypes.Vector => true,
            ScrDataTypes.Struct => true,
            ScrDataTypes.Entity => true,
            ScrDataTypes.Object => true,
            // Always falsy
            ScrDataTypes.Undefined => false,
            // Unknown
            _ => null,
        };
        ReadOnly = readOnly;
    }

    public ScrData(ScrDataTypes type, bool? booleanValue, bool readOnly = false)
    {
        Type = type;
        BooleanValue = booleanValue;
        ReadOnly = readOnly;
    }

    public ScrData(ScrDataTypes type, IEnumerable<IScrDataSubType>? subTypes, bool? booleanValue = null, bool readOnly = false)
    {
        Type = type;
        BooleanValue = booleanValue;
        SubTypes = subTypes?.ToImmutableHashSet();
        ReadOnly = readOnly;
    }

    public static ScrData Void { get; } = new(ScrDataTypes.Void);
    public static ScrData Default { get; } = new(ScrDataTypes.Any);
    public static ScrData Error { get; } = new(ScrDataTypes.Error);

    public string? FieldName { get; set; } = null;

    public static ScrData Undefined()
    {
        return new(ScrDataTypes.Undefined);
    }

    public static ScrData Function(ScrFunction function)
    {
        return new(ScrDataTypes.Function, 
            [new ScrDataFunctionReferenceType { Function = function }]);
    }

    public static ScrData FunctionPointer(ScrFunction function)
    {
        return new(ScrDataTypes.FunctionPointer,
            [new ScrDataFunctionReferenceType { Function = function }]);
    }

    /// <summary>
    /// Converts an API data type specification to an internal ScrData instance.
    /// </summary>
    /// <param name="apiType">The API type specification</param>
    /// <returns>The corresponding ScrData</returns>
    public static ScrData FromApiType(ScrFunctionDataType? apiType)
    {
        if (apiType is null)
        {
            return Default;
        }

        // Handle array types - map any array to just 'array' regardless of element type
        if (apiType.IsArray)
        {
            return new ScrData(ScrDataTypes.Array);
        }

        return ParseSingleApiType(apiType.DataType, apiType.InstanceType);
    }

    /// <summary>
    /// Converts a list of API data types (union) to an internal ScrData instance.
    /// Types are BitOr'd together and subtypes are collected.
    /// </summary>
    /// <param name="apiTypes">The list of API type specifications</param>
    /// <returns>The corresponding ScrData representing the union</returns>
    public static ScrData FromApiTypes(IEnumerable<ScrFunctionDataType>? apiTypes)
    {
        if (apiTypes is null)
        {
            return Default;
        }

        ScrDataTypes combinedType = ScrDataTypes.Void;
        List<IScrDataSubType>? subTypes = null;

        foreach (var apiType in apiTypes)
        {
            // Handle array types - map any array to just 'array'
            if (apiType.IsArray)
            {
                combinedType |= ScrDataTypes.Array;
                continue;
            }

            var (type, subType) = ParseSingleApiTypeWithSubType(apiType.DataType, apiType.InstanceType);
            combinedType |= type;

            if (subType is not null)
            {
                subTypes ??= new();
                subTypes.Add(subType);
            }
        }

        if (combinedType == ScrDataTypes.Void)
        {
            return Default;
        }

        return new ScrData(combinedType, subTypes);
    }

    /// <summary>
    /// Parses a single API data type string to the corresponding ScrDataTypes.
    /// </summary>
    private static ScrData ParseSingleApiType(string dataType, string? instanceType)
    {
        var (type, subType) = ParseSingleApiTypeWithSubType(dataType, instanceType);

        if (subType is not null)
        {
            return new ScrData(type, [subType]);
        }

        return new ScrData(type);
    }

    /// <summary>
    /// Parses a single API data type string to the corresponding ScrDataTypes and optional subtype.
    /// </summary>
    private static (ScrDataTypes Type, IScrDataSubType? SubType) ParseSingleApiTypeWithSubType(string dataType, string? instanceType)
    {
        // Normalize the type name
        string normalizedType = dataType.ToLowerInvariant().Trim();

        return normalizedType switch
        {
            // Primitives
            "int" or "integer" => (ScrDataTypes.Int, null),
            "float" => (ScrDataTypes.Float, null),
            "bool" or "boolean" => (ScrDataTypes.Bool, null),
            "string" => (ScrDataTypes.String, null),
            "istring" or "localizedstring" => (ScrDataTypes.IString, null),
            "number" => (ScrDataTypes.Number, null), // int | float
            "vector" or "vec3" => (ScrDataTypes.Vector, null),
            "hash" => (ScrDataTypes.Hash, null),
            "undefined" => (ScrDataTypes.Undefined, null),
            "void" => (ScrDataTypes.Void, null),

            // Complex types
            "array" => (ScrDataTypes.Array, null),
            "struct" => (ScrDataTypes.Struct, null),
            "function" => (ScrDataTypes.Function, null),
            "function*" or "functionpointer" => (ScrDataTypes.FunctionPointer, null),

            // Entity types - check for instance type
            "entity" => ParseEntityType(instanceType),

            // Enums map to any
            "enum" => (ScrDataTypes.Any, null),

            // Any/unknown
            "any" or "" => (ScrDataTypes.Any, null),

            // Default fallback for unknown types
            _ => (ScrDataTypes.Any, null)
        };
    }

    /// <summary>
    /// Parses an entity type, potentially with an instance type subtype.
    /// </summary>
    private static (ScrDataTypes Type, IScrDataSubType? SubType) ParseEntityType(string? instanceType)
    {
        // If no instance type or 'any', just return entity with no subtype
        if (string.IsNullOrWhiteSpace(instanceType) ||
            instanceType.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return (ScrDataTypes.Entity, null);
        }

        // Try to map the instance type to a known entity type
        ScrEntityTypes? entityType = instanceType.ToLowerInvariant() switch
        {
            "weapon" => ScrEntityTypes.Weapon,
            "vehicle" => ScrEntityTypes.Vehicle,
            "player" => ScrEntityTypes.Player,
            "actor" => ScrEntityTypes.Actor,
            "aitype" or "ai_type" => ScrEntityTypes.AiType,
            "pathnode" or "path_node" => ScrEntityTypes.PathNode,
            "sentient" => ScrEntityTypes.Sentient,
            "vehiclenode" or "vehicle_node" => ScrEntityTypes.VehicleNode,
            "hudelem" or "hud_elem" => ScrEntityTypes.HudElem,
            _ => null
        };

        if (entityType.HasValue)
        {
            return (ScrDataTypes.Entity, new ScrDataEntityType { EntityType = entityType.Value });
        }

        // Unknown instance type - return entity without subtype
        return (ScrDataTypes.Entity, null);
    }

    public bool TryGetFunction([NotNullWhen(true)] out ScrFunction? function)
    {
        if (SubTypes is not null)
        {
            foreach (var subType in SubTypes)
            {
                if (subType is ScrDataFunctionReferenceType funcRef)
                {
                    function = funcRef.Function;
                    return true;
                }
            }
        }
        function = null;
        return false;
    }

    public bool FunctionUnknown()
    {
        return !TryGetFunction(out _);
    }

    /// <summary>
    /// Deep-copies this data instance for use in other basic blocks.
    /// </summary>
    /// <returns>A deep-copied ScrData instance</returns>
    public ScrData Copy()
    {
        return new(Type, SubTypes, BooleanValue, ReadOnly);
    }

    [return: NotNullIfNotNull(nameof(incompatibleType))]
    public ScrData TryGetField(string name, out ScrDataTypes? incompatibleType)
    {
        // Helper to set FieldName on result
        ScrData WithFieldName(ScrData result)
        {
            result.FieldName = name;
            return result;
        }

        // If it's any, then assume it's compatible with any field.
        if(IsAny())
        {
            incompatibleType = null;
            return WithFieldName(Default);
        }

        // Compute what types are left after removing struct, entity and object types.
        // If there are any types left, then the data is incompatible with the field.
        // Special case: arrays have a readonly "size" field, so include Array in the mask when accessing "size".
        ScrDataTypes fieldMask = ScrDataTypes.Struct | ScrDataTypes.Entity | ScrDataTypes.Object;
        if (name == "size")
        {
            fieldMask |= ScrDataTypes.Array;
        }
        ScrDataTypes residualTypes = Type & ~fieldMask;
        if (residualTypes != ScrDataTypes.Void)
        {
            incompatibleType = residualTypes;
            return WithFieldName(Void);
        }
        incompatibleType = null;

        // If it's a struct, assume any field is compatible.
        if(HasType(ScrDataTypes.Struct))
        {
            return WithFieldName(Default);
        }
        // If it's an object, assume any field is compatible, because (at least as of now) all fields are any, and you can add custom fields to it too.
        if(HasType(ScrDataTypes.Object))
        {
            return WithFieldName(Default);
        }

        // For Entity, we'll have their sub-type records to check.
        // For Array accessing "size", it's a readonly integer.
        ScrDataTypes compositeType = ScrDataTypes.Void;
        // Start as true, as if any is not readonly, then we'll represent it as not.
        bool isReadOnly = true;

        // Handle array "size" field
        if (name == "size" && HasType(ScrDataTypes.Array))
        {
            compositeType |= ScrDataTypes.Int;
            // isReadOnly stays true - size is always readonly
        }

        if(SubTypes is not null)
        {
            foreach(IScrDataSubType subType in SubTypes)
            {
                if(subType.Kind == ScrDataSubTypeKind.Entity)
                {
                    ScrDataEntityType entityType = (ScrDataEntityType)subType;

                    ScrData field = ScrEntityRegistry.GetField(entityType.EntityType, name);
                    compositeType |= field.Type;
                    isReadOnly &= field.ReadOnly;

                    // Tracking boolean unnecessary, as we'll never receive a not-null boolean value from this location.
                }
            }
        }

        // If we didn't find any fields, return default
        if (compositeType == ScrDataTypes.Void)
        {
            return WithFieldName(Default);
        }

        return WithFieldName(new ScrData(compositeType, readOnly: isReadOnly));
    }

    public bool TrySetField(string name, ScrData value, out ScrSetFieldFailure? failure)
    {
        // 1. Check for incompatible base types (same as TryGetField)
        // If it's any, then assume it's compatible with any field.
        if (IsAny())
        {
            failure = null;
            return true;
        }

        // Compute what types are left after removing struct, entity and object types.
        // If there are any types left, then the data is incompatible with the field.
        // Special case: arrays have a readonly "size" field, so include Array in the mask when accessing "size".
        ScrDataTypes fieldMask = ScrDataTypes.Struct | ScrDataTypes.Entity | ScrDataTypes.Object;
        if (name == "size")
        {
            fieldMask |= ScrDataTypes.Array;
        }
        ScrDataTypes residualTypes = Type & ~fieldMask;
        if (residualTypes != ScrDataTypes.Void)
        {
            failure = new(IncompatibleBaseTypes: residualTypes, EntityFailures: null);
            return false;
        }

        // 2. If Struct or Object - they accept any field assignment
        if (HasType(ScrDataTypes.Struct) || HasType(ScrDataTypes.Object))
        {
            failure = null;
            return true;
        }

        // 3. Check for array "size" field - it's readonly
        bool arraySizeReadOnly = name == "size" && HasType(ScrDataTypes.Array);

        // 4. Check each entity sub-type
        List<ScrEntitySetFieldFailureInfo>? failures = null;

        if (SubTypes != null)
        {
            foreach (IScrDataSubType subType in SubTypes)
            {
                if (subType is ScrDataEntityType entityType)
                {
                    var result = ScrEntityRegistry.SetField(entityType.EntityType, name, value);
                    if (result != ScrEntitySetFieldResult.Success)
                    {
                        failures ??= new();
                        failures.Add(new(entityType.EntityType, result));
                    }
                }
            }
        }

        if (failures is not null || arraySizeReadOnly)
        {
            failure = new(IncompatibleBaseTypes: null, EntityFailures: failures?.ToImmutableList(), ArraySizeReadOnly: arraySizeReadOnly);
            return false;
        }

        failure = null;
        return true;
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
        bool? booleanValue = true;
        List<IScrDataSubType>? subTypes = null;

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

            if(data.SubTypes is not null)
            {
                subTypes ??= new();
                subTypes.AddRange(data.SubTypes);
            }

            if(data.BooleanValue is not null)
            {
                booleanValue = ComposeBooleanValues(booleanValue, data.BooleanValue);
            }
        }

        return new(type, subTypes, booleanValue, isReadOnly);
    }

    /// <summary>
    /// Returns whether this data instance is of type any.
    /// </summary>
    /// <returns></returns>
    public readonly bool IsAny()
    {
        // "Any" is our unknown-type marker. We also treat "Any except undefined" as unknown,
        // which is how flow analysis can represent "unknown but definitely defined".
        return Type == ScrDataTypes.Any ||
               Type == (ScrDataTypes.Any & ~ScrDataTypes.Undefined);
    }

    public readonly bool HasType(ScrDataTypes type)
    {
        return (Type & type) == type;
    }

    public string TypeToString()
    {
        return ScrDataTypeNames.TypeToString(Type);
    }

    /// <summary>
    /// Gets whether the expression is of a type that can be boolean checked.
    /// </summary>
    /// <returns>true if it can be</returns>
    public readonly bool CanEvaluateToBoolean()
    {
        return Type == ScrDataTypes.Int ||
            Type == ScrDataTypes.Bool ||
            Type == ScrDataTypes.Float ||
            Type == ScrDataTypes.String ||
            Type == ScrDataTypes.Array;
    }

    public bool? IsTruthy()
    {
       return BooleanValue;
    }

    public readonly bool IsVoid()
    {
        return Type == ScrDataTypes.Void;
    }

    public readonly bool IsNumeric()
    {
        // True if type contains only numeric types (Int and/or Float, but nothing else)
        // This handles union types like Int | Float from CFG merges
        return Type != ScrDataTypes.Void && (Type & ~ScrDataTypes.Number) == ScrDataTypes.Void;
    }

    public readonly bool TypeUnknown()
    {
        return IsAny();
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

    public static ScrData FromDataExprNode(DataExprNode dataExprNode)
    {
        return new(dataExprNode.Type, booleanValue: dataExprNode.Type switch
        {
            ScrDataTypes.Bool => (bool)dataExprNode.Value!,
            ScrDataTypes.Int => (int)dataExprNode.Value! > 0,
            ScrDataTypes.Float => (float)dataExprNode.Value! > 0,
            ScrDataTypes.String => (string)dataExprNode.Value! != "",
            ScrDataTypes.Array => true,
            ScrDataTypes.Vector => true,
            ScrDataTypes.Struct => true,
            ScrDataTypes.Entity => true,
            ScrDataTypes.Object => true,
            ScrDataTypes.Undefined => false,
            _ => null,
        });
    }

    private static bool? ComposeBooleanValues(bool? value1, bool? value2)
    {
        if(value1 is null || value2 is null)
        {
            return null;
        }
        return value1.Value && value2.Value;
    }
}

internal record ScrParameter(string Name, Token Source, Range Range, bool ByRef = false, ExprNode? Default = null);
internal record ScrVariable(string Name, ScrData Data, int LexicalScope, bool Global = false, bool IsConstant = false, Range? SourceLocation = null, AstNode? DefinitionSource = null);
// internal record ScrArguments(List<IExpressionNode> Arguments);
