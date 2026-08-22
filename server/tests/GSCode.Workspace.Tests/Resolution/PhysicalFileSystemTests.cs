using System.Text;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// The read path decodes bytes itself rather than pulling the file through a StreamReader, so the
/// question these ask is the only one that matters: does it return what File.ReadAllText returns?
/// The framework is the oracle, on real files, including the encodings a decompiler is unlikely to
/// emit but a user's editor might have saved.
/// </summary>
public class PhysicalFileSystemTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gscode-read").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("utf8-bom", true)]
    [InlineData("utf8-plain", false)]
    public void Utf8_MatchesTheFramework(string name, bool byteOrderMark)
    {
        string content = "main()\n{\n    print( \"café — 🙂\" );\n}\n";
        string path = Path.Combine(_directory, name + ".gsc");
        File.WriteAllText(path, content, new UTF8Encoding(byteOrderMark));

        Assert.Equal(File.ReadAllText(path), new PhysicalFileSystem().ReadAllText(path));
        Assert.Equal(content, new PhysicalFileSystem().ReadAllText(path));
    }

    [Fact]
    public void MarkedEncodings_MatchTheFramework()
    {
        string content = "level.x = 1;\r\nlevel.y = \"é\";\r\n";

        Encoding[] encodings =
        [
            Encoding.Unicode,
            Encoding.BigEndianUnicode,
            Encoding.UTF32,
            new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        ];

        for ( int index = 0; index < encodings.Length; index++ )
        {
            string path = Path.Combine(_directory, "marked-" + index + ".gsc");
            File.WriteAllText(path, content, encodings[index]);

            Assert.Equal(File.ReadAllText(path), new PhysicalFileSystem().ReadAllText(path));
        }
    }

    /// <summary>
    /// A byte that is not valid UTF-8 must be REPLACED rather than thrown on, which is what the
    /// framework does and what keeps one odd file in a raw tree from failing its own index entry.
    /// </summary>
    [Fact]
    public void InvalidBytes_AreReplacedRatherThanThrown()
    {
        string path = Path.Combine(_directory, "invalid.gsc");
        File.WriteAllBytes(path, [(byte)'a', 0xFF, 0xFE, (byte)'b']);

        Assert.Equal(File.ReadAllText(path), new PhysicalFileSystem().ReadAllText(path));
    }

    [Fact]
    public void AnEmptyFile_ReadsAsEmpty()
    {
        string path = Path.Combine(_directory, "empty.gsc");
        File.WriteAllBytes(path, []);

        Assert.Equal("", new PhysicalFileSystem().ReadAllText(path));
    }

    [Fact]
    public void AMissingFile_ThrowsTheSameWay()
    {
        string path = Path.Combine(_directory, "absent.gsc");

        Assert.Throws<FileNotFoundException>(() => new PhysicalFileSystem().ReadAllText(path));
    }
}
