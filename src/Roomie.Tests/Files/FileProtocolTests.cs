using Roomie.Core.Files;
using Roomie.Core.Protocol;

namespace Roomie.Tests.Files;

public sealed class FileProtocolTests
{
    private static ReadOnlyMemory<byte> PayloadOf(byte[] frame) => frame.AsMemory(5);

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(FileChunk.MaxDataLength)]
    public void Chunk_SurvivesEncoding(int length)
    {
        var id = Guid.NewGuid();
        var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        Assert.True(FileChunk.TryParse(PayloadOf(FileChunk.Encode(id, 7, data)), out var chunk));

        Assert.Equal(id, chunk.TransferId);
        Assert.Equal(7u, chunk.Sequence);
        Assert.Equal(data, chunk.Data.ToArray());
    }

    [Fact]
    public void Chunk_UsesTheWireFormatOfTheSpecification()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        var frame = FileChunk.Encode(id, 0x01020304, [0xAB]);

        Assert.Equal((byte)RmpFrameType.FileChunk, frame[4]);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF" + "01020304" + "00000001" + "AB"), frame[5..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(FileChunk.MaxDataLength + 1)]
    public void Chunk_OfAnImpossibleSize_CantBeEncoded(int length)
    {
        Assert.Throws<ArgumentException>(() => FileChunk.Encode(Guid.NewGuid(), 0, new byte[length]));
    }

    [Fact]
    public void Chunk_ThatIsShort_IsRefused()
    {
        Assert.False(FileChunk.TryParse(new byte[10], out _));
        Assert.False(FileChunk.TryParse(new byte[FileChunk.HeaderLength], out _));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(6u)]
    [InlineData(uint.MaxValue)]
    public void Chunk_WhoseLengthDisagreesWithTheFrame_IsRefused(uint claimed)
    {
        var payload = PayloadOf(FileChunk.Encode(Guid.NewGuid(), 0, new byte[5])).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20), claimed);

        Assert.False(FileChunk.TryParse(payload, out _));
    }

    [Fact]
    public void Chunk_WithAnEmptyTransferId_IsRefused()
    {
        Assert.False(FileChunk.TryParse(PayloadOf(FileChunk.Encode(Guid.Empty, 0, new byte[5])), out _));
    }

    [Theory]
    [InlineData("notes.pdf", "notes.pdf")]
    [InlineData("  notes.pdf  ", "notes.pdf")]
    [InlineData("नमस्ते 😀.txt", "नमस्ते 😀.txt")]
    [InlineData("..\\..\\Windows\\evil.exe", ".._.._Windows_evil.exe")]
    [InlineData("../../evil.exe", ".._.._evil.exe")]
    [InlineData("C:\\evil.exe", "C__evil.exe")]
    [InlineData("file.txt:hidden.exe", "file.txt_hidden.exe")]
    [InlineData("a<b>c\"d|e?f*g", "a_b_c_d_e_f_g")]
    [InlineData("evil.exe.", "evil.exe")]
    [InlineData("evil.exe. . .", "evil.exe")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("COM9.log", "_COM9.log")]
    [InlineData("CONFIG.txt", "CONFIG.txt")]
    [InlineData(".", "file")]
    [InlineData("..", "file")]
    [InlineData("...", "file")]
    [InlineData("   ", "file")]
    [InlineData("", "file")]
    [InlineData("a\u0000b\u0007c", "abc")]
    public void Filename_IsMadeSafe(string offered, string expected)
    {
        Assert.Equal(expected, SafeFilename.Create(offered));
    }

    [Fact]
    public void Filename_CantHideItsExtensionWithRightToLeftText()
    {
        var name = SafeFilename.Create("photo\u202Egpj.exe");

        Assert.Equal("photogpj.exe", name);
        Assert.True(SafeFilename.IsRunnable(name));
    }

    [Fact]
    public void Filename_IsLimitedInLength_AndKeepsItsExtension()
    {
        var name = SafeFilename.Create(new string('x', 400) + ".pdf");

        Assert.True(name.Length <= 200);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Filename_IsNeverAPath_WhateverItWasOffered()
    {
        foreach (var offered in new[] { "a/b", "a\\b", "..\\a", "/etc/passwd", "\\\\server\\share\\a", "C:a" })
        {
            var name = SafeFilename.Create(offered);
            Assert.Equal(name, Path.GetFileName(name));
            Assert.False(Path.IsPathRooted(name));
        }
    }

    [Theory]
    [InlineData("notes.pdf", 1, "notes (1).pdf")]
    [InlineData("notes", 2, "notes (2)")]
    [InlineData("a.tar.gz", 3, "a.tar (3).gz")]
    public void Filename_ForACollision_GetsACounter(string name, int counter, string expected)
    {
        Assert.Equal(expected, SafeFilename.WithCounter(name, counter));
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("SETUP.EXE", true)]
    [InlineData("run.bat", true)]
    [InlineData("script.ps1", true)]
    [InlineData("link.lnk", true)]
    [InlineData("macro.docm", true)]
    [InlineData("installer.msixbundle", true)]
    [InlineData("disk.iso", true)]
    [InlineData("settings.settingcontent-ms", true)]
    [InlineData("addin.xll", true)]
    [InlineData("script.py", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("notes.pdf", false)]
    [InlineData("noextension", false)]
    public void RunnableFiles_AreRecognized(string name, bool runnable)
    {
        Assert.Equal(runnable, SafeFilename.IsRunnable(name));
    }
}
