using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.DFA;

internal enum SetResult
{
    Success,
    NewField,
    ReadOnly,
    TypeMismatch,
    Immutable
}

internal class ScrArray //: IEnumerable<ScrData>
{
    /// <summary>
    /// The elements of the array.
    /// </summary>
    public Dictionary<object, ScrData>? Elements { get; set; }
}

/// <summary>
/// Base class for all script types that can have fields (Structs, Entities, Objects)
/// </summary>
internal abstract class ScrObject
{
    internal Dictionary<string, ScrData> Fields { get; } = new();
    public bool IsDeterministic { get; internal init; } = false;

    public override bool Equals(object? obj)
    {
        return Equals(obj as ScrObject);
    }

    public virtual bool Equals(ScrObject? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (GetType() != other.GetType())
        {
            return false;
        }
        if (IsDeterministic != other.IsDeterministic)
        {
            return false;
        }
        if (Fields.Count != other.Fields.Count)
        {
            return false;
        }

        foreach (var kvp in Fields)
        {
            if (!other.Fields.TryGetValue(kvp.Key, out var otherValue))
            {
                return false;
            }
            if (!kvp.Value.Equals(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(GetType(), IsDeterministic, Fields.Count);
    }

    protected ScrObject(IEnumerable<KeyValuePair<string, ScrData>> fields, bool deterministic = false)
    {
        IsDeterministic = deterministic;
        foreach (KeyValuePair<string, ScrData> field in fields)
        {
            Fields.Add(field.Key, field.Value with { Owner = this, FieldName = null });
        }
    }

    public ScrData Get(string fieldName)
    {
        if (!Fields.TryGetValue(fieldName, out ScrData value))
        {
            return (IsDeterministic ? ScrData.Undefined() : ScrData.Default)
                with
            { Owner = this, FieldName = fieldName };
        }

        return value with { Owner = this, FieldName = fieldName };
    }

    public virtual SetResult Set(string fieldName, ScrData value)
    {
        bool isNew = !Fields.ContainsKey(fieldName);
        Fields[fieldName] = value with { Owner = null, FieldName = null };
        return isNew ? SetResult.NewField : SetResult.Success;
    }

    public abstract ScrObject Copy();

    protected void CopyFieldsTo(ScrObject other)
    {
        foreach (KeyValuePair<string, ScrData> field in Fields)
        {
            other.Set(field.Key, field.Value.Copy());
        }
    }
}

/// <summary>
/// Standard script structure
/// </summary>
internal class ScrStruct : ScrObject
{
    public ScrStruct() : base([]) { }

    public ScrStruct(IEnumerable<KeyValuePair<string, ScrData>> fields, bool deterministic = false) 
        : base(fields, deterministic)
    {
    }

    public static ScrStruct Deterministic(params KeyValuePair<string, ScrData>[] fields)
    {
        return new ScrStruct(fields, true);
    }

    public static ScrStruct NonDeterministic(params KeyValuePair<string, ScrData>[] fields)
    {
        return new ScrStruct(fields, false);
    }

    /// <summary>
    /// Deep-copies this struct instance.
    /// </summary>
    /// <returns></returns>
    public override ScrObject Copy()
    {
        ScrStruct newStruct = new([], IsDeterministic);
        CopyFieldsTo(newStruct);
        return newStruct;
    }

    /// <summary>
    /// Produces a 'union' of the incoming objects, merging them together from their possible forms.
    /// </summary>
    public static T Merge<T>(params T[] incoming) where T : ScrObject, new()
    {
        if (incoming.Length == 1)
        {
            return (T)incoming[0].Copy();
        }

        HashSet<string> fieldNames = new();
        bool isDeterministic = true;

        foreach (T obj in incoming)
        {
            fieldNames.UnionWith(obj.Fields.Keys);
            isDeterministic &= obj.IsDeterministic;
        }

        T merged = new() { IsDeterministic = isDeterministic };
        foreach (string name in fieldNames)
        {
            ScrData[] values = incoming.Select(s => s.Get(name)).ToArray();
            ScrData mergedValue = ScrData.Merge(values);

            if (!isDeterministic && mergedValue.IsAny())
            {
                continue;
            }

            merged.Set(name, mergedValue);
        }

        return merged;
    }
}
