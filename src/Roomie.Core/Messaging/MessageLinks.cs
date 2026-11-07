using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Roomie.Core.Messaging;

/// <summary>A piece of a message: plain text, or a web address that can be opened.</summary>
/// <param name="Text">The text exactly as it was written.</param>
/// <param name="Address">For a link, the address to open, written so it can't pretend to be another site. Otherwise null.</param>
public sealed record MessageSegment(string Text, string? Address)
{
    public bool IsLink => Address is not null;
}

/// <summary>
/// Finds web addresses in messages. Messages come from other computers, so only plain web pages are links:
/// never files, network shares or other programs, which an address like file:// or ms-settings: would open.
/// </summary>
public static partial class MessageLinks
{
    /// <summary>Longer addresses are left as text. Browsers and Windows don't reliably handle more.</summary>
    public const int MaxAddressLength = 2048;

    private const string TrailingPunctuation = ".,;:!?'\"";

    public static bool HasLink(string? text) => Split(text).Any(segment => segment.IsLink);

    /// <summary>Splits a message into text and links, in order. Joining every piece's text gives back the message.</summary>
    public static IReadOnlyList<MessageSegment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var segments = new List<MessageSegment>();
        var plainStart = 0;
        foreach (Match match in Candidate().Matches(text))
        {
            var written = Trim(match.Value);
            if (ToAddress(written) is not { } address)
            {
                continue;
            }

            if (match.Index > plainStart)
            {
                segments.Add(new MessageSegment(text[plainStart..match.Index], null));
            }

            segments.Add(new MessageSegment(written, address));
            plainStart = match.Index + written.Length;
        }

        if (plainStart < text.Length)
        {
            segments.Add(new MessageSegment(text[plainStart..], null));
        }

        return segments;
    }

    /// <summary>
    /// The address to give the browser, or null if this isn't a web page that's safe to open. Checked again just
    /// before opening, so nothing reaches Windows that didn't come through here.
    /// </summary>
    public static string? ToAddress(string? written)
    {
        if (string.IsNullOrEmpty(written) || written.Length > MaxAddressLength)
        {
            return null;
        }

        var full = written.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + written : written;
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || string.IsNullOrEmpty(uri.Host)
            || uri.IsUnc
            // https://yourbank.com@other.site goes to other.site. A message has no reason to carry a user name.
            || uri.UserInfo.Length > 0)
        {
            return null;
        }

        // A name in another alphabet can look exactly like a familiar one, so the browser gets, and the tooltip
        // shows, the plain form (xn--...) that can't be mistaken for it.
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        return $"{uri.Scheme}://{host}{port}{uri.PathAndQuery}{uri.Fragment}";
    }

    /// <summary>Leaves off what usually follows an address in a sentence rather than belonging to it.</summary>
    private static string Trim(string candidate)
    {
        // An invisible or direction-changing character ends the address, so it can't hide part of it.
        var end = 0;
        foreach (var rune in candidate.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse)
            {
                break;
            }

            end += rune.Utf16SequenceLength;
        }

        var text = candidate[..end];
        while (text.Length > 0)
        {
            var last = text[^1];
            if (TrailingPunctuation.Contains(last)
                || (last == ')' && Unbalanced(text, '(', ')'))
                || (last == ']' && Unbalanced(text, '[', ']'))
                || (last == '}' && Unbalanced(text, '{', '}')))
            {
                text = text[..^1];
            }
            else
            {
                break;
            }
        }

        return text;
    }

    /// <summary>True when the address closes more brackets than it opens, as in "(see https://example.com)".</summary>
    private static bool Unbalanced(string text, char open, char close) =>
        text.Count(c => c == close) > text.Count(c => c == open);

    // Starts at http://, https:// or www., not in the middle of a word or an e-mail address, and runs to a space.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@./:-])(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Candidate();
}
