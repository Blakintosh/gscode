using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using GSCode.Core.Diagnostics;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Cache;

/// <summary>
/// Serializes a ScriptRecord to a compact binary blob and back.
///
/// This was gzipped JSON until the scale sweep showed what it cost. Restoring a record — inflate,
/// then a JSON parse of every reference, range and diagnostic — measured 12.5 s of thread-time over
/// bo3's 1,085 files, against 7.9 s to lex, preprocess, parse, extract and commit the same files
/// from source. A warm start therefore could not beat a cold one on any machine with enough cores,
/// and at 50,000 files it stood at twice its budget. Gzip was never the cost (PERF.md measured the
/// uncompressed JSON as no faster); the text format was.
///
/// The layout is the record's fields in declaration order, with no field names:
/// <list type="bullet">
/// <item>integers as LEB128 varints, since nearly every one is a line, a column or an enum value;</item>
/// <item>strings through a per-blob table — written in full on first use and as an index after —
/// because one file repeats the same few hundred names, namespaces and paths across thousands of
/// references;</item>
/// <item>a leading format byte, so a blob from any other layout reads as unreadable rather than as a
/// wrong record.</item>
/// </list>
/// The whole body is then deflated. A new field must be added to BOTH <see cref="WriteRecord"/> and
/// <see cref="ReadRecord"/> in the same position, and <see cref="CacheSchema.RecordFormatVersion"/>
/// bumped; <c>RecordSerializerTests</c> fails when a record type gains a property this does not name.
/// </summary>
public static class RecordSerializer
{
    /// <summary>
    /// The first byte of every blob. Changes with the layout, independently of the database's own
    /// version gate, so a blob can never be read with the wrong layout even if the gate is missed.
    /// </summary>
    private const byte FormatMarker = 0xB6;

    /// <summary>
    /// One scratch writer per thread. Serializing runs on the indexing threads, a record's body can
    /// run to hundreds of kilobytes, and allocating that per file put it on the large-object heap —
    /// the fragmentation PERF.md spent a whole section removing.
    /// </summary>
    [ThreadStatic]
    private static RecordWriter? t_writer;

    public static byte[] Serialize(ScriptRecord record)
    {
        RecordWriter writer = t_writer ??= new RecordWriter();
        writer.Reset();
        WriteRecord(writer, record);

        using MemoryStream output = new();
        output.WriteByte(FormatMarker);

        Span<byte> length = stackalloc byte[5];
        int lengthBytes = RecordWriter.EncodeVarUInt(length, (uint)writer.Length);
        output.Write(length[..lengthBytes]);

        using ( DeflateStream deflate = new(output, CompressionLevel.Fastest, leaveOpen: true) )
        {
            deflate.Write(writer.Written);
        }

        return output.ToArray();
    }

    /// <summary>Decompresses and deserializes a record, or null when the blob is unreadable.</summary>
    public static ScriptRecord? Deserialize(byte[] blob)
    {
        if ( blob.Length < 2 || blob[0] != FormatMarker )
        {
            return null;
        }

        byte[]? body = null;
        try
        {
            RecordReader header = new(blob.AsSpan(1));
            int length = (int)header.ReadVarUInt();
            int payloadStart = 1 + header.Position;

            body = ArrayPool<byte>.Shared.Rent(length);
            using ( MemoryStream input = new(blob, payloadStart, blob.Length - payloadStart) )
            using ( DeflateStream deflate = new(input, CompressionMode.Decompress) )
            {
                deflate.ReadExactly(body, 0, length);
            }

            RecordReader reader = new(body.AsSpan(0, length));
            return ReadRecord(ref reader);
        }
        catch ( Exception exception ) when ( exception is InvalidDataException or EndOfStreamException
            or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException )
        {
            return null;
        }
        finally
        {
            if ( body is not null )
            {
                ArrayPool<byte>.Shared.Return(body);
            }
        }
    }

    // ---- ScriptRecord ------------------------------------------------------------------------

