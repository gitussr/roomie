using System.Security.Cryptography.X509Certificates;

namespace Roomie.Core.Identity;

/// <summary>How a device certificate names its device: <c>CN=Roomie Device &lt;device id&gt;</c>.</summary>
public static class DeviceCertificateNames
{
    public const string SubjectPrefix = "CN=Roomie Device ";

    public static string SubjectFor(Guid deviceId) => SubjectPrefix + deviceId.ToString("D");

    /// <summary>Reads the device ID from the certificate subject, or null if the subject isn't in Roomie's form.</summary>
    public static Guid? ReadDeviceId(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var subject = certificate.Subject;
        if (!subject.StartsWith(SubjectPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var text = subject[SubjectPrefix.Length..];
        return Guid.TryParseExact(text, "D", out var deviceId)
            && deviceId != Guid.Empty
            && string.Equals(text, deviceId.ToString("D"), StringComparison.Ordinal)
            ? deviceId
            : null;
    }
}
