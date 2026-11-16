# RSS silent-film query cleanup

The isolated local snapshot identifies releases 12901 and 12959 as Film, with
TitleClean containing respectively `Les Roses magiques 1906 MUET` and
`Le Fakir de Singapour 1908 MUET`, Year null, and a V2 reject reason.
The generator directly reuses parsed TitleClean, so the contamination is reproduced
at query generation. No assertion is made about a historical HTTP request payload.

FindVideoReleaseYear rejects non-metadata words between a candidate year and the
technical boundary. MUET was not recognized in that position, which prevented year
extraction and left both the year and marker in the searched title.

Recognize uppercase MUET only inside this year-to-technical-boundary tail, when the
existing technical suffix is Rich. It is not added to global title normalization or
unconditional suffix trimming. The rest of the tail must still be recognized
metadata. Bare titles with MUET, title-initial years and MUET before the creation
year remain intact. Broadcast/annual-season policy and ranker thresholds unchanged.

Before/after frozen tests: 294 existing tests pass unchanged; four query-cleanup
cases and one localized-title ranking scenario fail before and pass after, with
four negative cases passing in both versions. Final targeted selection: 303 green.

Measured on exported rows from the isolated SQLite snapshot: all 60 MUET releases
(52 Film, 8 Animation) now have clean generated title queries and separately retained
years. They represent 59 probable normalized title/year pairs, not independently
verified provider works. A temporary local measurement test was removed afterward;
inputs/results remain outside Git in /private/tmp/feedarr-rss-title-audit/.

Other residual words after years among unidentified parsed titles were also counted:
VOF 9, VFF 4, CUSTOM 3, IMAX 3, FANSUB 2; these overlap other groups and have not been
proven to share the MUET cause. No additional stripping rule was introduced.

Simulated French provider-owned title versus a different original accepts only with
matching year/type under existing rules; remake year and competing homonym controls
remain non-accepted. No real-world identity, poster recovery or false-positive rate
is claimed. External provider calls: zero. Active SQLite unchanged. Release build
and git diff --check pass.