    private static void WriteRecord(RecordWriter writer, ScriptRecord record)
    {
        writer.WriteString(record.Path);
        writer.WriteVarUInt((uint)record.Language);
        writer.WriteString(record.ContextId);
        writer.WriteString(record.RelativePath);
        writer.WriteUInt64(record.ContentHash);

        writer.WriteVarUInt((uint)record.Namespaces.Length);
        foreach ( NamespaceSpan span in record.Namespaces )
        {
            writer.WriteString(span.Name);
            writer.WriteString(span.KeyName);
            writer.WriteRange(span.NameRange);
            writer.WriteRange(span.GovernedRange);
        }

        writer.WriteStrings(record.DeclaredNamespaces);

        writer.WriteVarUInt((uint)record.Functions.Length);
        foreach ( FunctionSymbol function in record.Functions )
        {
            WriteFunction(writer, function);
        }

        writer.WriteVarUInt((uint)record.Classes.Length);
        foreach ( ClassSymbol symbol in record.Classes )
        {
            WriteClass(writer, symbol);
        }

        writer.WriteVarUInt((uint)record.Macros.Length);
        foreach ( MacroRecord macro in record.Macros )
        {
            writer.WriteString(macro.Name);
            writer.WriteBool(macro.IsFunctionLike);
            writer.WriteStrings(macro.Parameters);
            writer.WriteRange(macro.NameRange);
            writer.WriteString(macro.Documentation);
        }

        writer.WriteVarUInt((uint)record.Dependencies.Length);
        foreach ( DependencyEdge edge in record.Dependencies )
        {
            writer.WriteString(edge.RawPath);
            writer.WriteString(edge.ResolvedPath);
            writer.WriteBool(edge.IsInsert);
            writer.WriteRange(edge.Range);
        }

        writer.WriteVarUInt((uint)record.PathCallTargets.Length);
        foreach ( PathCallReference target in record.PathCallTargets )
        {
            writer.WriteString(target.Path);
            writer.WriteRange(target.NameRange);
        }

        writer.WriteVarUInt((uint)record.References.Length);
        foreach ( ReferenceEntry entry in record.References )
        {
            writer.WriteString(entry.Key.Namespace);
            writer.WriteString(entry.Key.Name);
            writer.WriteVarUInt((uint)entry.Key.Kind);
            writer.WriteString(entry.Key.OwnerClass);
            writer.WriteRange(entry.Range);
            writer.WriteVarUInt((uint)entry.Kind);
            writer.WriteBool(entry.FromMacro);
        }

        writer.WriteVarUInt((uint)record.Diagnostics.Length);
        foreach ( Diagnostic diagnostic in record.Diagnostics )
        {
            WriteDiagnostic(writer, diagnostic);
        }

        writer.WriteBool(record.IsDirty);
    }

