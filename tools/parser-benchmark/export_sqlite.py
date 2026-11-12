#!/usr/bin/env python3
"""Standalone, bounded SELECT export. Output only to stdout; never open an output file.

Can be streamed to `ssh ... python3 - ...` without installing anything remotely.
An OS-enforced read-only view/account is required, including the SQLite sidecars.
Do not use immutable=1 against a live WAL database.
"""
import argparse
import collections
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import sys

CATEGORIES = {"Autre", "Film", "Serie", "Emission", "Spectacle", "JeuWindows",
              "Animation", "Anime", "Audio", "Book", "Comic"}
PROVIDERS = {"tmdb", "tvmaze", "igdb", "rawg", "jikan", "fanart", "googlebooks",
             "openlibrary", "comicvine", "musicbrainz", "theaudiodb"}
PARSED_FIELDS = ("TitleClean", "Year", "Season", "Episode", "AirDate", "Resolution",
                 "Source", "Codec", "ReleaseGroup", "MediaType")
READ_COLUMNS = {
    "sources": {"name"},
    "media_entities": {"id", "poster_file", "manual_match_provider", "tmdb_id", "tmdb_media_type", "tvdb_id"},
    "releases": {"id", "source_id", "title", "unified_category", "poster_file", "entity_id", "published_at_ts",
                 "created_at_ts", "title_clean", "year", "season", "episode", "resolution", "source", "codec",
                 "release_group", "media_type", "tmdb_id", "tmdb_media_type", "tvdb_id", "ext_provider",
                 "ext_provider_id", "poster_provider", "poster_provider_id", "poster_last_attempt_ts",
                 "poster_last_error", "title_manual_override", "needs_rebind"}}
SENSITIVE = re.compile(
    r"https?://|ftp://|magnet:|www\.|[\w.+-]+@[\w.-]+\.[a-z]{2,}|"
    r"\b(?:api[_ -]?key|token|password|passwd|cookie|authorization|secret)\s*[:=]|"
    r"\b[0-9a-f]{24,}\b|"
    r"(?:/volume\d/|/home/|/Users/|\\\\)|[\x00-\x08\x0b-\x1f\x7f]", re.I)
DOMAIN = re.compile(r"\b[\w-]+\.(?:com|net|org|ovh|io|ru|fr|local|internal|lan)(?:\b|/)", re.I)


def sensitive(value):
    if isinstance(value, str):
        if SENSITIVE.search(value):
            return True
        for match in DOMAIN.finditer(value):
            # Uppercase release notation is not a hostname; schemes/URLs still fail above.
            if not re.fullmatch(r"(?:(?:19|20)\d{2}|VOST|VO)\.FR", match.group()):
                return True
        # Avoid confusing malformed four-component version numbers with IPv4 addresses.
        return any(all(int(part) <= 255 for part in match.group().split('.'))
                   for match in re.finditer(r"\b(?:\d{1,3}\.){3}\d{1,3}\b", value))
    if isinstance(value, dict):
        return any(sensitive(k) or sensitive(v) for k, v in value.items())
    if isinstance(value, list):
        return any(sensitive(v) for v in value)
    return False


def token(seed, kind, value):
    # Pseudonyms support grouping; they are not a promise of anonymous releases.
    return kind + "-" + hashlib.sha256(f"{seed}:{kind}:{value}".encode()).hexdigest()[:16]


