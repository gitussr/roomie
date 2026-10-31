namespace Roomie.Core.Identity;

/// <summary>Signs with this device's private key without exposing it.</summary>
public interface IDeviceSigner
{
    /// <summary>ECDSA P-256 with SHA-256, as a 64-byte IEEE P1363 signature (r ‖ s).</summary>
    byte[] SignData(ReadOnlySpan<byte> data);
}
