namespace Rechunk;

/// <summary>The writing systems the tokenizer and the built-in detector tell apart.</summary>
internal enum ScriptClass
{
    Other = 0,
    Latin,
    Han,
    Hiragana,
    Katakana,
    Hangul,
    Thai,
    Lao,
    Khmer,
    Myanmar,
    Cyrillic,
    Greek,
    Hebrew,
    Arabic,
    Devanagari,
}

internal static class Scripts
{
    public static ScriptClass Of(int cp)
    {
        if (cp < 0x80)
        {
            return (cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z') ? ScriptClass.Latin : ScriptClass.Other;
        }

        return cp switch
        {
            >= 0x00C0 and <= 0x024F => cp is 0x00D7 or 0x00F7 ? ScriptClass.Other : ScriptClass.Latin,
            >= 0x1E00 and <= 0x1EFF => ScriptClass.Latin,
            >= 0x0370 and <= 0x03FF or >= 0x1F00 and <= 0x1FFF => ScriptClass.Greek,
            >= 0x0400 and <= 0x052F => ScriptClass.Cyrillic,
            >= 0x0590 and <= 0x05FF => ScriptClass.Hebrew,
            >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF => ScriptClass.Arabic,
            >= 0x0900 and <= 0x097F => ScriptClass.Devanagari,
            >= 0x0E00 and <= 0x0E7F => ScriptClass.Thai,
            >= 0x0E80 and <= 0x0EFF => ScriptClass.Lao,
            >= 0x1000 and <= 0x109F => ScriptClass.Myanmar,
            >= 0x1780 and <= 0x17FF => ScriptClass.Khmer,
            >= 0x3040 and <= 0x309F => ScriptClass.Hiragana,
            >= 0x30A0 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F => ScriptClass.Katakana,
            >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF => ScriptClass.Hangul,
            >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
                or >= 0x20000 and <= 0x2FA1F or >= 0x3005 and <= 0x3007 => ScriptClass.Han,
            _ => ScriptClass.Other,
        };
    }

    /// <summary>True for scripts that do not put spaces between words, so a run of them has to be cut into characters.</summary>
    public static bool IsUnspaced(ScriptClass script) =>
        script is ScriptClass.Han or ScriptClass.Hiragana or ScriptClass.Katakana
            or ScriptClass.Thai or ScriptClass.Lao or ScriptClass.Khmer or ScriptClass.Myanmar;

    /// <summary>Chinese and Japanese numerals. They are numbers for the false merge guard, like 30 is.</summary>
    public static bool IsHanNumeral(int cp) =>
        cp is '〇' or '零' or '一' or '二' or '三' or '四' or '五' or '六' or '七' or '八' or '九' or '十'
            or '百' or '千' or '万' or '萬' or '億' or '亿' or '兆' or '两' or '兩';

    /// <summary>A few Han characters that negate what follows. Used by the false merge guard.</summary>
    public static bool IsHanNegation(int cp) =>
        cp is '不' or '没' or '沒' or '无' or '無' or '非' or '未' or '勿' or '别' or '別';
}