def readonly_connection(path, local_snapshot=False):
    path = Path(path).resolve(strict=True)
    uri = path.as_uri() + "?mode=ro"
    if local_snapshot:
        root = (Path(__file__).resolve().parent / "local").resolve()
        if root not in path.parents or re.match(r"^/volume\d+(?:/|$)", str(path)) or str(path).startswith('\\\\'):
            raise ValueError("snapshot must be an independent local backup under local/")
        if any(Path(str(path) + suffix).exists() for suffix in ("-wal", "-shm", "-journal")):
            raise ValueError("standalone snapshot must have no sidecars")
        # Only for an explicitly declared, independent consistent backup, never a live DB.
        # immutable disables journals/SHM/locks without changing filesystem permissions.
        uri += "&immutable=1"
    # mode=ro alone can create/write SHM on a writable filesystem. Fail closed.
    for item in ([] if local_snapshot else [path.parent, path, Path(str(path) + "-wal"), Path(str(path) + "-shm")]):
        if item.exists() and os.access(item, os.W_OK):
            raise ValueError("OS read-only directory/database/sidecars required")
    connection = sqlite3.connect(uri, uri=True, timeout=5)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA temp_store=MEMORY")
    connection.execute("PRAGMA query_only=ON")
    allowed = {sqlite3.SQLITE_SELECT, sqlite3.SQLITE_TRANSACTION, sqlite3.SQLITE_RECURSIVE}

    def authorize(action, arg1, arg2, db, trigger):
        if action == sqlite3.SQLITE_PRAGMA and arg1 == "table_info" and arg2:
            return sqlite3.SQLITE_OK
        if action == sqlite3.SQLITE_READ:
            return sqlite3.SQLITE_OK if arg1 in READ_COLUMNS and (not arg2 or arg2 in READ_COLUMNS[arg1]) else sqlite3.SQLITE_DENY
        if action == sqlite3.SQLITE_FUNCTION:
            return sqlite3.SQLITE_OK if (arg2 or "").lower() in {"coalesce", "max", "length", "lower", "instr", "row_number", "like", "glob"} else sqlite3.SQLITE_DENY
        return sqlite3.SQLITE_OK if action in allowed else sqlite3.SQLITE_DENY

    connection.set_authorizer(authorize)
    return connection


def columns(connection, table):
    return {row["name"] for row in connection.execute(f"PRAGMA table_info({table})")}


def classify_error(error_expression):
    # Raw errors may contain URLs/tokens. Classify inside SQL; never select them.
    return f"""CASE
      WHEN {error_expression} IS NULL OR {error_expression} = '' THEN 'none'
      WHEN lower({error_expression}) LIKE '%no%match%' THEN 'no_match'
      WHEN lower({error_expression}) LIKE '%429%' OR lower({error_expression}) LIKE '%rate%limit%' THEN 'rate_limit'
      WHEN lower({error_expression}) LIKE '%401%' OR lower({error_expression}) LIKE '%403%' THEN 'authorization'
      WHEN lower({error_expression}) LIKE '%timeout%' THEN 'timeout'
      WHEN lower({error_expression}) LIKE '%title_clean missing%' THEN 'missing_title'
      ELSE 'other' END"""


