using System.Collections.ObjectModel;

namespace Feedarr.Api.Services.ExternalProviders;

public sealed class ExternalProviderRegistry
{
    private readonly ReadOnlyDictionary<string, ExternalProviderDefinition> _definitionsByKey;
    private readonly IReadOnlyList<ExternalProviderDefinition> _definitions;

    public ExternalProviderRegistry()
    {
        var definitions = new List<ExternalProviderDefinition>
        {
            new(
                ProviderKey: ExternalProviderKeys.Tmdb,
                DisplayName: "TMDB",
                Kind: "movie",
                DefaultBaseUrl: "https://api.themoviedb.org/3/",
                UiHints: new ExternalProviderUiHints("movie", new[] { "TMDB" }),
                FieldsSchema: new[]
                {
                    new ExternalProviderFieldDefinition(
                        Key: "apiKey",
                        Label: "Cle API",
                        Type: "password",
                        Placeholder: "Entrez la cle API TMDB",
                        Required: true,
                        Secret: true),
                }),
            new(
                ProviderKey: ExternalProviderKeys.Tvmaze,
                DisplayName: "TVmaze",
                Kind: "tv",
                DefaultBaseUrl: "https://api.tvmaze.com/",
                UiHints: new ExternalProviderUiHints("tv", new[] { "TVmaze" }),
                FieldsSchema: new[]
                {
                    new ExternalProviderFieldDefinition(
                        Key: "apiKey",
                        Label: "Cle API (optionnel)",
                        Type: "password",
                        Placeholder: "Entrez la cle API TVmaze (optionnel)",
                        Required: false,
                        Secret: true),
                }),
            new(
                ProviderKey: ExternalProviderKeys.Fanart,
                DisplayName: "Fanart TV",
                Kind: "artwork",
                DefaultBaseUrl: "https://webservice.fanart.tv/v3/",
                UiHints: new ExternalProviderUiHints("artwork", new[] { "Fanart" }),
                FieldsSchema: new[]
                {
                    new ExternalProviderFieldDefinition(
                        Key: "apiKey",
                        Label: "Cle API",
                        Type: "password",
                        Placeholder: "Entrez la cle API Fanart",
                        Required: true,
                        Secret: true),
                }),
            new(
                ProviderKey: ExternalProviderKeys.Igdb,
                DisplayName: "IGDB",
                Kind: "game",
                DefaultBaseUrl: "https://api.igdb.com/v4/",
                UiHints: new ExternalProviderUiHints("game", new[] { "IGDB" }),
                FieldsSchema: new[]
                {
                    new ExternalProviderFieldDefinition(
                        Key: "clientId",
                        Label: "Client ID",
                        Type: "password",
                        Placeholder: "Entrez le Client ID",
                        Required: true,
                        Secret: true),
                    new ExternalProviderFieldDefinition(
                        Key: "clientSecret",
                        Label: "Client Secret",
                        Type: "password",
                        Placeholder: "Entrez le Client Secret",
                        Required: true,
                        Secret: true),
                }),
            new(
                ProviderKey: ExternalProviderKeys.Jikan,
                DisplayName: "Jikan (MAL)",
                Kind: "anime",
                DefaultBaseUrl: "https://api.jikan.moe/v4/",
                UiHints: new ExternalProviderUiHints("anime", new[] { "Anime", "MAL" }),
                FieldsSchema: Array.Empty<ExternalProviderFieldDefinition>()),
            new(
                ProviderKey: ExternalProviderKeys.GoogleBooks,
                DisplayName: "Google Books",
                Kind: "book",
                DefaultBaseUrl: "https://www.googleapis.com/books/v1/",
                UiHints: new ExternalProviderUiHints("book", new[] { "Books" }),
                FieldsSchema: new[]
                {
                    new ExternalProviderFieldDefinition(
                        Key: "apiKey",
                        Label: "Cle API (optionnel)",
                        Type: "password",
                        Placeholder: "Entrez la cle API Google Books (optionnel)",
                        Required: false,
                        Secret: true),
                }),
            new(
                ProviderKey: ExternalProviderKeys.TheAudioDb,
                DisplayName: "TheAudioDB",
                Kind: "audio",
                DefaultBaseUrl: "https://www.theaudiodb.com/api/v1/json/",
                UiHints: new ExternalProviderUiHints("audio", new[] { "Audio", "Music" }),
                FieldsSchema: new[]
                {
                    new ExternalProviderFieldDefinition(
                        Key: "apiKey",
                        Label: "Cle API",
                        Type: "password",
                        Placeholder: "Entrez la cle API TheAudioDB",
                        Required: true,
                        Secret: true),
                }),
            new(
                ProviderKey: ExternalProviderKeys.ComicVine,
                DisplayName: "Comic Vine",
                Kind: "comic",
                DefaultBaseUrl: "https://comicvine.gamespot.com/api/",
                UiHints: new ExternalProviderUiHints("comic", new[] { "Comic", "BD" }),
                FieldsSchema: new[]
                {
                    new ExternalProviderFieldDefinition(
                        Key: "apiKey",
                        Label: "Cle API",
                        Type: "password",
                        Placeholder: "Entrez la cle API Comic Vine",
                        Required: true,
                        Secret: true),
                }),
        };

        _definitions = definitions.AsReadOnly();
        _definitionsByKey = new ReadOnlyDictionary<string, ExternalProviderDefinition>(
            definitions.ToDictionary(
                d => d.ProviderKey,
                d => d,
                StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ExternalProviderDefinition> List() => _definitions;

    public bool TryGet(string? providerKey, out ExternalProviderDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            definition = default!;
            return false;
        }

        return _definitionsByKey.TryGetValue(providerKey.Trim(), out definition!);
    }
}

public sealed record ExternalProviderDefinition(
    string ProviderKey,
    string DisplayName,
    string Kind,
    string? DefaultBaseUrl,
    ExternalProviderUiHints UiHints,
    IReadOnlyList<ExternalProviderFieldDefinition> FieldsSchema);

public sealed record ExternalProviderFieldDefinition(
    string Key,
    string Label,
    string Type,
    string? Placeholder,
    bool Required,
    bool Secret);

public sealed record ExternalProviderUiHints(
    string Icon,
    IReadOnlyList<string> Badges);
