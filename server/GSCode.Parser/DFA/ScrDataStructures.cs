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
    // TODO: need to ensure that Fields is deeply compared for value equivalence
    private Dictionary<string, ScrData> Fields { get; } = new();
    public bool Deterministic { get; } = false;

    public ScrStruct(IEnumerable<KeyValuePair<string, ScrData>> fields, bool deterministic = false)
    {
        Deterministic = deterministic;
        foreach (KeyValuePair<string, ScrData> field in fields)
        {
            Fields.Add(field.Key, field.Value);
        }
    }

    public ScrData Get(string fieldName)
    {
        if(!Fields.TryGetValue(fieldName, out ScrData value))
        {
            return Deterministic ? ScrData.Undefined(this) : ScrData.Default;
        }

        return value;
    }

    public void Set(string fieldName, ScrData value)
    {
        value.Owner = this;
        Fields[fieldName] = value;
    }

    /// <summary>
    /// Deep-copies this struct instance.
    /// </summary>
    /// <returns></returns>
    public ScrStruct Copy()
    {
        List<KeyValuePair<string, ScrData>> fields = [];
        ScrStruct newStruct = new(fields, Deterministic);

        foreach(KeyValuePair<string, ScrData> field in Fields)
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
        if(incoming.Length == 1)
        {
            return incoming[0].Copy();
        }

        // Find keys present across all structs that are incoming
        HashSet<string> fields = new();
        bool deterministic = true;

        foreach (ScrStruct scrStruct in incoming)
        {
            fields.UnionWith(scrStruct.Fields.Keys);

            // We can only assert that our resulting struct is deterministic if ALL those it came from are too.
            deterministic &= scrStruct.Deterministic;
        }

        List<KeyValuePair<string, ScrData>> mergedFields = [];
        foreach (string field in fields)
        {
            ScrData[] values = incoming.Select(s => s.Get(field)).ToArray();
            ScrData mergedValue = ScrData.Merge(values);

            // Skip adding if we're not deterministic and the merged value is 'any', it not being present would mean the same thing.
            if(!deterministic && mergedValue.IsAny())
            {
                continue;
            }

            mergedFields.Add(new KeyValuePair<string, ScrData>(field, mergedValue));
        }

        return new ScrStruct(mergedFields, deterministic);
    }
}