def export(connection, size=1500, seed="feedarr-parser-v2.1"):
    if not 1 <= size <= 2000:
        raise ValueError("size must be between 1 and 2000")
    rc, ec = columns(connection, "releases"), columns(connection, "media_entities")
    required = {"id", "title", "source_id", "unified_category", "poster_file"}
    if not required <= rc:
        raise ValueError("unsupported releases schema")
    join = "entity_id" in rc and {"id", "poster_file"} <= ec

    def r(name, fallback="NULL"):
        return f"r.{name}" if name in rc else fallback

    def e(name, fallback="NULL"):
        return f"me.{name}" if join and name in ec else fallback

    # Same COALESCE semantics as ReleaseRepository's missing-poster counts/list state,
    # including empty-string release posters shadowing entity posters.
    poster = f"COALESCE(r.poster_file, {e('poster_file')})"
    has_poster = f"CASE WHEN {poster} IS NOT NULL AND {poster} <> '' THEN 1 ELSE 0 END"
    error = classify_error(r("poster_last_error"))
    ts = r("published_at_ts", r("created_at_ts", "0"))
    connection.execute("BEGIN")
    max_ts = connection.execute(f"SELECT COALESCE(MAX({ts}),0) FROM releases r").fetchone()[0]
    cutoff = int(max_ts or 0) - 180 * 86400
    source_names = "0"
    if "name" in columns(connection, "sources"):
        text = f"r.title || ' ' || COALESCE({r('title_clean')},'') || ' ' || COALESCE({r('release_group')},'')"
        source_names = f"EXISTS(SELECT 1 FROM sources s WHERE length(s.name)>=3 AND instr(lower({text}),lower(s.name))>0)"
    projection = ["r.id", "r.source_id", "r.title", "r.unified_category",
                  f"{has_poster} AS has_poster", f"{source_names} AS contains_source_name",
                  f"CASE WHEN {ts} IS NULL OR {ts}=0 THEN 'unknown' WHEN {ts}>={cutoff} THEN 'recent' ELSE 'older' END AS age_band",
                  "CASE WHEN length(r.title)>80 OR instr(r.title,'[')>0 OR instr(r.title,'(')>0 THEN 'complex' ELSE 'simple' END AS complexity",
                  f"{error} AS error_class"]
    names = ["title_clean", "year", "season", "episode", "resolution", "source", "codec",
             "release_group", "media_type", "tmdb_id", "tmdb_media_type", "tvdb_id", "ext_provider",
             "ext_provider_id", "poster_provider", "poster_provider_id", "poster_last_attempt_ts",
             "title_manual_override", "needs_rebind"]
    for name in names:
        expression = r(name)
        if name in {"poster_provider_id", "ext_provider_id"}:
            expression = f"CASE WHEN length({expression}) BETWEEN 1 AND 12 AND CAST({expression} AS TEXT) NOT GLOB '*[^0-9]*' THEN {expression} ELSE NULL END"
        projection.append(f"{expression} AS {name}")
    projection += [f"{e('manual_match_provider')} AS manual_provider",
                   f"{e('tmdb_id')} AS entity_tmdb_id", f"{e('tmdb_media_type')} AS entity_tmdb_type",
                   f"{e('tvdb_id')} AS entity_tvdb_id"]
    numeric_seed = int(hashlib.sha256(seed.encode()).hexdigest()[:7], 16)
    per_stratum = size
    # Bounded projection, no source URLs, credentials, GUIDs, JSON metadata or file paths.
    # Reads/sorts can still scan releases; use a quiet period and an OS read-only view.
    sql = f"""WITH projected AS (
      SELECT {','.join(projection)} FROM releases r
      {'LEFT JOIN media_entities me ON me.id=r.entity_id' if join else ''}
    ), ranked AS (
      SELECT *, ROW_NUMBER() OVER (
        PARTITION BY unified_category,has_poster,source_id,age_band,complexity
        ORDER BY ((id * 1103515245 + {numeric_seed}) & 2147483647),id) AS sample_rank
      FROM projected
    ) SELECT * FROM ranked WHERE sample_rank<=?
      ORDER BY sample_rank,((id * 1103515245 + {numeric_seed}) & 2147483647),id LIMIT ?"""
    excluded = collections.Counter()
    candidates = []
    for row in connection.execute(sql, (per_stratum, max(100, size * 12))):
        if row["contains_source_name"]:
            excluded["source_name"] += 1
            continue
        parsed = {key: row[name] for key, name in zip(
            ("TitleClean", "Year", "Season", "Episode", "Resolution", "Source", "Codec", "ReleaseGroup", "MediaType"),
            ("title_clean", "year", "season", "episode", "resolution", "source", "codec", "release_group", "media_type")) if name in rc}
        provider = row["poster_provider"] or row["ext_provider"]
        provider = provider.lower() if isinstance(provider, str) and provider.lower() in PROVIDERS else None
        provider_id = row["poster_provider_id"] or row["ext_provider_id"]
        provider_id = str(provider_id) if provider_id is not None and re.fullmatch(r"[0-9]{1,12}", str(provider_id)) else None
        tmdb_type = row["tmdb_media_type"] if row["tmdb_id"] is not None else row["entity_tmdb_type"]
        association = {"tmdbId": row["tmdb_id"] or row["entity_tmdb_id"],
                       "tmdbType": tmdb_type if tmdb_type in {"movie", "tv", "series"} else None,
                       "tvdbId": row["tvdb_id"] or row["entity_tvdb_id"],
                       "provider": provider, "providerId": provider_id,
                       "manualMatch": bool(row["manual_provider"])}
        associated = any(association[k] is not None for k in ("tmdbId", "tvdbId", "providerId"))
        entry = {"id": token(seed, "release", row["id"]), "raw": row["title"],
                 "category": row["unified_category"] if row["unified_category"] in CATEGORIES else "Autre",
                 "sourceGroup": token(seed, "source", row["source_id"]),
                 "hasPoster": bool(row["has_poster"]), "ageBand": row["age_band"],
                 "complexity": row["complexity"], "stored": parsed, "association": association,
                 "matchStatus": "associated" if associated else "unknown" if "poster_last_attempt_ts" not in rc else "attempted" if row["poster_last_attempt_ts"] else "never_attempted",
                 "errorClass": row["error_class"] if "poster_last_error" in rc else "unknown"}
        if "title_manual_override" in rc: entry["manualTitle"] = bool(row["title_manual_override"])
        if "needs_rebind" in rc: entry["needsRebind"] = bool(row["needs_rebind"])
        if not isinstance(entry["raw"], str) or not 1 <= len(entry["raw"]) <= 1000 or sensitive(entry):
            excluded["sensitive_or_invalid"] += 1
            continue
        candidates.append(entry)
    connection.rollback()
    selected = sample(candidates, size, seed)
    return {"schemaVersion": 1, "origin": "production-export", "seed": seed,
            "selection": {"requested": size, "missingPosterTarget": .7,
                          "candidateLimit": max(100, size * 12), "candidatesAccepted": len(candidates),
                          "excluded": dict(sorted(excluded.items())), "ageCutoffBasis": "max-publication-minus-180-days",
                          "missingOptionalColumns": sorted(set(names) - rc)}, "entries": selected}


