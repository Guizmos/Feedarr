# Auth V2 — web sessions, browser login and PWA

Auth V2.1 added JSON endpoints and persistent sessions alongside HTTP Basic.
Auth V2.2 connects the React login and PWA to them. SmartAuth trust, bootstrap scopes
and explicit Basic API contracts remain unchanged. XTubee continues to send Basic
on every request.

## Endpoints

All `/api/auth` responses and cookie-authenticated responses are `Cache-Control: no-store`,
enforced when headers are sent even if a controller sets public caching. Basic API
cache headers retain their existing behavior.
Exact public dispatch is limited to GET `/api/auth/session`, POST `/api/auth/login`,
and POST `/api/auth/logout`. Explicit Basic keeps the existing middleware path,
including its challenge and throttling; an invalid Basic header cannot fall back
to a valid session. Unknown auth paths do not receive this exception.

- `POST /api/auth/login`: JSON `{ "username": "…", "password": "…", "rememberMe": false }`.
  Requires HTTPS, same-origin Origin (or same-origin Referer when Origin is absent),
  configured credentials. A successful login returns the state
  below and sets a new cookie, atomically revoking the previous supplied session.
  Failed login does not destroy a previous session. No token is accepted from the body.
- `GET /api/auth/session`: returns state with HTTP 200 even when anonymous, expired,
  or unconfigured, without a Basic challenge unless explicit Basic was sent.
  An active session is renewed within its fixed absolute deadline.
- `POST /api/auth/logout`: same origin and HTTPS required; a valid cookie also requires
  `X-Feedarr-CSRF`. Revokes the row and expires the cookie. Repeating logout without
  a valid session is safe and returns `{ "authenticated": false }`.

State fields: `authenticated` (a web session, not Basic/Open access), `authRequired`
(current connection policy), `authConfigured`, `authMode`, `setupCompleted`,
`webSessionAvailable`, `csrfToken`, `expiresAt`, `absoluteExpiresAt`, `rememberMe`.
Anonymous state has null CSRF/expiry fields. Expirations are UTC ISO 8601 values.
No username, password, password hash/salt, or session cookie value is returned.

Controller failures use ProblemDetails with `status`, `title` and `code`:

| Status | Code |
| --- | --- |
| 400 | `https_required` |
| 401 | `invalid_credentials` (same for unknown username/wrong password) |
| 403 | `setup_required`, `cross_site_request_blocked` |
| 409 | `credentials_not_configured`, `security_configuration_changed` |
| 429 | `too_many_auth_attempts`, with `retryAfterSeconds` and `Retry-After` |

The existing origin middleware can reject first with JSON
`{error:"cross_site_request_blocked",message:…,reason:…}`. Model binding uses the
existing MVC validation ProblemDetails contract. The existing rate limiter returns
`429 {error:"too many requests"}` when its per-IP policy rejects before login.
The login policy permits ten attempts per minute per effective client IP regardless
of supplied username, in addition to existing Basic backoff and the global limiter.
Login bodies are limited to 8 KiB; overlarge bodies use the server's 413 handling.

## Sessions, cookies and revocation

Tokens contain 32 random bytes; only SHA-256(token) is stored. CSRF is a deterministic
HMAC derived from that bearer token with a distinct context. Cookie: HttpOnly,
Secure, SameSite=Lax, no Domain. Path uses Request.PathBase, otherwise the persisted
PublicBaseUrl prefix, otherwise `/`. Raw forwarded-prefix headers are ignored.
At `/`, the name is `__Host-FeedarrSession`; at a prefix it is `FeedarrSession`.
TLS state and public origin use only the existing trusted forwarded-header pipeline.
HTTP LAN refuses session creation; Basic's transport behavior is unchanged.

Default sessions have a 30-minute idle timeout and 12-hour absolute limit, with a
nonpersistent cookie. Remember-me sessions have a 7-day idle timeout and 30-day
absolute limit; cookie expiry is the absolute deadline. Server-side expiry is
always enforced, including after process restart. A session cookie may be restored
by a browser; a nonpersistent cookie is not a promise to log out on browser close.

