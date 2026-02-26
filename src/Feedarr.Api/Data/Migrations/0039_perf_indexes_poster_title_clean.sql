-- Compound index for title-based poster reuse queries (GetPosterForTitleClean).
-- This method runs up to 3 sequential queries: exact (title_clean + year + media_type),
-- then fallback without year, then by normalizedTitle. The first two steps hit this index.
CREATE INDEX IF NOT EXISTS idx_releases_poster_title_clean
    ON releases(title_clean, year, media_type)
    WHERE title_clean IS NOT NULL;

-- Index on poster_matches for the TryGetByTitleKey hot path
-- (normalized_title + media_type + year lookup, ordered by confidence DESC).
CREATE INDEX IF NOT EXISTS idx_poster_matches_title_key
    ON poster_matches(normalized_title, media_type, year, confidence DESC);
