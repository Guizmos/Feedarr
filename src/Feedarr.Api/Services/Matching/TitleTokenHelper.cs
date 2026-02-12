using System.Collections.Generic;
using System.Linq;

namespace Feedarr.Api.Services.Matching;

public static class TitleTokenHelper
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "the", "and", "with", "from", "into", "over", "under", "without", "of", "to", "in", "on", "for", "by",
        "a", "an", "is", "it", "its", "at", "as", "be", "but", "or", "not", "this", "that", "was", "are",
        // French
        "le", "la", "les", "des", "du", "de", "au", "aux", "un", "une", "et", "ou", "en", "sur", "dans",
        "ce", "cette", "ces", "son", "sa", "ses", "mon", "ma", "mes", "ton", "ta", "tes", "leur", "leurs",
        "qui", "que", "quoi", "dont", "avec", "pour", "par", "sans", "sous", "vers", "chez", "entre",
        "comme", "mais", "donc", "car", "ni", "ne", "pas", "plus", "moins", "tres", "bien", "tout", "tous",
        // German
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "eines", "einem", "einen",
        "und", "oder", "aber", "doch", "wenn", "weil", "dass", "ob", "als", "wie", "wo", "was", "wer",
        "mit", "von", "zu", "bei", "nach", "vor", "aus", "um", "auf", "an", "im", "am",
        "ist", "sind", "war", "waren", "hat", "haben", "wird", "werden", "kann", "konnen",
        "nicht", "auch", "noch", "nur", "schon", "sehr", "mehr", "viel", "alle", "alles",
        // Spanish
        "el", "los", "lo", "las", "unos", "unas", "y", "o", "pero", "sino", "porque",
        "cual", "quien", "donde", "cuando", "como", "con", "sin", "para", "por", "sobre",
        "del", "al", "se", "su", "sus", "mi", "mis", "tu", "tus", "es", "son", "esta",
        "este", "esto", "eso", "ese", "no", "si", "muy", "mas", "menos", "todo", "todos", "nada",
        // Italian
        "il", "gli", "i", "uno", "ed", "ma", "che", "chi", "cui", "dove",
        "per", "tra", "fra", "di", "da", "della", "dei", "delle", "nel", "nella",
        "non", "molto", "poco", "tutto", "tutti", "questo", "quello",
        // Portuguese
        "os", "um", "uma", "uns", "umas", "ao", "aos", "do", "dos", "da", "das", "nas",
        "em", "sem", "ate", "desde", "contra",
        "eu", "ele", "ela", "nos", "vos", "eles", "elas", "meu", "minha", "seu", "sua",
        "nao", "sim", "muito", "pouco", "bem", "mal"
    };

    public static IReadOnlyList<string> GetTokens(string? value)
    {
        var normalized = TitleNormalizer.NormalizeTitle(value);
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    public static IReadOnlyList<string> GetSignificantTokens(string? value)
    {
        return GetTokens(value)
            .Where(token => token.Length > 2 && !StopWords.Contains(token))
            .ToList();
    }

    public static int CountSignificantTokens(string? value)
        => GetSignificantTokens(value).Count;

    public static int CountSignificantTokenOverlap(string query, string candidate, string? originalCandidate)
    {
        var queryTokens = new HashSet<string>(GetSignificantTokens(query), StringComparer.Ordinal);
        if (queryTokens.Count == 0) return 0;

        var candidateTokens = new HashSet<string>(GetSignificantTokens(candidate), StringComparer.Ordinal);
        var overlap = queryTokens.Count(t => candidateTokens.Contains(t));

        if (!string.IsNullOrWhiteSpace(originalCandidate))
        {
            var originalTokens = new HashSet<string>(GetSignificantTokens(originalCandidate), StringComparer.Ordinal);
            overlap = Math.Max(overlap, queryTokens.Count(t => originalTokens.Contains(t)));
        }

        return overlap;
    }

    public static bool IsStopWord(string token) => StopWords.Contains(token);
}
