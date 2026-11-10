# Smart authentication: connection trust (P0-01)

Smart exempts only a direct loopback connection (IPv4, IPv6 or IPv4-mapped IPv6),
with no forwarding metadata, a local Host and no external PublicBaseUrl.
Host may require authentication; it can never establish that the client is local.
Missing client IP, private LAN addresses and Docker bridge peers require authentication.
The local socket address is the server endpoint, not evidence of client identity.

The original transport peer and presence of forwarding metadata are recorded before
ASP.NET Core ForwardedHeadersMiddleware rewrites RemoteIpAddress/Host. A configured
proxy peer never receives the loopback exemption, even without forwarded headers or
when it reports a loopback client. This also protects loopback-only bootstrap-token
issuance; the existing bootstrap secret remains an alternative.

Only Security:KnownProxies and Security:KnownNetworks establish forwarding trust.
Framework default loopback trust is removed. With no configured proxies/networks,
forwarded-header processing is disabled (empty trust lists otherwise mean trust-all).
Raw Forwarded, X-Forwarded-*, X-Real-IP and X-Original-* metadata can remove the local
exemption, but never grant one. Malformed or multi-hop values cannot grant exemption.

## Deployment implications

- A browser connecting directly to localhost keeps Smart's local exemption.
- LAN/Docker bridge requests require configured credentials, even with Host: localhost.
- Reverse proxies, including local proxies, must be explicitly listed. Prefer exact
  proxy IPs; do not trust a whole private network unless every peer is controlled.
- TLS-terminating proxies must sanitize incoming forwarding headers and supply their
  own. Security:TrustedReverseProxyTls requires explicit proxy/network configuration.
- A loopback proxy without any forwarding metadata and without being declared is
  indistinguishable from a direct local process at the HTTP server. Declare its
  loopback address in KnownProxies (all connections from that address then require
  authentication), configure external PublicBaseUrl, or use Strict mode. Do not deploy
  an undeclared, metadata-free loopback relay with Smart's local exemption enabled.
- Strict/Open, explicit public assets and GET /health retain their existing behavior.
  Existing initial setup/bootstrap scopes and credential validation are unchanged.

The P0 tests cover policy decisions and execute the provenance capture, real
ForwardedHeadersMiddleware and BasicAuthMiddleware in sequence. They do not replace
an end-to-end check of a deployment's actual proxy, Docker networking or TLS setup.
