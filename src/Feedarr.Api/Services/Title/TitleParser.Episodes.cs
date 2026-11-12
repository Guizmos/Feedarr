using System.Globalization;

namespace Feedarr.Api.Services.Titles;

public sealed partial class TitleParser
{
    private static class EpisodeRecognition
    {
        public static void ParseSeriesTokens(string raw, ParsedTitle p)
        {
            var m = RxSeasonEpisode.Match(raw);
            if (m.Success)
            {
                p.Season = TryInt(m.Groups["s"].Value);
                p.Episode = TryInt(m.Groups["e"].Value);
                return;
            }

            var m2 = RxAltSeasonEpisode.Match(raw);
            if (m2.Success)
            {
                p.Season = TryInt(m2.Groups["s"].Value);
                p.Episode = TryInt(m2.Groups["e"].Value);
                return;
            }

            var md = RxSeasonDisc.Match(raw);
            if (md.Success)
            {
                p.Season = TryInt(md.Groups["s"].Value);
                return;
            }

            var ms = RxSeasonOnly.Match(raw);
            if (ms.Success)
            {
                p.Season = TryInt(ms.Groups["s"].Value);
                return;
            }

            var mw = RxSeasonWord.Match(raw);
            if (mw.Success)
            {
                p.Season = TryInt(mw.Groups["s"].Value);
                return;
            }

            // Episode seul sans saison (ex: E06)
            var me = RxEpisodeOnly.Match(raw);
            if (me.Success)
                p.Episode = TryInt(me.Groups["e"].Value);
        }

        public static void ParseEmissionTokens(string raw, ParsedTitle p)
        {
            if (TryExtractAirDate(raw, out var airDateIso, out var year))
            {
                p.AirDate = airDateIso;
                if (year.HasValue) p.Year = year;
                return;
            }

            ParseSeriesTokens(raw, p);
        }

        public static bool TryExtractAirDate(string raw, out string? iso, out int? year)
        {
            iso = null;
            year = null;

            var m = RxAirDateDmy.Match(raw);
            if (!m.Success)
                m = RxAirDateYmd.Match(raw);

            if (!m.Success) return false;

            var y = TryInt(m.Groups["y"].Value);
            var mo = TryInt(m.Groups["m"].Value);
            var d = TryInt(m.Groups["d"].Value);
            if (!y.HasValue || !mo.HasValue || !d.HasValue) return false;

            if (!DateTime.TryParseExact(
                    $"{y.Value:D4}-{mo.Value:D2}-{d.Value:D2}",
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dt))
            {
                return false;
            }

            iso = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            year = dt.Year;
            return true;
        }

    }
}
