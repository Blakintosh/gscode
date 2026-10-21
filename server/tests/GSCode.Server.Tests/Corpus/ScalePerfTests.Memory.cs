using System.Collections.Concurrent;
using System.Reflection;
using GSCode.Workspace.Database;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Where a record's bytes go: every field of <see cref="ScriptRecord"/>, with the whole object graph
/// beneath it — arrays, objects, strings — counted once, at the first field that reaches it.
///
/// Retained memory is one number, and at 50,000 heavy BO3 files it is 12 GB. Knowing that half of it
/// is references (113 M entries at 56 bytes) came from a back-of-envelope product; this measures the
/// other half too, per field, so a change that shrinks one field says by how much and the next
/// largest is named. Sizes follow the 64-bit CLR layout — an object header and method-table pointer,
/// an array's length, references at 8 bytes, value types at their field sizes padded to their
/// largest field — which is close, not exact; the ranking is the point.
/// </summary>
public partial class ScalePerfTests
{
    /// <summary>Records sampled for the census, spread across the store.</summary>
    private const int CensusSample = 300;

    private static List<(string Field, double KilobytesPerRecord)> MeasureRecordCensus(ScriptDatabase database)
    {
        List<ScriptRecord> records = [.. database.Gsc.AllRecords];
        List<ScriptRecord> sample = [];
        for ( int index = 0; index < CensusSample && records.Count > 0; index++ )
        {
            sample.Add(records[(int)((long)index * records.Count / Math.Min(CensusSample, records.Count))]);
        }

        RecordCensus census = new();
        foreach ( ScriptRecord record in sample )
        {
            census.Visit(record);
        }

        List<(string Field, double KilobytesPerRecord)> rows = [];
        foreach ( KeyValuePair<string, long> field in census.BytesByField.OrderByDescending(pair => pair.Value) )
        {
            rows.Add((field.Key, field.Value / 1024.0 / Math.Max(1, sample.Count)));
        }

        return rows;
    }

    private sealed class RecordCensus
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> s_fields = new();
        private static readonly ConcurrentDictionary<Type, int> s_valueSizes = new();

        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public Dictionary<string, long> BytesByField { get; } = new(StringComparer.Ordinal);

        public void Visit(ScriptRecord record)
        {
            _seen.Add(record);
            Add("(the record itself)", ObjectBytes(typeof(ScriptRecord)));
            foreach ( FieldInfo field in FieldsOf(typeof(ScriptRecord)) )
            {
                Walk(field.GetValue(record), CleanName(field.Name));
            }
        }

        private void Add(string field, long bytes)
        {
            BytesByField[field] = BytesByField.GetValueOrDefault(field) + bytes;
        }

        private void Walk(object? value, string field)
        {
            if ( value is null )
            {
                return;
            }

            Type type = value.GetType();
            if ( type.IsPrimitive || type.IsEnum || value is Type || value is Delegate || value is Pointer )
            {
                return;
            }

            // A value type held inline: its bytes belong to whatever holds it, but anything it
            // REFERENCES is reached from here.
            if ( type.IsValueType )
            {
                foreach ( FieldInfo inner in FieldsOf(type) )
                {
                    Walk(inner.GetValue(value), field);
                }

                return;
            }

            if ( !_seen.Add(value) )
            {
                return;
            }

            if ( value is string text )
            {
                Add(field, (22 + 2L * text.Length + 7) & ~7L);
                return;
            }

            if ( value is Array array )
            {
                Type element = type.GetElementType()!;
                int elementBytes = element.IsValueType ? ValueBytes(element) : 8;
                Add(field, (24 + (long)elementBytes * array.Length + 7) & ~7L);
                if ( !element.IsPrimitive && !element.IsEnum )
                {
                    foreach ( object? item in array )
                    {
                        Walk(item, field);
                    }
                }

                return;
            }

            Add(field, ObjectBytes(type));

            // A function is most of what a record declares, so its own fields are split out:
            // "Functions" alone would say nothing about which part of a function to shrink.
            bool split = type == typeof(GSCode.Core.Symbols.FunctionSymbol) && !field.Contains('.');
            foreach ( FieldInfo inner in FieldsOf(type) )
            {
                Walk(inner.GetValue(value), split ? field + "." + CleanName(inner.Name) : field);
            }
        }

        private static long ObjectBytes(Type type)
        {
            long fields = 0;
            foreach ( FieldInfo field in FieldsOf(type) )
            {
                fields += field.FieldType.IsValueType ? ValueBytes(field.FieldType) : 8;
            }

            return Math.Max(24, (16 + fields + 7) & ~7L);
        }

        private static int ValueBytes(Type type)
        {
            return s_valueSizes.GetOrAdd(type, static candidate =>
            {
                if ( candidate.IsEnum )
                {
                    return ValueBytes(Enum.GetUnderlyingType(candidate));
                }

                if ( candidate.IsPrimitive )
                {
                    return System.Runtime.InteropServices.Marshal.SizeOf(candidate == typeof(char) ? typeof(short) : candidate == typeof(bool) ? typeof(byte) : candidate);
                }

                int total = 0;
                int largest = 1;
                foreach ( FieldInfo field in FieldsOf(candidate) )
                {
                    int size = field.FieldType.IsValueType ? ValueBytes(field.FieldType) : 8;
                    int alignment = Math.Min(8, size);
                    total = (total + alignment - 1) / alignment * alignment + size;
                    largest = Math.Max(largest, alignment);
                }

                return Math.Max(1, (total + largest - 1) / largest * largest);
            });
        }

        private static string CleanName(string fieldName)
        {
            if ( fieldName.StartsWith('<') )
            {
                int close = fieldName.IndexOf('>');
                if ( close > 1 )
                {
                    return fieldName[1..close];
                }
            }

            return fieldName;
        }

        private static FieldInfo[] FieldsOf(Type type)
        {
            return s_fields.GetOrAdd(type, static candidate =>
            {
                List<FieldInfo> fields = [];
                for ( Type? current = candidate; current is not null && current != typeof(object); current = current.BaseType )
                {
                    fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                }

                return fields.ToArray();
            });
        }
    }
}
