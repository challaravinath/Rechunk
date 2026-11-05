namespace Rechunk;

/// <summary>
/// Tells which language a chunk is written in, for callers that do not tag their chunks. Plug in your own (a real
/// detection library, or a lookup from your document metadata) through LanguageOptions.Detector.
/// Return null when you can not tell. The result is normalized, so "en-US" and "en" mean the same.
/// A detector should be fast and have no side effects, it runs once for every untagged chunk.
/// </summary>
public interface ILanguageDetector
{
    string? Detect(ReadOnlySpan<char> text);
}
