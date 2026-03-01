using System.Globalization;
using System.Xml.Linq;

namespace Feedarr.Api.Services.Torznab;

public sealed class TorznabRssParser
{
    // Les 2 namespaces qu'on rencontre souvent
    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";
    private static readonly XNamespace NewznabNs = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    public List<TorznabItem> Parse(string xml)
    {
        var doc = XmlSecureParser.Parse(xml);
        var items = doc.Descendants("item");

        var result = new List<TorznabItem>();

        foreach (var it in items)
        {
            var title = (string?)it.Element("title") ?? "";
            var guid = (string?)it.Element("guid");
            var link = (string?)it.Element("link");

            // guid fallback (certains flux ont guid vide ou inutile)
            if (string.IsNullOrWhiteSpace(guid))
                guid = link ?? title;

            guid = guid?.Trim() ?? "";
            title = title.Trim();

            var pubTs = ParsePubDateToUnix(it);

            // enclosure url (souvent le vrai download url)
            var enclosureUrl = (string?)it.Element("enclosure")?.Attribute("url");

            // Parse attrs torznab/newznab
            var attrs = ParseAttrs(it);

            // category : parfois en <category>, parfois en attr "category" (souvent multiple)
            var categoryIds = ParseCategoryIds(it, attrs);
            var (stdCategoryId, specCategoryId) = ResolveStdSpec(categoryIds);
            int? categoryId = specCategoryId ?? stdCategoryId ?? (categoryIds.Count > 0 ? categoryIds[0] : null);

            // download url fallback: enclosure > link
            var downloadUrl = !string.IsNullOrWhiteSpace(enclosureUrl)
                ? enclosureUrl
                : link;

            var sizeBytes = TryLong(attrs, "size")
                ?? TryLongValue(GetElementValueByLocalName(it, "size"))
                ?? TryLongValue((string?)it.Element("enclosure")?.Attribute("length"));
            var grabs = TryInt(attrs, "grabs")
                ?? TryIntValue(GetElementValueByLocalName(it, "grabs"));

            var item = new TorznabItem
            {
                Guid = guid,
                Title = title,
                Link = link,
                DownloadUrl = downloadUrl,
                PublishedAtTs = pubTs,

                SizeBytes = sizeBytes,
                Seeders = TryInt(attrs, "seeders"),
                Leechers = TryInt(attrs, "leechers") ?? TryInt(attrs, "peers"),
                Grabs = grabs,
                InfoHash = TryString(attrs, "infohash") ?? TryString(attrs, "info_hash") ?? TryString(attrs, "hash"),

                CategoryId = categoryId,
                CategoryIds = categoryIds,
                StdCategoryId = stdCategoryId,
                SpecCategoryId = specCategoryId,
                Attrs = attrs
            };

            // Certains indexers mettent "seeders/leechers" en texte (description/content)
            // -> fallback léger si attrs vides
            if (item.Seeders is null || item.Leechers is null)
            {
                var desc = (string?)it.Element("description") ?? (string?)it.Element("summary");
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    // ⚠️ Fallback simple, tu peux durcir plus tard
                    // Ex: "Seeders: 123 Leechers: 45"
                    var (s, l) = ParseSeedLeechFromText(desc);
                    item.Seeders ??= s;
                    item.Leechers ??= l;
                }
            }

            result.Add(item);
        }

        return result;
    }

    private static List<int> ParseCategoryIds(XElement item, Dictionary<string, string> attrs)
    {
        var list = new List<int>();

        // torznab/newznab attr (peuvent être multiples)
        foreach (var a in item.Elements(TorznabNs + "attr"))
        {
            var name = (string?)a.Attribute("name");
            var value = (string?)a.Attribute("value");
            if (string.Equals(name, "category", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var id))
                list.Add(id);
        }

        foreach (var a in item.Elements(NewznabNs + "attr"))
        {
            var name = (string?)a.Attribute("name");
            var value = (string?)a.Attribute("value");
            if (string.Equals(name, "category", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var id))
                list.Add(id);
        }

        // <category>...</category>
        foreach (var cat in item.Elements("category"))
        {
            var catText = (string?)cat;
            if (int.TryParse(catText, out var cid)) list.Add(cid);
        }

        return list.Distinct().ToList();
    }

    private static (int? stdId, int? specId) ResolveStdSpec(IReadOnlyCollection<int> ids)
    {
        int? parentStdId = null;
        int? childStdId = null;
        int? parentSpecId = null;
        int? childSpecId = null;

        foreach (var id in ids)
        {
            if (id >= 10000)
            {
                // Mirror std logic: prefer child (non-multiple of 1000) over parent.
                // e.g. [100000, 100314] → pick 100314, not 100000.
                if (id % 1000 == 0)
                    parentSpecId ??= id;
                else
                    childSpecId ??= id;
            }
            else if (id >= 1000 && id <= 8999)
            {
                if (id % 1000 == 0)
                    parentStdId ??= id;
                else
                    childStdId ??= id;
            }
        }

        // Child takes priority over parent (both std and spec)
        return (childStdId ?? parentStdId, childSpecId ?? parentSpecId);
    }

    private static Dictionary<string, string> ParseAttrs(XElement item)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // torznab:attr
        foreach (var a in item.Elements(TorznabNs + "attr"))
        {
            var name = (string?)a.Attribute("name");
            var value = (string?)a.Attribute("value");
            if (!string.IsNullOrWhiteSpace(name) && value is not null)
                dict[name.Trim()] = value.Trim();
        }

        // newznab:attr (certains flux)
        foreach (var a in item.Elements(NewznabNs + "attr"))
        {
            var name = (string?)a.Attribute("name");
            var value = (string?)a.Attribute("value");
            if (!string.IsNullOrWhiteSpace(name) && value is not null)
                dict[name.Trim()] = value.Trim();
        }

        return dict;
    }

    private static long? ParsePubDateToUnix(XElement item)
    {
        var pub = (string?)item.Element("pubDate") ?? (string?)item.Element("published");
        if (string.IsNullOrWhiteSpace(pub)) return null;

        // pubDate est souvent RFC1123, mais on laisse DateTimeOffset parser large
        if (DateTimeOffset.TryParse(pub, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUnixTimeSeconds();

        return null;
    }

    private static (int? seed, int? leech) ParseSeedLeechFromText(string text)
    {
        // mini parser permissif
        int? s = null, l = null;

        var seedMatch = System.Text.RegularExpressions.Regex.Match(text, @"seeders?\s*[:=]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (seedMatch.Success && int.TryParse(seedMatch.Groups[1].Value, out var sv)) s = sv;

        var leechMatch = System.Text.RegularExpressions.Regex.Match(text, @"leechers?\s*[:=]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (leechMatch.Success && int.TryParse(leechMatch.Groups[1].Value, out var lv)) l = lv;

        return (s, l);
    }

    private static string? TryString(Dictionary<string, string> attrs, string key)
        => attrs.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static int? TryInt(Dictionary<string, string> attrs, string key)
        => attrs.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : null;

    private static long? TryLong(Dictionary<string, string> attrs, string key)
        => attrs.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : null;

    private static long? TryLongValue(string? value)
        => !string.IsNullOrWhiteSpace(value) && long.TryParse(value, out var n) ? n : null;

    private static int? TryIntValue(string? value)
        => !string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var n) ? n : null;

    private static string? GetElementValueByLocalName(XElement item, string localName)
        => item.Elements().FirstOrDefault(el => string.Equals(el.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value;
}
