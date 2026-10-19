using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// A JSON string's value even when it is not valid UTF-16. A tool output cut off in the middle
/// of an emoji leaves a lone surrogate escape (<c>\ud83d</c>) — Node's JSON.stringify writes
/// exactly that — and <see cref="JsonElement.GetString"/> refuses the whole value. Here the
/// broken half becomes U+FFFD and the rest of the message survives.
/// </summary>
internal static class JsonText
{
    public static string? Of(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return null;
        try { return element.GetString(); }
        catch (InvalidOperationException) { return Lenient(element.GetRawText()); }
    }

    /// <summary>Unescapes a raw JSON string token; surrogates left without a partner become U+FFFD.</summary>
    private static string Lenient(string raw)
    {
        var s = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }
            var escaped = s[++i];
            switch (escaped)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < s.Length
                              && ushort.TryParse(s.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier,
                                  CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(escaped); break;     // \" \\ \/ and anything unexpected
            }
        }

        for (var i = 0; i < sb.Length; i++)
        {
            if (char.IsHighSurrogate(sb[i]) && i + 1 < sb.Length && char.IsLowSurrogate(sb[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(sb[i])) sb[i] = '�';
        }
        return sb.ToString();
    }
}
