using System.Buffers.Binary;
using System.Text;
using Roomie.Core.Configuration;
using Roomie.Core.Sounds;

namespace Roomie.Tests.Sounds;

public sealed class SoundBankTests
{
    private const int HeaderLength = 44;
    private const int SampleRate = 44100;

    public static TheoryData<SoundEffect> Effects() => [.. Enum.GetValues<SoundEffect>()];

    private static short[] Samples(byte[] wav)
    {
        var samples = new short[(wav.Length - HeaderLength) / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(HeaderLength + (i * 2)));
        }

        return samples;
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void EachSound_IsAWavFileWindowsCanPlay(SoundEffect effect)
    {
        var wav = SoundBank.Render(effect);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(wav.Length - HeaderLength, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void EachSound_IsShortAndNotLoud(SoundEffect effect)
    {
        var samples = Samples(SoundBank.Render(effect));

        Assert.InRange(samples.Length / (double)SampleRate, 0.2, 1.0);
        var peak = samples.Max(s => Math.Abs((int)s)) / (double)short.MaxValue;
        Assert.InRange(peak, SoundBank.PeakLevel - 0.01, SoundBank.PeakLevel + 0.01);
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void EachSound_StartsAndEndsWithoutAClick(SoundEffect effect)
    {
        var samples = Samples(SoundBank.Render(effect));

        // No jump between neighbouring samples anywhere near the size of a click, and silence at both ends.
        const int Quiet = 300;
        Assert.InRange(Math.Abs((int)samples[0]), 0, Quiet);
        Assert.InRange(Math.Abs((int)samples[^1]), 0, Quiet);
        for (var i = 1; i < samples.Length; i++)
        {
            Assert.InRange(Math.Abs(samples[i] - samples[i - 1]), 0, short.MaxValue / 8);
        }
    }

    [Fact]
    public void TheSounds_AreAllDifferent()
    {
        var all = Enum.GetValues<SoundEffect>().Select(e => Convert.ToHexString(SoundBank.Render(e))).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Sounds_AreOnUnlessTurnedOff()
    {
        Assert.True(new AppSettings().Sounds);
    }
}