    private static ScriptRecord ReadRecord(ref RecordReader reader)
    {
        string path = reader.ReadRequiredString();
        ScriptLanguage language = (ScriptLanguage)reader.ReadVarUInt();
        string contextId = reader.ReadRequiredString();
        string relativePath = reader.ReadRequiredString();
        ulong contentHash = reader.ReadUInt64();

        int namespaceCount = reader.ReadCount();
        ImmutableArray<NamespaceSpan>.Builder namespaces = ImmutableArray.CreateBuilder<NamespaceSpan>(namespaceCount);
        for ( int index = 0; index < namespaceCount; index++ )
        {
            string name = reader.ReadRequiredString();
            string keyName = reader.ReadRequiredString();
            TextRange nameRange = reader.ReadRange();
            TextRange governedRange = reader.ReadRange();
            namespaces.Add(new NamespaceSpan(name, keyName, nameRange, governedRange));
        }

        ImmutableArray<string> declaredNamespaces = reader.ReadStrings();

        int functionCount = reader.ReadCount();
        ImmutableArray<FunctionSymbol>.Builder functions = ImmutableArray.CreateBuilder<FunctionSymbol>(functionCount);
        for ( int index = 0; index < functionCount; index++ )
        {
            functions.Add(ReadFunction(ref reader));
        }

        int classCount = reader.ReadCount();
        ImmutableArray<ClassSymbol>.Builder classes = ImmutableArray.CreateBuilder<ClassSymbol>(classCount);
        for ( int index = 0; index < classCount; index++ )
        {
            classes.Add(ReadClass(ref reader));
        }

        int macroCount = reader.ReadCount();
        ImmutableArray<MacroRecord>.Builder macros = ImmutableArray.CreateBuilder<MacroRecord>(macroCount);
        for ( int index = 0; index < macroCount; index++ )
        {
            string name = reader.ReadRequiredString();
            bool isFunctionLike = reader.ReadBool();
            ImmutableArray<string> parameters = reader.ReadStrings();
            TextRange nameRange = reader.ReadRange();
            string documentation = reader.ReadRequiredString();
            macros.Add(new MacroRecord(name, isFunctionLike, parameters, nameRange, documentation));
        }

        int dependencyCount = reader.ReadCount();
        ImmutableArray<DependencyEdge>.Builder dependencies = ImmutableArray.CreateBuilder<DependencyEdge>(dependencyCount);
        for ( int index = 0; index < dependencyCount; index++ )
        {
            string rawPath = reader.ReadRequiredString();
            string resolvedPath = reader.ReadRequiredString();
            bool isInsert = reader.ReadBool();
            TextRange range = reader.ReadRange();
            dependencies.Add(new DependencyEdge(rawPath, resolvedPath, isInsert, range));
        }

        int targetCount = reader.ReadCount();
        ImmutableArray<PathCallReference>.Builder targets = ImmutableArray.CreateBuilder<PathCallReference>(targetCount);
        for ( int index = 0; index < targetCount; index++ )
        {
            string targetPath = reader.ReadRequiredString();
            TextRange nameRange = reader.ReadRange();
            targets.Add(new PathCallReference(targetPath, nameRange));
        }

        int referenceCount = reader.ReadCount();
        ImmutableArray<ReferenceEntry>.Builder references = ImmutableArray.CreateBuilder<ReferenceEntry>(referenceCount);
        for ( int index = 0; index < referenceCount; index++ )
        {
            string? keyNamespace = reader.ReadString();
            string keyName = reader.ReadRequiredString();
            SymbolKind keyKind = (SymbolKind)reader.ReadVarUInt();
            string? ownerClass = reader.ReadString();
            TextRange range = reader.ReadRange();
            ReferenceKind kind = (ReferenceKind)reader.ReadVarUInt();
            bool fromMacro = reader.ReadBool();
            references.Add(new ReferenceEntry(new SymbolKey(keyNamespace, keyName, keyKind, ownerClass), range, kind, fromMacro));
        }

        int diagnosticCount = reader.ReadCount();
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(diagnosticCount);
        for ( int index = 0; index < diagnosticCount; index++ )
        {
            diagnostics.Add(ReadDiagnostic(ref reader));
        }

        bool isDirty = reader.ReadBool();

        return new ScriptRecord
        {
            Path = path,
            Language = language,
            ContextId = contextId,
            RelativePath = relativePath,
            ContentHash = contentHash,
            Namespaces = namespaces.MoveToImmutable(),
            DeclaredNamespaces = declaredNamespaces,
            Functions = functions.MoveToImmutable(),
            Classes = classes.MoveToImmutable(),
            Macros = macros.MoveToImmutable(),
            Dependencies = dependencies.MoveToImmutable(),
            PathCallTargets = targets.MoveToImmutable(),
            References = references.MoveToImmutable(),
            Diagnostics = diagnostics.MoveToImmutable(),
            IsDirty = isDirty,
        };
    }

    // ---- Functions and classes ---------------------------------------------------------------

    private static void WriteFunction(RecordWriter writer, FunctionSymbol function)
    {
        writer.WriteString(function.Name);
        writer.WriteString(function.KeyName);
        writer.WriteString(function.Namespace);
        writer.WriteString(function.OwnerClassKeyName);
        writer.WriteBool(function.IsPrivate);
        writer.WriteBool(function.IsAutoexec);
        writer.WriteBool(function.IsDevOnly);

        writer.WriteVarUInt((uint)function.Parameters.Length);
        foreach ( ParameterSymbol parameter in function.Parameters )
        {
            writer.WriteString(parameter.Name);
            writer.WriteBool(parameter.ByRef);
            writer.WriteString(parameter.DefaultValueText);
        }

        writer.WriteBool(function.HasVarargs);
        writer.WriteRange(function.NameRange);
        writer.WriteRange(function.FullRange);
        writer.WriteString(function.SourceFile);
        WriteDoc(writer, function.Doc);

        writer.WriteVarUInt((uint)function.Assignments.Length);
        foreach ( AssignmentSymbol assignment in function.Assignments )
        {
            writer.WriteString(assignment.OwnerName);
            writer.WriteString(assignment.Name);
            writer.WriteString(assignment.KeyName);
            writer.WriteRange(assignment.Range);
            writer.WriteBool(assignment.IsLoopVariable);
        }
    }

