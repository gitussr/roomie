using System.Diagnostics.CodeAnalysis;

namespace Roomie.Core.Configuration;

/// <summary>Rules for application version strings sent to peers, e.g. <c>1.0.0</c> or <c>0.3.0-beta+abc</c>.</summary>
public static class AppVersionFormat
{
    public const int MaxLength = 32;

    public static bool IsValid([NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+');
}
