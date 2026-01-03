using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace GSCode.Parser.DFA;

internal record class ScrClassField(ScrDataTypes Type, bool ReadOnly = false);

/// <summary>
/// A registry that tracks user-defined classes, their fields, and methods. 
/// </summary>
internal class ScrClassRegistry
{
    // private Dictionary<string, ScrClassDefinition> _classes = new();

    public ScrData GetField(string className, string fieldName)
    {
        // TODO: implement.
        return ScrData.Default;
    }

    public ScrClassSetFieldResult SetField(string className, string fieldName, ScrData value)
    {
        return ScrClassSetFieldResult.Success;
    }
}

internal enum ScrClassSetFieldResult
{
    Success,
    FieldNotFound
}