    private static FunctionSymbol ReadFunction(ref RecordReader reader)
    {
        string name = reader.ReadRequiredString();
        string keyName = reader.ReadRequiredString();
        string ns = reader.ReadRequiredString();
        string? ownerClassKeyName = reader.ReadString();
        bool isPrivate = reader.ReadBool();
        bool isAutoexec = reader.ReadBool();
        bool isDevOnly = reader.ReadBool();

        int parameterCount = reader.ReadCount();
        ImmutableArray<ParameterSymbol>.Builder parameters = ImmutableArray.CreateBuilder<ParameterSymbol>(parameterCount);
        for ( int index = 0; index < parameterCount; index++ )
        {
            string parameterName = reader.ReadRequiredString();
            bool byRef = reader.ReadBool();
            string defaultValueText = reader.ReadRequiredString();
            parameters.Add(new ParameterSymbol(parameterName, byRef, defaultValueText));
        }

        bool hasVarargs = reader.ReadBool();
        TextRange nameRange = reader.ReadRange();
        TextRange fullRange = reader.ReadRange();
        string sourceFile = reader.ReadRequiredString();
        ScriptDocComment doc = ReadDoc(ref reader);

        int assignmentCount = reader.ReadCount();
        ImmutableArray<AssignmentSymbol>.Builder assignments = ImmutableArray.CreateBuilder<AssignmentSymbol>(assignmentCount);
        for ( int index = 0; index < assignmentCount; index++ )
        {
            string ownerName = reader.ReadRequiredString();
            string assignmentName = reader.ReadRequiredString();
            string assignmentKeyName = reader.ReadRequiredString();
            TextRange range = reader.ReadRange();
            bool isLoopVariable = reader.ReadBool();
            assignments.Add(new AssignmentSymbol(ownerName, assignmentName, assignmentKeyName, range, isLoopVariable));
        }

        return new FunctionSymbol
        {
            Name = name,
            KeyName = keyName,
            Namespace = ns,
            OwnerClassKeyName = ownerClassKeyName,
            IsPrivate = isPrivate,
            IsAutoexec = isAutoexec,
            IsDevOnly = isDevOnly,
            Parameters = parameters.MoveToImmutable(),
            HasVarargs = hasVarargs,
            NameRange = nameRange,
            FullRange = fullRange,
            SourceFile = sourceFile,
            Doc = doc,
            Assignments = assignments.MoveToImmutable(),
        };
    }

    private static void WriteClass(RecordWriter writer, ClassSymbol symbol)
    {
        writer.WriteString(symbol.Name);
        writer.WriteString(symbol.KeyName);
        writer.WriteString(symbol.Namespace);
        writer.WriteString(symbol.ParentKeyName);

        writer.WriteVarUInt((uint)symbol.Members.Length);
        foreach ( MemberSymbol member in symbol.Members )
        {
            writer.WriteString(member.Name);
            writer.WriteString(member.KeyName);
            writer.WriteRange(member.Range);
        }

        writer.WriteVarUInt((uint)symbol.Methods.Length);
        foreach ( FunctionSymbol method in symbol.Methods )
        {
            WriteFunction(writer, method);
        }

        writer.WriteBool(symbol.HasConstructor);
        writer.WriteBool(symbol.HasDestructor);
        WriteOptionalFunction(writer, symbol.Constructor);
        WriteOptionalFunction(writer, symbol.Destructor);
        writer.WriteRange(symbol.NameRange);
        writer.WriteRange(symbol.FullRange);
        writer.WriteString(symbol.SourceFile);
    }

