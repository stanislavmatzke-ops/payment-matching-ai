namespace PaymentMatching.Rules;

/// <summary>
/// String helpers with the semantics of the legacy Pascal runtime. Text is handled as Latin-1
/// (one char = one byte), so lengths and offsets match the byte-based legacy code.
/// </summary>
internal static class PascalText
{
    /// <summary>Pascal <c>Trim</c>: removes characters &lt;= ' ' (spaces and control chars) at both ends.</summary>
    public static string Trim(string s)
    {
        var start = 0;
        var end = s.Length - 1;
        while (start <= end && s[start] <= ' ')
        {
            start++;
        }

        while (end >= start && s[end] <= ' ')
        {
            end--;
        }

        return s[start..(end + 1)];
    }

    /// <summary>Pascal <c>UpperCase</c>: converts ASCII letters only.</summary>
    public static string UpperAscii(string s) =>
        string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            }
        });
}
