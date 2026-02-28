using System.Xml;
using System.Xml.Linq;

namespace Feedarr.Api.Services.Torznab;

/// <summary>
/// Parses XML documents with hardened security settings:
/// DTD processing is prohibited, external resolvers are disabled,
/// and document size is capped to prevent XML-bomb denial-of-service.
/// </summary>
internal static class XmlSecureParser
{
    // 5 million characters ≈ 5 MB — generous for Torznab responses
    private const long MaxDocumentCharacters = 5_000_000;

    /// <summary>
    /// Parses <paramref name="xml"/> into an <see cref="XDocument"/>.
    /// Throws <see cref="XmlException"/> if the input contains a DTD declaration
    /// or if the document exceeds <see cref="MaxDocumentCharacters"/>.
    /// </summary>
    internal static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxDocumentCharacters,
        };

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, LoadOptions.None);
    }
}
