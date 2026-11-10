# Public poster downloads (P1-01)

## Download paths

Manual selections in PostersController call PosterFetchService.Save*PosterAsync.
Automatic jobs call PosterFetchService.FetchPosterAsync through the matching orchestrator.
Both use the existing typed provider clients to fetch image bytes. Banner downloads in
PostersController use TMDB/Fanart directly. Store materialization also writes image bytes.

All eleven public provider registrations now use UsePublicProviderTransport:
TMDB, Fanart, IGDB, TVmaze, Jikan, Google Books, TheAudioDB, Comic Vine,
Open Library, MusicBrainz and RAWG. This covers metadata-supplied URLs, manual cover
URLs, API requests and CDN requests without duplicating the SSRF implementation.

The transport reuses OutboundDestinationPolicy (Strict), SsrfGuardHandler,
ProtocolDowngradeRedirectHandler and ValidatedOutboundConnector. DNS is checked
before requests and at connection time; sockets receive a validated IP snapshot.
Automatic redirects, system HTTP proxies and cookies are disabled. Each redirect
is checked again. Cross-origin public GET/HEAD redirects support legitimate CDN
flows; sensitive headers are not forwarded. HTTPS-to-HTTP redirects are rejected.
Existing private integration exceptions remain available to Torznab/Arr and do not
apply to public providers. Invalid certificates are not accepted for these providers.

Before writing a new image in PosterFetchService or the banner controller,
PosterImageWriter decodes it with the existing SkiaSharp codec and requires successful
pixel decoding. HTML, unknown formats, incomplete/corrupt images and images larger
than 40 megapixels are rejected without replacing an existing file or saving its
poster reference. Valid bytes are written unchanged. Rejected downloads may surface
as existing error paths; they are never reported as successfully published images.
Existing cached files are not automatically purged or revalidated by this correction.

## Verification limits

Tests use fake DNS, sockets and HTTP responses exclusively. They cover strict private
address rejection, rebinding between preflight and connection, redirect destinations,
CDN success, invalid payload publication and simulated network/timeout failures.
Provider contract fixtures now contain real image bytes. No live provider, credentials,
private service, actual Internet TLS handshake or deployment HTTP proxy was tested.
Public provider URLs configured to reach private networks are intentionally rejected;
using a system outbound proxy for public providers is no longer supported by this path.
