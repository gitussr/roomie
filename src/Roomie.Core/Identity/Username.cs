using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Roomie.Core.Identity;

/// <summary>
/// Username rules shared by the local editor and, later, by validation of names received from peers.
/// </summary>
public static class Username
{
    public const int MaxLength = 32;
    public const string Fallback = "User";

    /// <summary>
    /// Trims the name and rejects empty names, names over <see cref="MaxLength"/> characters, and
    /// control or format characters (format characters include bidi overrides, which can disguise
    /// one name as another).
    /// </summary>
    public static bool TryNormalize(string? candidate, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var trimmed = candidate?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            error = "Username cannot be empty.";
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"Username cannot be longer than {MaxLength} characters.";
            return false;
        }

        foreach (var ch in trimmed)
        {
            var category = char.GetUnicodeCategory(ch);
            if (char.IsControl(ch) || category == UnicodeCategory.Format)
            {
                error = "Username contains characters that are not allowed.";
                return false;
            }
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    /// <summary>
    /// Turns an arbitrary string (such as the Windows account name) into a valid username, falling back
    /// to <see cref="Fallback"/> when nothing usable remains.
    /// </summary>
    public static string Sanitize(string? candidate)
    {
        var kept = new string((candidate ?? string.Empty)
            .Where(ch => !char.IsControl(ch) && char.GetUnicodeCategory(ch) != UnicodeCategory.Format)
            .ToArray()).Trim();

        if (kept.Length > MaxLength)
        {
            // Don't cut a surrogate pair in half.
            var length = char.IsHighSurrogate(kept[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            kept = kept[..length].TrimEnd();
        }

        return kept.Length == 0 ? Fallback : kept;
    }
}
