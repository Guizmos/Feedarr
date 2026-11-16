# Title typography audit — beta

## Local observation

Release 10105: Astroboy.2009.MULTI.VFF.1080p.BluRay.HDLight.DTS.HD.MA.5.1.av1-Badloose.
Parsed title Astroboy, year 2009, category Animation; latest persisted decision is
v2:ranking:business_abstention:reject. Release 2878 has parsed title Astro Boy,
year 2009, category Animation and TMDB identity 16577. This is a local comparison,
not independent validation of either provider response.

The automatic path uses parsed query proposals, unfiltered movie searches in the
configured language and English, with full bounded pagination. Manual search uses
list helpers with no year filter and does not apply the V2 acceptance decision.
No captured candidate list establishes whether identity 16577 reached the automatic
ranker for 10105. A reject does not reveal the individual contradictions.

## Shared comparison

Strict normalization removes diacritics, normalizes punctuation to word boundaries
and collapses whitespace; the ranker additionally handles apostrophe omission and
provider-owned official titles. Similarity uses ordered tokens, so Astroboy and
Astro Boy share no tokens. This is reproduced with a simulated candidate, not a
reproduction of the actual provider journey. Search generation preserves the
parsed title and does not guess word boundaries. Movie collection does not fetch
alternative-title endpoints in the examined active V2 path.

Deleting every word boundary is unsafe: Therapist / The Rapist and Nowhere /
Now Here become identical. Neither the same year nor the same media type proves
that such titles denote the same work. No global normalization change is justified.
A provider-owned alias already resolves the synthetic case without weakening
identity, year or media-type checks. Obtaining official alternatives for plausible
candidates is an avenue to evaluate, not a demonstrated fix for this release.

## Impact and validation

A read-only local comparison of unidentified releases against identified titles,
using compact alphanumeric keys with identical year and category, found one
release/entity pair: 10105 versus Astro Boy. This is a potential recovery only.
It does not estimate titles absent from the local identified catalogue.

TitleTypographyCorpusTests freezes eight typography/collision pairs and a compound
alias guard case. With unchanged production code, four typography positives accept;
compact-only and reordered collision controls do not accept. A simulated official
alias accepts; incorrect year, identity ownership and type controls do not.
The existing multilingual and quality corpus remains green (43 targeted test cases
in total). Before/after production behavior is unchanged; no real recovery or
publication is claimed. Provider calls: zero. Active SQLite: read-only access only.

Next required evidence: bounded automatic search responses for Astroboy, typed
candidate identities and years, individual ranked reasons, and official alias data
for any plausible candidate. Compare the exact same captured responses before and
after any subsequent change. No score or threshold change is recommended.
