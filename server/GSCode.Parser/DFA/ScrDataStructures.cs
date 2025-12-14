using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.DFA;

internal class ScrArray //: IEnumerable<ScrData>
{
    /// <summary>
    /// If the array is being used strictly as an array, then this will be used
    /// </summary>
    public List<ScrData>? ArrayElements { get; set; }

    /// <summary>
    /// If map elements have been used, then this will be used.
    /// </summary>
    public Dictionary<object, ScrData>? MapElements { get; set; }
}

/// <summary>
/// Standard script structure
/// </summary>
internal record ScrStruct
{
    private Dictionary<string, ScrData> Fields { get; } = new();
    public bool IsDeterministic { get; } = false;

    public virtual bool Equals(ScrStruct? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
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
        // Simple hash combining IsDeterministic and field count
        // Full content hashing would be expensive and isn't needed for correctness
        return HashCode.Combine(IsDeterministic, Fields.Count);
    }

    protected ScrStruct(IEnumerable<KeyValuePair<string, ScrData>> fields, bool deterministic = false)
    {
        IsDeterministic = deterministic;
        foreach (KeyValuePair<string, ScrData> field in fields)
        {
            // Don't store the metadata, because otherwise it creates a cycle for equality checks.
            Fields.Add(field.Key, field.Value with { Owner = null, FieldName = null });
        }
    }

    public static ScrStruct Deterministic(params KeyValuePair<string, ScrData>[] fields)
    {
        return new ScrStruct(fields, true);
    }

    public static ScrStruct NonDeterministic(params KeyValuePair<string, ScrData>[] fields)
    {
        return new ScrStruct(fields, false);
    }

    public ScrData Get(string fieldName)
    {
        if (!Fields.TryGetValue(fieldName, out ScrData value))
        {
            return (IsDeterministic ? ScrData.Undefined() : ScrData.Default)
                with
            { Owner = this, FieldName = fieldName };
        }

        // Always attach target metadata for member access, even when the field exists.
        // This is used by the analyser to identify assignment destinations (a.b = ...).
        return value with { Owner = this, FieldName = fieldName };
    }

    public bool Set(string fieldName, ScrData value)
    {
        bool isNew = !Fields.ContainsKey(fieldName);

        // Store the value without any target metadata to avoid cycles.
        Fields[fieldName] = value with { Owner = null, FieldName = null };
        return isNew;
    }

    /// <summary>
    /// Deep-copies this struct instance.
    /// </summary>
    /// <returns></returns>
    public ScrStruct Copy()
    {
        List<KeyValuePair<string, ScrData>> fields = [];
        ScrStruct newStruct = new(fields, IsDeterministic);

        foreach (KeyValuePair<string, ScrData> field in Fields)
        {
            newStruct.Set(field.Key, field.Value.Copy());
        }

        return newStruct;
    }

    /// <summary>
    /// Produces a 'union' of the incoming structs, merging them together from their possible forms.
    /// </summary>
    /// <param name="incoming">The struct instances present at incoming nodes to the basic block.</param>
    /// <returns></returns>
    public static ScrStruct Merge(params ScrStruct[] incoming)
    {
        // Base case - just copy the one incoming
        if (incoming.Length == 1)
        {
            return incoming[0].Copy();
        }

        // Find keys present across all structs that are incoming
        HashSet<string> fields = new();
        bool isDeterministic = true;

        foreach (ScrStruct scrStruct in incoming)
        {
            fields.UnionWith(scrStruct.Fields.Keys);

            // We can only assert that our resulting struct is deterministic if ALL those it came from are too.
            isDeterministic &= scrStruct.IsDeterministic;
        }

        List<KeyValuePair<string, ScrData>> mergedFields = [];
        foreach (string field in fields)
        {
            ScrData[] values = incoming.Select(s => s.Get(field)).ToArray();
            ScrData mergedValue = ScrData.Merge(values);

            // Skip adding if we're not deterministic and the merged value is 'any', it not being present would mean the same thing.
            if (!isDeterministic && mergedValue.IsAny())
            {
                continue;
            }

            mergedFields.Add(new KeyValuePair<string, ScrData>(field, mergedValue));
        }

        return new ScrStruct(mergedFields, isDeterministic);
    }
}