Migration `0059_web_sessions.sql` adds `web_sessions`, `web_security_generation`, an
expiry index and security-setting triggers. Changes, insertion or deletion of the
security document increment the generation and delete sessions atomically. This
conservatively includes PublicBaseUrl changes. Unchanged settings do not revoke.
Generation and both expirations are checked atomically on each authenticated
request, without the Basic settings cache. Login captures generation before
credential validation and inserts only if it still matches. Password storage is
unchanged; the shared validator is the extracted existing Basic validator.

## CSRF and API compatibility

A valid cookie gets an explicit HttpContext marker. Every unsafe request using it
requires both same origin and the session-bound `X-Feedarr-CSRF`, before any legacy
CSRF exemption, including security settings and setup/configuration mutations.
The legacy AllowedOrigins list cannot authorize cookie mutations. Basic and
bootstrap retain their old policies and do not need the session CSRF token.
Login has strict origin protection even without a cookie; logout additionally
requires the bound token when a session is valid. Safe requests do not require it.

A stolen bearer cookie cannot be intrinsically distinguished from its owner.
HttpOnly/Secure, CSRF checks, expiry and server revocation limit exposure; they do
not claim to prevent replay by an attacker already holding the full token.

No API redirect is introduced. Feed/release/source/poster authorization, JSON,
torrent binary responses and magnet redirects retain their existing paths. No new
recovery bypass is added; Basic and bootstrap retain their existing scope.

## Auth V2.2 browser integration

Exact known React routes and public icons/build assets can load anonymously. They
contain no user data. The server marks neutral HTML with
`X-Feedarr-Shell: public-v1`. The auth gate checks session state before mounting
private components and uses server decisions rather than duplicating SmartAuth.

The browser fetch adapter adds `X-Feedarr-Web: 1` to same-origin API calls and the
in-memory CSRF token to mutations, including legacy direct fetch callers. No Basic
credentials are created by JavaScript. Web fetches explicitly set an empty
Authorization header so the browser does not add a cached HTTP authentication entry
([Fetch standard](https://fetch.spec.whatwg.org/#http-network-or-cache-fetch)); this
still permits session cookies. Caller-supplied nonempty Authorization is untouched.
The backend returns JSON 401 without a Basic challenge for the marked web path,
browser API navigations and same-origin poster images without explicit Basic.
Explicit Basic always keeps the original challenge/error contract.

Login supports password-manager autocomplete and bounded remember-me sessions.
Credentials remain only in the submitted form/request, never in storage or URLs.
Network failures show a retry state rather than declaring the session expired.
401 rechecks session state; generic 403/429 remain errors, while setup/security-lock
403 responses refresh the server policy. Security saves refresh session state after
revocation. Session checks also run on foreground/BFCache restoration and expiry.
Page hiding masks the root; revalidation gates restored private React trees.
Logout revokes server-side, broadcasts invalidation to other tabs and replaces the
page to discard module/React state while preserving non-sensitive preferences.

During incomplete setup, configured credentials may create a session so the wizard
can continue after its security step. The existing setup lock runs before cookie
validation on API requests: a setup session cannot read the private feed. Unconfigured
initial setup still uses the existing bootstrap/local rules. An inconsistent secured,
unconfigured installation after completed setup gets an explicit recovery message,
without opening a privileged route. HTTP LAN gets an HTTPS requirement; Basic API
access retains its prior behavior.

## Auth V2.2 public PWA cache

The service worker fetches public resources with credentials omitted. It precaches
only marked neutral index HTML, versioned public icons/manifest and build assets
listed in generated `pwa-assets.json`. A failed precache prevents activation.
Navigation responses, APIs, 401/403, redirects and private/no-store responses are
never cached. Offline navigation awaits the known public index fallback; the React
gate then shows server-unavailable rather than private offline data. Each frontend
build versions the worker registration/cache. Activation removes only old
`feedarr-pwa-*` caches. Source and published worker come from the same build, and
published icons are byte-for-byte copies of the existing versioned source icons.

Physical validation remains necessary for Safari/iPhone standalone install, cold
launch, password-manager save/autofill, expiry/resume, logout/back navigation, old
HTTP Basic browser caches and offline recovery. Static/controller tests do not
establish the historical iOS download bug's exact device-specific cause. Browser
visual automation was unavailable in the implementation environment.
