using System.Collections.Immutable;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

public class ObjectFieldsTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    [Fact]
    public void Load_HasFieldsAcrossKinds()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory);

        // "origin" is declared on several entity kinds.
        ImmutableArray<ObjectField> origin = fields.FindField("origin");
        Assert.NotEmpty(origin);
    }

    [Fact]
    public void FindField_IsCaseInsensitive_AndCarriesType()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory);

        ImmutableArray<ObjectField> upper = fields.FindField("AIFUSETIME");
        ObjectField weapon = Assert.Single(upper.Where(f => f.EntityKind == "weapon"));
        Assert.Equal("int", weapon.Type);
    }

    [Fact]
    public void NoBundledFieldIsMarkedReadOnly()
    {
        // Deliberate, and pinned here so regenerating the artifact cannot quietly bring the flags
        // back. The 362 flags this data used to carry were applied by hand during the manual
        // import from ScriptObjectFields.xlsx, which has no read-only column — nothing sourced
        // them. They produced 87 warnings telling authors that shipped, working stock code was
        // wrong. The reading rule in ReadOnlyWriteLint is kept and still tested against synthetic
        // data, so flags that can be sourced need only be added back to the curated JSON.
        ObjectFields fields = ObjectFields.Load(ApiDirectory);

        Assert.DoesNotContain(
            fields.FieldNames().SelectMany(name => fields.FindField(name)),
            static field => field.ReadOnly);
    }

    [Fact]
    public void RadiantKeys_LoadWithTypesAndSides()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory);

        RadiantKey? origin = fields.FindRadiantKey("origin");
        Assert.NotNull(origin);
        Assert.Equal("vector", origin!.Type);

        // keys.txt marks classname client-only; the generator corrects that to "both".
        // See RadiantKeyVisibilityTests for the full side-filtering behaviour.
        RadiantKey? classname = fields.FindRadiantKey("classname");
        Assert.NotNull(classname);
        Assert.Equal("both", classname!.Side);
    }

    [Fact]
    public void UnknownField_ReturnsEmpty()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory);
        Assert.Empty(fields.FindField("definitely_not_a_field_xyz"));
    }
}
