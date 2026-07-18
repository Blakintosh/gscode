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
    public void FindField_IsCaseInsensitive_AndCarriesTypeAndReadonly()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory);

        // weapon.aifusetime is an int and read-only in the curated data.
        ImmutableArray<ObjectField> upper = fields.FindField("AIFUSETIME");
        ObjectField weapon = Assert.Single(upper.Where(f => f.EntityKind == "weapon"));
        Assert.Equal("int", weapon.Type);
        Assert.True(weapon.ReadOnly);
    }

    [Fact]
    public void RadiantKeys_LoadWithTypesAndSides()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory);

        RadiantKey? origin = fields.FindRadiantKey("origin");
        Assert.NotNull(origin);
        Assert.Equal("vector", origin!.Type);

        // classname is a client-only key in keys.txt.
        RadiantKey? classname = fields.FindRadiantKey("classname");
        if ( classname is not null )
        {
            Assert.Equal("client", classname.Side);
        }
    }

    [Fact]
    public void UnknownField_ReturnsEmpty()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory);
        Assert.Empty(fields.FindField("definitely_not_a_field_xyz"));
    }
}
