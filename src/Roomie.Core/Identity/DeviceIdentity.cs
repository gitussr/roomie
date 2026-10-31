namespace Roomie.Core.Identity;

/// <summary>
/// Public description of this computer's identity (architecture.md §1.2). The private key is
/// deliberately not part of this type and must never be serialized, logged or transmitted.
/// </summary>
/// <param name="DeviceId">Random, stable device identifier. A convenient handle, not proof of identity.</param>
/// <param name="Username">Editable display name. Never used for trust decisions.</param>
/// <param name="CertificateFingerprint">SPKI fingerprint (<c>SHA256:HEX</c>); the trust anchor peers pin.</param>
/// <param name="PublicKey">Base64 DER SubjectPublicKeyInfo of the device key.</param>
/// <param name="CreatedAt">When the identity was generated.</param>
/// <param name="IdentityVersion">Format version of the persisted identity.</param>
public sealed record DeviceIdentity(
    Guid DeviceId,
    string Username,
    string CertificateFingerprint,
    string PublicKey,
    DateTimeOffset CreatedAt,
    int IdentityVersion)
{
    public const int CurrentVersion = 1;

    /// <summary>Human-friendly form of <see cref="DeviceId"/>, e.g. <c>RM-7F92-A31C-8D20</c>.</summary>
    public string DisplayId => DeviceIdFormat.ToDisplayId(DeviceId);
}
