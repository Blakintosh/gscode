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
    public void OnlyWeaponFieldsAreMarkedReadOnly()
    {
        // Pinned because the flags have a single documented source and must not spread past it.
        // Weapon Fields.txt lists all 240 weapon fields under "These are read only fields
        // accessible on weapon ID values returned by GetWeapon()". No other curated file has any
        // such authority -- the 128 flags they used to carry were applied by hand during the
        // manual import and produced warnings on shipped, working stock code, so they were
        // removed. A regeneration that reintroduces guesses fails here.
        ObjectFields fields = ObjectFields.Load(ApiDirectory);

        ObjectField[] readOnly = [.. fields.FieldNames()
            .SelectMany(name => fields.FindField(name))
            .Where(static field => field.ReadOnly)];

        Assert.Equal(240, readOnly.Length);
        Assert.All(readOnly, static field => Assert.Equal("weapon", field.EntityKind));
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
