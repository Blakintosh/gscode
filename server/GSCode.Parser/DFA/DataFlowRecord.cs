using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.DFA;

#if PREVIEW
internal enum ScrDataRecordKind
{
    LocalVariable,
    GlobalVariable,
}

internal record class ScrDataRecord(int Scope, ScrDataRecordKind Kind, ScrData Data);

internal class DataFlowRecord
{
    public Dictionary<string, ScrDataRecord> Members { get; } = new();

    public bool Equals(DataFlowRecord other)
    {
        if (Members.Count != other.Members.Count)
        {
            return false;
        }

        HashSet<string> allMembers = [.. Members.Keys];
        allMembers.UnionWith(other.Members.Keys);

        foreach (string member in allMembers)
        {
            ScrDataRecord? thisMember = Members[member];
            ScrDataRecord? otherMember = other.Members[member];

            // One is not in the other
            if(thisMember is null || otherMember is null)
            {
                return false;
            }

            // Records are not equal
            if(thisMember != otherMember)
            {
                return false;
            }
        }

        return true;
    }
}

#endif