using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models
{
    public static class ScriptValueType
    {
        public const string Int = "int";
        public const string Float = "float";
        public const string String = "string";
        public const string Struct = "struct";
        public const string Vec3 = "vec3";
        public const string Bool = "bool";
        public const string Undefined = "undefined";
        public const string Object = "object";
        public const string FunctionPtr = "functionptr";
    }

    public enum ScriptStringModifier
    {
        None,
        CompileTimeHash,
        IString
    }

    /// <summary>
    /// Stores a script string literal value
    /// </summary>
    public sealed class ScriptStringValue
    {
        public ScriptStringModifier Modifier { get; init; }
        public string Value { get; init; } = default!;
    }

    /// <summary>
    /// Boxes script values for use in various situations.
    /// </summary>
    public sealed class ScriptValue
    {
        public string Type { get; init; } = default!;

        public object? Value { private get; init; } = default!;

        public T? Get<T>()
        {
            if (Value is T value)
            {
                return value;
            }
            throw new Exception($"Malformed Get<T> called on ScriptValue, type {typeof(T)} requested which could not be unboxed.");
        }    
    }
}
