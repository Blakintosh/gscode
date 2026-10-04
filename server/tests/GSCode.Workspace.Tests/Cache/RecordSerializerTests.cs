using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using GSCode.Core.Diagnostics;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Cache;

/// <summary>
/// The binary record layout names every field by position, so a property added to any type a
/// record carries is silently dropped from the cache unless the serializer learns it too. These pin
/// both halves: what each type holds, and that all of it survives the trip.
/// </summary>
public class RecordSerializerTests
{
    /// <summary>
    /// Every settable property of every type in a record. A failure here means a type gained or
    /// lost one: add it to BOTH halves of RecordSerializer in the same position, bump
    /// CacheSchema.RecordFormatVersion, and then update this list.
    /// </summary>
    [Theory]
    [InlineData(typeof(ScriptRecord), "ClassesContentHashContextIdDeclaredNamespacesDependenciesDiagnosticsFieldBindingsFunctionsIsDirtyLanguageMacrosNamespacesPathPathCallTargetsReferencesRelativePath")]
    [InlineData(typeof(FunctionSymbol), "AssignmentsDocFullRangeHasVarargsIsAutoexecIsDevOnlyIsPrivateKeyNameNameNameRangeNamespaceOwnerClassKeyNameParametersSourceFile")]
    [InlineData(typeof(ClassSymbol), "ConstructorDestructorFullRangeHasConstructorHasDestructorKeyNameMembersMethodsNameNameRangeNamespaceParentKeyNameSourceFile")]
    [InlineData(typeof(ParameterSymbol), "ByRefDefaultValueTextName")]
    [InlineData(typeof(AssignmentSymbol), "IsLoopVariableKeyNameNameOwnerNameRange")]
    [InlineData(typeof(MemberSymbol), "KeyNameNameRange")]
    [InlineData(typeof(NamespaceSpan), "GovernedRangeKeyNameNameNameRange")]
    [InlineData(typeof(MacroRecord), "DocumentationIsFunctionLikeNameNameRangeParameters")]
    [InlineData(typeof(DependencyEdge), "IsInsertRangeRawPathResolvedPath")]
    [InlineData(typeof(PathCallReference), "NameRangePath")]
    [InlineData(typeof(FieldBinding), "FieldRangeTarget")]
    [InlineData(typeof(ReferenceEntry), "FromMacroKeyKindRange")]
    [InlineData(typeof(SymbolKey), "KindNameNamespaceOwnerClass")]
    [InlineData(typeof(Diagnostic), "CodeMessageRangeRelatedInformationSeverityTags")]
    [InlineData(typeof(DiagnosticRelation), "FilePathMessageRange")]
    [InlineData(typeof(ScriptDocComment), "ArgumentsCallOnExamplesModuleNameRawTextSpmpSummary")]
    [InlineData(typeof(ScriptDocArgument), "DescriptionNameOptional")]
    [InlineData(typeof(TextRange), "EndStart")]
    [InlineData(typeof(Position), "CharacterLine")]
    public void EveryPropertyOfARecordTypeIsKnownToTheSerializer(Type type, string expected)
    {
        IEnumerable<string> settable = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.SetMethod is not null)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, string.Concat(settable));
    }

    [Fact]
    public void AFullyPopulatedRecord_SurvivesTheRoundTripWhole()
    {
        ScriptRecord record = FullyPopulated();

        ScriptRecord? restored = RecordSerializer.Deserialize(RecordSerializer.Serialize(record));

        Assert.NotNull(restored);
        Assert.Equal(AsJson(record), AsJson(restored));
    }

    [Fact]
    public void AnEmptyDoc_ReadsBackAsTheSharedNoneInstance()
    {
        ScriptRecord record = FullyPopulated() with
        {
            Functions = [FullyPopulated().Functions[0] with { Doc = new ScriptDocComment() }],
        };

        ScriptRecord? restored = RecordSerializer.Deserialize(RecordSerializer.Serialize(record));

        Assert.NotNull(restored);
        Assert.Same(ScriptDocComment.None, restored.Functions[0].Doc);
    }

    [Fact]
    public void ABlobFromTheOldJsonLayout_IsUnreadableRatherThanMisread()
    {
        using MemoryStream output = new();
        using ( GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true) )
        {
            JsonSerializer.Serialize(gzip, FullyPopulated());
        }

        Assert.Null(RecordSerializer.Deserialize(output.ToArray()));
    }

    [Fact]
    public void ATruncatedOrCorruptedBlob_IsUnreadable()
    {
        byte[] blob = RecordSerializer.Serialize(FullyPopulated());

        for ( int length = 0; length < blob.Length; length += Math.Max(1, blob.Length / 50) )
        {
            Assert.Null(RecordSerializer.Deserialize(blob[..length]));
        }

        // Past the header, so the marker still matches and the payload is what is wrong.
        byte[] corrupted = (byte[])blob.Clone();
        for ( int index = 4; index < corrupted.Length; index++ )
        {
            corrupted[index] ^= 0x5A;
        }

        Assert.Null(RecordSerializer.Deserialize(corrupted));
    }

    private static string AsJson(ScriptRecord record)
    {
        return JsonSerializer.Serialize(record);
    }

    private static TextRange Range(int seed)
    {
        return TextRange.FromCoordinates(seed, seed + 1, seed + 2, seed + 300);
    }

    /// <summary>Every field set to something other than its default, so a dropped one cannot hide.</summary>
    private static ScriptRecord FullyPopulated()
    {
        ScriptDocComment doc = new()
        {
            RawText = "/@ raw @/",
            Name = "doc_name",
            Summary = "Summary with ünïcödé",
            Module = "module",
            CallOn = "player",
            Spmp = "both",
            Arguments = [new ScriptDocArgument("<arg>", "an argument", Optional: true)],
            Examples = ["foo( 1 );", "bar();"],
        };

        FunctionSymbol function = new()
        {
            Name = "DoThing",
            KeyName = "dothing",
            Namespace = "ns",
            OwnerClassKeyName = "cthing",
            IsPrivate = true,
            IsAutoexec = true,
            IsDevOnly = true,
            Parameters = [new ParameterSymbol("a", ByRef: true, "undefined"), new ParameterSymbol("b", ByRef: false, "")],
            HasVarargs = true,
            NameRange = Range(1),
            FullRange = Range(2),
            SourceFile = @"c:\ws\scripts\thing.gsc",
            Doc = doc,
            Assignments = [new AssignmentSymbol("self", "Field", "field", Range(3), IsLoopVariable: true)],
        };

        ClassSymbol symbol = new()
        {
            Name = "CThing",
            KeyName = "cthing",
            Namespace = "ns",
            ParentKeyName = "cbase",
            Members = [new MemberSymbol("Member", "member", Range(4))],
            Methods = [function],
            HasConstructor = true,
            HasDestructor = true,
            Constructor = function with { Name = "__constructor", KeyName = "__constructor" },
            Destructor = function with { Name = "__destructor", KeyName = "__destructor" },
            NameRange = Range(5),
            FullRange = Range(6),
            SourceFile = @"c:\ws\scripts\thing.gsc",
        };

        return new ScriptRecord
        {
            Path = @"c:\ws\scripts\thing.gsc",
            Language = ScriptLanguage.Csc,
            ContextId = "workspace:c:\\ws",
            RelativePath = @"scripts\thing.gsc",
            ContentHash = 0xF00DFACE12345678UL,
            Namespaces = [new NamespaceSpan("NS", "ns", Range(7), Range(8))],
            DeclaredNamespaces = ["ns", "other"],
            Functions = [function],
            Classes = [symbol],
            Macros = [new MacroRecord("MACRO", IsFunctionLike: true, ["x", "y"], Range(9), "doc")],
            Dependencies = [new DependencyEdge(@"scripts\a.gsh", @"c:\raw\scripts\a.gsh", IsInsert: true, Range(10))],
            PathCallTargets = [new PathCallReference(@"maps\mp\_utility", Range(11))],
            FieldBindings =
            [
                new FieldBinding(
                    new SymbolKey(null, "callback", SymbolKind.Field),
                    new SymbolKey("ns", "on_damage", SymbolKind.Function),
                    Range(15)),
                new FieldBinding(
                    new SymbolKey(null, "scene", SymbolKind.Field),
                    new SymbolKey(null, "cawarenessscene", SymbolKind.Class),
                    Range(16)),
            ],
            References =
            [
                new ReferenceEntry(new SymbolKey("ns", "dothing", SymbolKind.Function), Range(12), ReferenceKind.Call, FromMacro: true),
                new ReferenceEntry(new SymbolKey(null, "method", SymbolKind.Function, "cthing"), Range(13), ReferenceKind.MethodCall),
                new ReferenceEntry(new SymbolKey(null, "a string", SymbolKind.StringLiteral), Range(14), ReferenceKind.Literal),
            ],
            Diagnostics =
            [
                new Diagnostic(Range(15), DiagnosticSeverity.Warning, GscDiagnosticCode.UnusedLocal, "message")
                {
                    Tags = [DiagnosticTag.Unnecessary],
                    RelatedInformation = [new DiagnosticRelation(@"c:\ws\other.gsc", Range(16), "related")],
                },
            ],
            IsDirty = true,
        };
    }
}
