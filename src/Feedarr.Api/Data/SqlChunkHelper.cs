namespace Feedarr.Api.Data;

/// <summary>
/// Helper pour fragmenter les listes utilisées dans les clauses SQL "IN @ids".
/// SQLite lève une erreur si le nombre de paramètres dépasse ~999.
/// Limite recommandée : 500 items par chunk.
/// </summary>
public static class SqlChunkHelper
{
    /// <summary>
    /// Divise <paramref name="source"/> en tranches de taille <paramref name="chunkSize"/>.
    /// </summary>
    public static IEnumerable<IReadOnlyList<T>> Chunk<T>(IEnumerable<T> source, int chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var list = source as IReadOnlyList<T> ?? source.ToList();
        for (var i = 0; i < list.Count; i += chunkSize)
        {
            var end = Math.Min(i + chunkSize, list.Count);
            yield return list.Skip(i).Take(end - i).ToList();
        }
    }
}