    private static ClassSymbol ReadClass(ref RecordReader reader)
    {
        string name = reader.ReadRequiredString();
        string keyName = reader.ReadRequiredString();
        string ns = reader.ReadRequiredString();
        string? parentKeyName = reader.ReadString();

        int memberCount = reader.ReadCount();
        ImmutableArray<MemberSymbol>.Builder members = ImmutableArray.CreateBuilder<MemberSymbol>(memberCount);
        for ( int index = 0; index < memberCount; index++ )
        {
            string memberName = reader.ReadRequiredString();
            string memberKeyName = reader.ReadRequiredString();
            TextRange range = reader.ReadRange();
            members.Add(new MemberSymbol(memberName, memberKeyName, range));
        }

        int methodCount = reader.ReadCount();
        ImmutableArray<FunctionSymbol>.Builder methods = ImmutableArray.CreateBuilder<FunctionSymbol>(methodCount);
        for ( int index = 0; index < methodCount; index++ )
        {
            methods.Add(ReadFunction(ref reader));
        }

        bool hasConstructor = reader.ReadBool();
        bool hasDestructor = reader.ReadBool();
        FunctionSymbol? constructor = ReadOptionalFunction(ref reader);
        FunctionSymbol? destructor = ReadOptionalFunction(ref reader);
        TextRange nameRange = reader.ReadRange();
        TextRange fullRange = reader.ReadRange();
        string sourceFile = reader.ReadRequiredString();

        return new ClassSymbol
        {
            Name = name,
            KeyName = keyName,
            Namespace = ns,
            ParentKeyName = parentKeyName,
            Members = members.MoveToImmutable(),
            Methods = methods.MoveToImmutable(),
            HasConstructor = hasConstructor,
            HasDestructor = hasDestructor,
            Constructor = constructor,
            Destructor = destructor,
            NameRange = nameRange,
            FullRange = fullRange,
            SourceFile = sourceFile,
        };
    }

    private static void WriteOptionalFunction(RecordWriter writer, FunctionSymbol? function)
    {
        writer.WriteBool(function is not null);
        if ( function is not null )
        {
            WriteFunction(writer, function);
        }
    }

    private static FunctionSymbol? ReadOptionalFunction(ref RecordReader reader)
    {
        if ( !reader.ReadBool() )
        {
            return null;
        }

        return ReadFunction(ref reader);
    }

    // ---- Docs and diagnostics ----------------------------------------------------------------

    /// <summary>
    /// A flag first: most functions carry no doc comment, and an empty one reads back as the shared
    /// <see cref="ScriptDocComment.None"/> instance rather than a fresh empty copy per function.
    /// </summary>
    private static void WriteDoc(RecordWriter writer, ScriptDocComment doc)
    {
        bool empty = doc.RawText.Length == 0 && doc.Name.Length == 0 && doc.Summary.Length == 0
            && doc.Module.Length == 0 && doc.CallOn.Length == 0 && doc.Spmp.Length == 0
            && doc.Arguments.IsDefaultOrEmpty && doc.Examples.IsDefaultOrEmpty;

        writer.WriteBool(!empty);
        if ( empty )
        {
            return;
        }

        writer.WriteString(doc.RawText);
        writer.WriteString(doc.Name);
        writer.WriteString(doc.Summary);
        writer.WriteString(doc.Module);
        writer.WriteString(doc.CallOn);
        writer.WriteString(doc.Spmp);

        writer.WriteVarUInt((uint)doc.Arguments.Length);
        foreach ( ScriptDocArgument argument in doc.Arguments )
        {
            writer.WriteString(argument.Name);
            writer.WriteString(argument.Description);
            writer.WriteBool(argument.Optional);
        }

        writer.WriteStrings(doc.Examples);
    }

