# Cross-category unmatched titles audit

## Observations (local SQLite read-only)

10105: Astroboy, Animation, 2009, V2 reject. Local identified comparator 2878:
Astro Boy (2009), TMDB 16577. Actual candidates/reasons are not captured; the
simulated lexical rejection is established, but actual search coverage is not.
The typography audit and frozen corpus remain applicable. Do not compact all spaces.

10106: 66.minutes.S2026E12.VOF.1080p.WEB.AAC.2.0.H264-BYOR, Emission,
parsed title 66 minutes, year null, season 20, episode null, V2 ambiguous.
9902/9903 (Serie) and 10108 (Emission) have the same annual-marker defect.
These are four local entities, two normalized title groups, not four verified works.
The actual candidate corresponding to the user-proposed 2006 series is not captured.

## Demonstrated mechanism and correction

RxSeasonEpisode accepted only one/two digit seasons; RxSeasonOnly accepted the
prefix S20 of S2026E12. Recognize explicit S19xxE/S20xxE markers as numbered
annual seasons, preserve the episode, and prohibit partial digit matches in
season-only parsing. Never turn the annual season into the series creation year.
Provider numbering may differ: no calendar-year-to-season-ordinal mapping is inferred.
Emission and Serie both route to TV V2 (TMDB and enabled TVMaze); fixing fields does
not remove the requirement for episode evidence or resolve competitors.

## Frozen benchmark comparison

Before: 212 targeted tests green. After, against the unchanged golden: one failure,
Show.S2024E123.1080p changed from season 20 / no episode to season 2024 / episode 123;
all other baseline cases passed. Corrected that single known-bad golden expectation.
Final expanded selection: 235 cases green, including annual positive/malformed
markers, typography collisions, official aliases, multilingual and quality controls.
A series created in 2006 with requested S2026E12 remains unaccepted without proof;
a simulated verified numbered episode can accept without altering its creation year.

Measured real new identities/publications: none demonstrated. Four local releases
have potentially improved evidence collection, not four guaranteed recoveries.
Provider requests: zero. Active SQLite unmodified. Release build and diff check pass.
Next evidence required: captured bounded search candidates and ranked reasons for
both witness releases, then actual numbered episode coverage. No score changes.