def sample(entries, size, seed):
    def take(pool, count):
        buckets = collections.defaultdict(list)
        for entry in pool:
            key = (entry["category"], entry["sourceGroup"], entry["ageBand"], entry["complexity"])
            buckets[key].append(entry)
        for rows in buckets.values():
            rows.sort(key=lambda row: token(seed, "rank", row["id"]))
        keys = sorted(buckets, key=lambda key: token(seed, "bucket", repr(key)))
        chosen = []
        while len(chosen) < count and keys:
            for key in list(keys):
                chosen.append(buckets[key].pop())
                if not buckets[key]: keys.remove(key)
                if len(chosen) == count: break
        return chosen

    missing = [e for e in entries if not e["hasPoster"]]
    present = [e for e in entries if e["hasPoster"]]
    result = take(missing, round(size * .7)) + take(present, size - round(size * .7))
    used = {e["id"] for e in result}
    result += take([e for e in entries if e["id"] not in used], size - len(result))
    return sorted(result, key=lambda row: row["id"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", required=True)
    parser.add_argument("--size", type=int, default=1500)
    parser.add_argument("--seed", default="feedarr-parser-v2.1")
    parser.add_argument("--local-snapshot", action="store_true",
                        help="Independent consistent backup under local/, no sidecars; never use on a live database")
    args = parser.parse_args()
    try:
        with readonly_connection(args.db, local_snapshot=args.local_snapshot) as connection:
            output = export(connection, args.size, args.seed)
        print(json.dumps(output, sort_keys=True, ensure_ascii=False))
    except (OSError, ValueError, sqlite3.Error):
        # Errors must not disclose paths, SQL values, usernames or raw titles.
        print("Read-only export refused or unavailable; verify read-only permissions/schema/SQLite >=3.25.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
