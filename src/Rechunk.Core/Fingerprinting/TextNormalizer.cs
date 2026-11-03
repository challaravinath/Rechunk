using System.Text;

namespace Rechunk;

/// <summary>
/// Cleans text up before hashing so harmless differences do not matter.
/// Unicode NFC, any run of whitespace becomes one space, leading and trailing whitespace is dropped.
/// Case is kept on purpose. Whitespace includes non breaking spaces.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Bump this if the rules above ever change.</summary>
    public const string Version = "norm-v1";

    public static string Normalize(ReadOnlySpan<char> text)
    {
        // TODO: do this on spans and avoid the extra strings
        var composed = text.ToString().Normalize(NormalizationForm.FormC);

        var sb = new StringBuilder(composed.Length);
        var pendingSpace = false;

        foreach (var c in composed)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