    private static ScriptDocComment ReadDoc(ref RecordReader reader)
    {
        if ( !reader.ReadBool() )
        {
            return ScriptDocComment.None;
        }

        string rawText = reader.ReadRequiredString();
        string name = reader.ReadRequiredString();
        string summary = reader.ReadRequiredString();
        string module = reader.ReadRequiredString();
        string callOn = reader.ReadRequiredString();
        string spmp = reader.ReadRequiredString();

        int argumentCount = reader.ReadCount();
        ImmutableArray<ScriptDocArgument>.Builder arguments = ImmutableArray.CreateBuilder<ScriptDocArgument>(argumentCount);
        for ( int index = 0; index < argumentCount; index++ )
        {
            string argumentName = reader.ReadRequiredString();
            string description = reader.ReadRequiredString();
            bool optional = reader.ReadBool();
            arguments.Add(new ScriptDocArgument(argumentName, description, optional));
        }

        ImmutableArray<string> examples = reader.ReadStrings();

        return new ScriptDocComment
        {
            RawText = rawText,
            Name = name,
            Summary = summary,
            Module = module,
            CallOn = callOn,
            Spmp = spmp,
            Arguments = arguments.MoveToImmutable(),
            Examples = examples,
        };
    }

    private static void WriteDiagnostic(RecordWriter writer, Diagnostic diagnostic)
    {
        writer.WriteRange(diagnostic.Range);
        writer.WriteVarUInt((uint)diagnostic.Severity);
        writer.WriteVarUInt((uint)diagnostic.Code);
        writer.WriteString(diagnostic.Message);

        writer.WriteVarUInt((uint)diagnostic.Tags.Length);
        foreach ( DiagnosticTag tag in diagnostic.Tags )
        {
            writer.WriteVarUInt((uint)tag);
        }

        writer.WriteVarUInt((uint)diagnostic.RelatedInformation.Length);
        foreach ( DiagnosticRelation relation in diagnostic.RelatedInformation )
        {
            writer.WriteString(relation.FilePath);
            writer.WriteRange(relation.Range);
            writer.WriteString(relation.Message);
        }
    }

    private static Diagnostic ReadDiagnostic(ref RecordReader reader)
    {
        TextRange range = reader.ReadRange();
        DiagnosticSeverity severity = (DiagnosticSeverity)reader.ReadVarUInt();
        GscDiagnosticCode code = (GscDiagnosticCode)reader.ReadVarUInt();
        string message = reader.ReadRequiredString();

        int tagCount = reader.ReadCount();
        ImmutableArray<DiagnosticTag>.Builder tags = ImmutableArray.CreateBuilder<DiagnosticTag>(tagCount);
        for ( int index = 0; index < tagCount; index++ )
        {
            tags.Add((DiagnosticTag)reader.ReadVarUInt());
        }

        int relationCount = reader.ReadCount();
        ImmutableArray<DiagnosticRelation>.Builder relations = ImmutableArray.CreateBuilder<DiagnosticRelation>(relationCount);
        for ( int index = 0; index < relationCount; index++ )
        {
            string filePath = reader.ReadRequiredString();
            TextRange relationRange = reader.ReadRange();
            string relationMessage = reader.ReadRequiredString();
            relations.Add(new DiagnosticRelation(filePath, relationRange, relationMessage));
        }

        return new Diagnostic(range, severity, code, message)
        {
            Tags = tags.MoveToImmutable(),
            RelatedInformation = relations.MoveToImmutable(),
        };
    }

    // ---- Primitive encoding ------------------------------------------------------------------

    /// <summary>A growable byte buffer with the varint and string-table encodings, reused per thread.</summary>
    private sealed class RecordWriter
    {
        private byte[] _buffer = new byte[64 * 1024];
        private int _length;
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);

        public int Length
        {
            get { return _length; }
        }

        public ReadOnlySpan<byte> Written
        {
            get { return _buffer.AsSpan(0, _length); }
        }

        public void Reset()
        {
            _length = 0;
            _strings.Clear();
        }

        public static int EncodeVarUInt(Span<byte> destination, uint value)
        {
            int written = 0;
            while ( value >= 0x80 )
            {
                destination[written] = (byte)(value | 0x80);
                written++;
                value >>= 7;
            }

            destination[written] = (byte)value;
            return written + 1;
        }

        public void WriteVarUInt(uint value)
        {
            Ensure(5);
            _length += EncodeVarUInt(_buffer.AsSpan(_length), value);
        }

        public void WriteBool(bool value)
        {
            Ensure(1);
            _buffer[_length] = value ? (byte)1 : (byte)0;
            _length++;
        }

