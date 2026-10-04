using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

public class ObjectFieldsTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    [Fact]
    public void Load_ALockedObjectFieldsFile_ReportsFailureRatherThanThrowing()
    {
        // Only JsonException was caught, and there was no onParseFailure hook at all — so a
        // locked file both crashed the caller AND, if it had not, would have failed silently with
        // no way to tell "the profile ships no data" apart from "this data exists but is broken".
        GameProfile bo3 = GameProfile.BlackOps3;
        string directory = Path.Combine(Path.GetTempPath(), $"gscode_object_fields_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, bo3.ObjectFieldsFileName!);
        File.WriteAllText(path, "{}");

        try
        {
            using FileStream exclusiveLock = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            List<(string Path, Exception Exception)> failures = [];
            ObjectFields fields = ObjectFields.Load(directory, bo3, (failedPath, exception) => failures.Add((failedPath, exception)));

            Assert.Empty(fields.FindField("origin"));
            Assert.Single(failures);
            Assert.Equal(path, failures[0].Path);
            Assert.IsType<IOException>(failures[0].Exception);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

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
