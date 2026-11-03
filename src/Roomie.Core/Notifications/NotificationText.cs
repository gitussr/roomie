using System.Globalization;
using System.Text;

namespace Roomie.Core.Notifications;

/// <summary>
/// Makes text from other devices safe to show in a Windows notification, where it appears outside Roomie and
/// could otherwise pretend to be something else, for example by reversing a file's extension with a direction mark.
/// </summary>
public static class NotificationText
{
    /// <summary>Windows cuts a notification title at 63 characters.</summary>
    public const int MaxTitleLength = 63;

    /// <summary>Windows cuts notification text at 255 characters.</summary>
    public const int MaxBodyLength = 255;

    private const char Ellipsis = '…';

    /// <summary>Puts text on one line, without invisible or direction-changing characters, and within a length.</summary>
    public static string Clean(string? text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(Math.Min(text.Length, maxLength * 2));
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                pendingSpace = cleaned.Length > 0;
                continue;
            }

            // Format characters include the direction marks. Joiners go too: emoji still show, in parts at worst.
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (pendingSpace)
            {
                cleaned.Append(' ');
                pendingSpace = false;
            }

            cleaned.Append(rune.ToString());
            if (cleaned.Length > maxLength)
            {
                break;
            }
        }

        return Shorten(cleaned.ToString(), maxLength);
    }

    private static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // Cut between whole characters, so a surrogate pair or a combining mark is never split.
        var info = new StringInfo(text);
        var keep = Math.Min(info.LengthInTextElements, maxLength - 1);
        while (keep > 0)
        {
            var part = info.SubstringByTextElements(0, keep);
            if (part.Length < maxLength)
            {
                return part.TrimEnd() + Ellipsis;
            }

            keep--;
        }

        return Ellipsis.ToString();
    }
}