        public void WriteUInt64(ulong value)
        {
            Ensure(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
            _length += 8;
        }

        /// <summary>
        /// 0 is null; 1..n names a string already written, by its position in the table plus one;
        /// n + 1 introduces a new one, whose UTF-8 length and bytes follow.
        /// </summary>
        public void WriteString(string? value)
        {
            if ( value is null )
            {
                WriteVarUInt(0);
                return;
            }

            if ( _strings.TryGetValue(value, out int existing) )
            {
                WriteVarUInt((uint)existing + 1);
                return;
            }

            int index = _strings.Count;
            _strings[value] = index;
            WriteVarUInt((uint)index + 1);

            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteVarUInt((uint)byteCount);
            Ensure(byteCount);
            Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
            _length += byteCount;
        }

        public void WriteStrings(ImmutableArray<string> values)
        {
            if ( values.IsDefault )
            {
                WriteVarUInt(0);
                return;
            }

            WriteVarUInt((uint)values.Length);
            foreach ( string value in values )
            {
                WriteString(value);
            }
        }

        public void WriteRange(TextRange range)
        {
            WriteVarUInt((uint)range.Start.Line);
            WriteVarUInt((uint)range.Start.Character);
            WriteVarUInt((uint)range.End.Line);
            WriteVarUInt((uint)range.End.Character);
        }

        private void Ensure(int additional)
        {
            if ( _length + additional <= _buffer.Length )
            {
                return;
            }

            int size = _buffer.Length * 2;
            while ( size < _length + additional )
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, size);
        }
    }

    /// <summary>The reading half. Any malformed input surfaces as one of the exceptions <see cref="Deserialize"/> catches.</summary>
    private ref struct RecordReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;
        private List<string>? _strings;

        public RecordReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
            _strings = null;
        }

        public readonly int Position
        {
            get { return _position; }
        }

        public uint ReadVarUInt()
        {
            uint result = 0;
            int shift = 0;
            while ( true )
            {
                if ( shift > 28 )
                {
                    throw new InvalidDataException("Varint too long.");
                }

                byte next = _data[_position];
                _position++;
                result |= (uint)(next & 0x7F) << shift;
                if ( (next & 0x80) == 0 )
                {
                    return result;
                }

                shift += 7;
            }
        }

        /// <summary>A collection length, refused when it could not possibly fit in what is left.</summary>
        public int ReadCount()
        {
            uint count = ReadVarUInt();
            if ( count > (uint)(_data.Length - _position) )
            {
                throw new InvalidDataException("Count exceeds the remaining data.");
            }

            return (int)count;
        }

        public bool ReadBool()
        {
            byte value = _data[_position];
            _position++;
            return value != 0;
        }

        public ulong ReadUInt64()
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
            _position += 8;
            return value;
        }

        public string? ReadString()
        {
            _strings ??= new List<string>(256);

            uint code = ReadVarUInt();
            if ( code == 0 )
            {
                return null;
            }

            if ( code <= (uint)_strings.Count )
            {
                return _strings[(int)code - 1];
            }

            if ( code != (uint)_strings.Count + 1 )
            {
                throw new InvalidDataException("String table index out of order.");
            }

            int byteCount = ReadCount();
            string value = Encoding.UTF8.GetString(_data.Slice(_position, byteCount));
            _position += byteCount;
            _strings.Add(value);
            return value;
        }

        public string ReadRequiredString()
        {
            return ReadString() ?? throw new InvalidDataException("A required string was null.");
        }

        public ImmutableArray<string> ReadStrings()
        {
            int count = ReadCount();
            ImmutableArray<string>.Builder values = ImmutableArray.CreateBuilder<string>(count);
            for ( int index = 0; index < count; index++ )
            {
                values.Add(ReadRequiredString());
            }

            return values.MoveToImmutable();
        }

        public TextRange ReadRange()
        {
            int startLine = (int)ReadVarUInt();
            int startCharacter = (int)ReadVarUInt();
            int endLine = (int)ReadVarUInt();
            int endCharacter = (int)ReadVarUInt();
            return new TextRange(new Position(startLine, startCharacter), new Position(endLine, endCharacter));
        }
    }
}
