namespace Roomie.Core.Identity;

public static class DeviceIdFormat
{
    /// <summary>
    /// Formats the first 48 bits of the device ID as <c>RM-XXXX-XXXX-XXXX</c> for display.
    /// Display only: it is not unique enough to identify a device, and is never a trust anchor.
    /// </summary>
    public static string ToDisplayId(Guid deviceId)
    {
        var hex = deviceId.ToString("N").ToUpperInvariant();
        return $"RM-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }
}
