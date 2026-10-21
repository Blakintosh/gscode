using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>
    /// The heap through one phase, sampled every 250 ms on its own thread: working set, what the
    /// allocator has handed out, what the GC has committed, and — as of the last collection — each
    /// generation's size, the large-object heap included.
    ///
    /// At 10,000 heavy files the cold index peaked at 9.6 GB of working set against 2.1 GB retained,
    /// with no heap limit anywhere near. Whether that gap is gen0 budgets, promoted garbage or the
    /// large-object heap decides what could shrink it, and a single peak cannot say which.
    /// </summary>
    private sealed class HeapSampler : IDisposable
    {
        private readonly Thread _thread;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(double Seconds, string Line, long WorkingSet)> _samples = [];
        private volatile bool _stopped;

        public HeapSampler()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "scale heap sampler" };
            _thread.Start();
        }

        private void Run()
        {
            using Process self = Process.GetCurrentProcess();
            while ( !_stopped )
            {
                self.Refresh();
                GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
                ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
                string line = $"{_clock.Elapsed.TotalSeconds,6:F1} s  working set {self.WorkingSet64 / 1048576.0,7:F0} MB  "
                    + $"allocated {GC.GetTotalMemory(false) / 1048576.0,7:F0}  committed {info.TotalCommittedBytes / 1048576.0,7:F0}  "
                    + $"last GC #{info.Index} gen{info.Generation}: gen0 {Mb(generations, 0)} gen1 {Mb(generations, 1)} "
                    + $"gen2 {Mb(generations, 2)} loh {Mb(generations, 3)} poh {Mb(generations, 4)} MB, "
                    + $"promoted {info.PromotedBytes / 1048576.0:F0}";
                lock ( _samples )
                {
                    _samples.Add((_clock.Elapsed.TotalSeconds, line, self.WorkingSet64));
                }

                Thread.Sleep(250);
            }
        }

        private static string Mb(ReadOnlySpan<GCGenerationInfo> generations, int index)
        {
            return index < generations.Length ? $"{generations[index].SizeAfterBytes / 1048576.0:F0}" : "-";
        }

        /// <summary>About a dozen samples spread over the phase, and the one at the peak working set.</summary>
        public List<string> Stop()
        {
            _stopped = true;
            _thread.Join();

            List<string> lines = [];
            lock ( _samples )
            {
                if ( _samples.Count == 0 )
                {
                    return lines;
                }

                int step = Math.Max(1, _samples.Count / 12);
                for ( int index = 0; index < _samples.Count; index += step )
                {
                    lines.Add(_samples[index].Line);
                }

                (double Seconds, string Line, long WorkingSet) peak = _samples.MaxBy(sample => sample.WorkingSet);
                lines.Add("PEAK " + peak.Line);
            }

            return lines;
        }

        public void Dispose()
        {
            _stopped = true;
        }
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
            bool split = (type == typeof(GSCode.Core.Symbols.FunctionSymbol) || type == typeof(ReferenceTable)) && !field.Contains('.');
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
