using System.Buffers.Binary;

namespace Roomie.Core.Sounds;

/// <summary>What a sound announces.</summary>
public enum SoundEffect
{
    Message,
    FileOffered,
    TransferFinished,
    TransferFailed,
    PairingRequest,
}

/// <summary>
/// Makes Roomie's sounds as WAV data. They're generated, so there are no audio files to ship or license. Each is a few
/// soft notes: rising for good news, falling for a failure, and a repeated pair for something waiting for an answer.
/// </summary>
public static class SoundBank
{
    /// <summary>The loudest any sample gets, as a share of full scale. These play in a quiet room, often unexpectedly.</summary>
    public const double PeakLevel = 0.3;

    private const int SampleRate = 44100;
    private const int HeaderLength = 44;
    private const double AttackMs = 6;

    private const double C4 = 261.63;
    private const double E4 = 329.63;
    private const double C5 = 523.25;
    private const double D5 = 587.33;
    private const double E5 = 659.25;
    private const double G5 = 783.99;
    private const double A5 = 880.00;
    private const double C6 = 1046.50;
    private const double E6 = 1318.51;

    private readonly record struct Note(double StartMs, double Frequency, double LengthMs);

    public static byte[] Render(SoundEffect effect)
    {
        Note[] notes = effect switch
        {
            SoundEffect.Message => [new(0, E5, 150), new(90, A5, 220)],
            SoundEffect.FileOffered => [new(0, C5, 150), new(100, E5, 150), new(200, G5, 260)],
            SoundEffect.TransferFinished => [new(0, G5, 150), new(110, C6, 150), new(220, E6, 340)],
            SoundEffect.TransferFailed => [new(0, E4, 240), new(190, C4, 400)],
            SoundEffect.PairingRequest => [new(0, D5, 130), new(120, A5, 170), new(340, D5, 130), new(460, A5, 240)],
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };

        var totalMs = notes.Max(n => n.StartMs + n.LengthMs);
        var mix = new double[(int)(totalMs * SampleRate / 1000)];
        foreach (var note in notes)
        {
            AddNote(mix, note);
        }

        // Notes overlap, so the level is set from the finished mix and every sound peaks the same.
        var loudest = mix.Max(Math.Abs);
        var bytes = new byte[HeaderLength + (mix.Length * sizeof(short))];
        WriteHeader(bytes, mix.Length * sizeof(short));
        for (var i = 0; i < mix.Length; i++)
        {
            var sample = (short)Math.Round(mix[i] / loudest * PeakLevel * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(HeaderLength + (i * sizeof(short))), sample);
        }

        return bytes;
    }

    private static void AddNote(double[] mix, Note note)
    {
        var start = (int)(note.StartMs * SampleRate / 1000);
        var length = (int)(note.LengthMs * SampleRate / 1000);
        for (var i = 0; i < length && start + i < mix.Length; i++)
        {
            var seconds = (double)i / SampleRate;
            var ms = seconds * 1000;

            // A quick rise and a fade that reaches exactly zero, because a sudden start or end is heard as a click.
            var left = 1 - ((double)i / length);
            var envelope = Math.Min(1, ms / AttackMs) * left * left;

            // A little of the octave above makes it less like a test tone.
            var phase = 2 * Math.PI * note.Frequency * seconds;
            mix[start + i] += envelope * (Math.Sin(phase) + (0.18 * Math.Sin(2 * phase)));
        }
    }

    private static void WriteHeader(byte[] bytes, int dataLength)
    {
        const short Channels = 1;
        const short BytesPerSample = sizeof(short);
        var span = bytes.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], HeaderLength - 8 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * Channels * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], Channels * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], BytesPerSample * 8);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
    }
}
