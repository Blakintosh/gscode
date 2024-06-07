using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Sense;

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
internal class ScrStruct
{
    public ScrData Parent { get; }
    public int SplitCount { get; set; } = 0;
    public bool CopyOnWrite => SplitCount > 0;

    public ScrStruct(ScrData parent)
    {
        Parent = parent;
    }

    protected Dictionary<string, ScrProperty> Members { get; } = new();

    public void AddMember(string name, ScrData data)
    {
        Members.Add(name, new ScrProperty(name, Parent, data));
    }

    public void MarkSplit()
    {
        SplitCount++;

        foreach(KeyValuePair<string, ScrProperty> member in Members)
        {
            member.Value.MarkSplit();
        }
    }

    public void UnmarkSplit()
    {
        SplitCount = Math.Max(0, SplitCount - 1);

        foreach (KeyValuePair<string, ScrProperty> member in Members)
        {
            member.Value.UnmarkSplit();
        }
    }

    public ScrStruct DeepCopy(ScrData parent)
    {
        ScrStruct scrStruct = new(parent);
        foreach(KeyValuePair<string, ScrProperty> member in Members)
        {
            scrStruct.Members.Add(member.Key, member.Value.DeepCopy(parent));
        }
        return scrStruct;
    }

    /// <summary>
    /// Gets the member of the structure, or undefined if it doesn't exist
    /// </summary>
    /// <param name="name">The name of the member</param>
    /// <returns></returns>
    public virtual ScrProperty GetMember(string name)
    {
        if(!Members.ContainsKey(name))
        {
            return ScrProperty.Undefined(name, Parent);
        }
        return Members[name];
    }

    public static ScrStruct Merge(ScrData parent, ScrStruct first, ScrStruct second)
    {
        ScrStruct merged = new(parent);

        foreach(KeyValuePair<string, ScrProperty> member in first.Members)
        {
            if (second.Members.ContainsKey(member.Key))
            {
                // Merge the values
                ScrProperty mergedProperty = ScrProperty.Merge(parent, member.Value, second.Members[member.Key]);
                merged.Members.Add(member.Key, mergedProperty);
                continue;
            }
            merged.Members.Add(member.Key, new ScrProperty(member.Key, parent, member.Value));
        }

        foreach (KeyValuePair<string, ScrProperty> member in second.Members)
        {
            if (merged.Members.ContainsKey(member.Key))
            {
                // We've already merged this member
                continue;
            }
            merged.Members.Add(member.Key, new ScrProperty(member.Key, parent, member.Value));
        }

        return merged;
    }
}

/// <summary>
/// Reserved script structure (level, self, etc.) - no assumptions can be made about members
/// </summary>
internal class ScrReservedStruct : ScrStruct
{
    public ScrReservedStruct(ScrData parent, IEnumerable<KeyValuePair<string, ScrData>> members) : base(parent)
    {
        foreach (KeyValuePair<string, ScrData> member in members)
        {
            Members.Add(member.Key, new ScrProperty(member.Key, parent, member.Value));
        }
    }

    public ScrReservedStruct(ScrData parent) : base(parent) {}

    public override ScrProperty GetMember(string name)
    {
        if (!Members.ContainsKey(name))
        {
            return ScrProperty.Default(name, Parent);
        }
        return Members[name];
    }

    public static ScrReservedStruct Merge(ScrData parent, ScrReservedStruct first, ScrReservedStruct second)
    {
        ScrReservedStruct merged = new(parent);

        foreach (KeyValuePair<string, ScrProperty> member in first.Members)
        {
            if (second.Members.ContainsKey(member.Key))
            {
                // Merge the values
                ScrProperty mergedProperty = ScrProperty.Merge(parent, member.Value, second.Members[member.Key]);
                merged.Members.Add(member.Key, mergedProperty);
                continue;
            }
            merged.Members.Add(member.Key, new ScrProperty(member.Key, parent, member.Value));
        }

        foreach (KeyValuePair<string, ScrProperty> member in second.Members)
        {
            if (merged.Members.ContainsKey(member.Key))
            {
                // We've already merged this member
                continue;
            }
            merged.Members.Add(member.Key, new ScrProperty(member.Key, parent, member.Value));
        }

        return merged;
    }
}