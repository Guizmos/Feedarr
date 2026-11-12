#!/usr/bin/env python3
"""Offline corpus import, actual-parser execution, labelled evaluation and comparison."""
import argparse
import collections
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

from export_sqlite import CATEGORIES, PARSED_FIELDS, PROVIDERS, sensitive

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
ASSOCIATION_FIELDS = {"tmdbId", "tmdbType", "tvdbId", "provider", "providerId", "manualMatch"}
ENTRY_KEYS = {"id", "raw", "category", "sourceGroup", "hasPoster", "ageBand", "complexity",
              "stored", "association", "matchStatus", "errorClass", "manualTitle", "needsRebind"}


def sensitive_document(value):
    # Known content hashes are not credentials. All other text is scanned normally.
    if isinstance(value, dict):
        return any(sensitive(key) or (not (key in {"corpusSha256", "referencesSha256", "parserSourceSha256"}
                       and isinstance(item, str) and re.fullmatch(r"[a-f0-9]{64}", item))
                       and sensitive_document(item)) for key, item in value.items())
    if isinstance(value, list):
        return any(sensitive_document(item) for item in value)
    return sensitive(value)


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def load(path):
    if Path(path).stat().st_size > 30_000_000:
        raise ValueError("input_too_large")
    return json.loads(Path(path).read_text())


def local_output(path):
    path = Path(path).resolve()
    if re.match(r"^/volume\d+(?:/|$)", str(path)):
        raise ValueError("NAS_output_forbidden")
    if (path == ROOT or ROOT in path.parents) and not (path == HERE / "local" or HERE / "local" in path.parents):
        raise ValueError("generated_corpora_must_stay_in_ignored_local_directory")
    return path


def write_text(path, text):
    path = local_output(path)
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as file:
        os.chmod(file.name, 0o600)
        file.write(text)
    os.replace(file.name, path)


def write(path, value):
    write_text(path, json.dumps(value, sort_keys=True, ensure_ascii=False, indent=2) + "\n")


def validate_fields(fields, association=False):
    allowed = ASSOCIATION_FIELDS if association else set(PARSED_FIELDS)
    if not isinstance(fields, dict) or not set(fields) <= allowed:
        raise ValueError("unexpected_fields")
    numeric = {"Year", "Season", "Episode", "tmdbId", "tvdbId"}
    for key, value in fields.items():
        if value is None:
            continue
        if key in numeric:
            if type(value) is not int or not 0 <= value <= 2**31 - 1:
                raise ValueError("invalid_number")
        elif key == "manualMatch":
            if type(value) is not bool:
                raise ValueError("invalid_boolean")
        elif not isinstance(value, str) or len(value) > 1000:
            raise ValueError("invalid_text")
        if key == "tmdbType" and value not in ("movie", "tv", "series"):
            raise ValueError("invalid_tmdb_type")
        if key == "provider" and value not in PROVIDERS:
            raise ValueError("invalid_provider")
        if key == "providerId" and not re.fullmatch(r"[0-9]{1,12}", value):
            raise ValueError("invalid_provider_id")
    if sensitive(fields):
        raise ValueError("sensitive_input")


def validate_corpus(corpus):
    if not isinstance(corpus, dict) or corpus.get("schemaVersion") != 1 or set(corpus) - {
            "schemaVersion", "origin", "seed", "selection", "entries"}:
        raise ValueError("invalid_corpus")
    if corpus.get("origin") not in {"production-export", "regression-fixtures", "imported-local"}:
        raise ValueError("invalid_origin")
    if not isinstance(corpus.get("seed"), str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", corpus["seed"]):
        raise ValueError("invalid_seed")
    selection = corpus.get("selection", {})
    if not isinstance(selection, dict) or set(selection) - {"requested", "missingPosterTarget", "candidateLimit",
            "candidatesAccepted", "excluded", "ageCutoffBasis", "missingOptionalColumns"}:
        raise ValueError("invalid_selection_metadata")
    for key in ("requested", "candidateLimit", "candidatesAccepted"):
        if key in selection and (type(selection[key]) is not int or selection[key] < 0):
            raise ValueError("invalid_selection_count")
    if "missingPosterTarget" in selection and selection["missingPosterTarget"] != .7:
        raise ValueError("invalid_selection_target")
    if "ageCutoffBasis" in selection and selection["ageCutoffBasis"] != "max-publication-minus-180-days":
        raise ValueError("invalid_age_basis")
    excluded = selection.get("excluded", {})
    if not isinstance(excluded, dict) or set(excluded) - {"source_name", "sensitive_or_invalid"} or any(
            type(value) is not int or value < 0 for value in excluded.values()):
        raise ValueError("invalid_exclusion_metadata")
    optional_columns = {"title_clean", "year", "season", "episode", "resolution", "source", "codec", "release_group",
                        "media_type", "tmdb_id", "tmdb_media_type", "tvdb_id", "ext_provider", "ext_provider_id",
                        "poster_provider", "poster_provider_id", "poster_last_attempt_ts", "title_manual_override", "needs_rebind"}
    if not isinstance(selection.get("missingOptionalColumns", []), list) or not set(selection.get("missingOptionalColumns", [])) <= optional_columns:
        raise ValueError("invalid_optional_columns")
    entries = corpus.get("entries")
    if not isinstance(entries, list) or not 1 <= len(entries) <= 10000 or sensitive(corpus):
        raise ValueError("empty_oversized_or_sensitive_corpus")
    ids = set()
    for row in entries:
        if not isinstance(row, dict) or set(row) - ENTRY_KEYS:
            raise ValueError("unexpected_entry_fields")
        if not isinstance(row.get("id"), str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", row["id"]) or row["id"] in ids:
            raise ValueError("duplicate_or_invalid_id")
        ids.add(row["id"])
        if not isinstance(row.get("raw"), str) or not 1 <= len(row["raw"]) <= 1000 or row.get("category") not in CATEGORIES:
            raise ValueError("invalid_parser_input")
        if row.get("hasPoster") is not None and type(row["hasPoster"]) is not bool:
            raise ValueError("invalid_poster_state")
        for key in ("manualTitle", "needsRebind"):
            if key in row and type(row[key]) is not bool:
                raise ValueError("invalid_flag")
        if row.get("stored") is not None:
            validate_fields(row["stored"])
        if row.get("association") is not None:
            validate_fields(row["association"], association=True)
        for key in ("sourceGroup", "ageBand", "complexity", "matchStatus", "errorClass"):
            if key in row and (not isinstance(row[key], str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", row[key])):
                raise ValueError("invalid_label")
        for key, allowed in (("ageBand", {"recent", "older", "unknown"}),
                             ("complexity", {"simple", "complex", "fixture", "unknown"}),
                             ("matchStatus", {"associated", "attempted", "never_attempted", "unknown"}),
                             ("errorClass", {"none", "no_match", "rate_limit", "authorization", "timeout", "missing_title", "other", "unknown"})):
            if key in row and row[key] not in allowed:
                raise ValueError("invalid_label_value")
    # Order and formatting do not change the identity of a corpus.
    return {**corpus, "entries": sorted(entries, key=lambda row: row["id"])}


def corpus_hash(corpus):
    return digest(validate_corpus(corpus))


def reference_template(corpus):
    return {"schemaVersion": 1, "corpusSha256": corpus_hash(corpus), "entries": [
        {"id": row["id"], "status": "unreviewed", "reviewer": "", "evidence": "",
         "parserExpected": {}, "associationExpected": {}} for row in corpus["entries"]]}


def validate_references(references, corpus):
    if references.get("schemaVersion") != 1 or references.get("corpusSha256") != corpus_hash(corpus):
        raise ValueError("references_for_different_corpus")
    if set(references) - {"schemaVersion", "corpusSha256", "entries"} or sensitive_document(references):
        raise ValueError("invalid_references")
    ids = {row["id"] for row in corpus["entries"]}
    seen = set()
    for reference in references.get("entries", []):
        if set(reference) - {"id", "status", "reviewer", "evidence", "parserExpected", "associationExpected"}:
            raise ValueError("invalid_reference_fields")
        if reference.get("id") not in ids or reference["id"] in seen:
            raise ValueError("unknown_or_duplicate_reference")
        seen.add(reference["id"])
        if reference.get("status") not in {"validated", "ambiguous", "unreviewed"}:
            raise ValueError("invalid_reference_status")
        for key in ("reviewer", "evidence"):
            if not isinstance(reference.get(key), str) or len(reference[key]) > 1000:
                raise ValueError("invalid_reference_evidence")
        if reference["status"] == "validated" and (not reference["reviewer"].strip() or not reference["evidence"].strip()):
            raise ValueError("validation_requires_reviewer_and_evidence")
        for key, association in (("parserExpected", False), ("associationExpected", True)):
            validate_fields(reference.get(key, {}), association)
        if reference["status"] == "validated" and not (reference.get("parserExpected") or reference.get("associationExpected")):
            raise ValueError("validated_reference_has_no_expectation")
    return {**references, "entries": sorted(references["entries"], key=lambda row: row["id"])}


def fixtures(golden):
    data = load(golden)
    corpus = {"schemaVersion": 1, "origin": "regression-fixtures", "seed": "lot5-p0-174",
              "entries": [{"id": f"fixture-{i:04d}", "raw": row["Raw"], "category": row["Category"],
                           "hasPoster": None, "sourceGroup": "fixture", "ageBand": "unknown", "complexity": "fixture"}
                          for i, row in enumerate(data)]}
    corpus = validate_corpus(corpus)
    references = reference_template(corpus)
    for reference, row in zip(references["entries"], data):
        reference.update(status="validated", reviewer="existing-reviewed-regression-fixtures",
                         evidence="Parser P0 approved expectations; not validation of provider associations",
                         parserExpected=row["Expected"])
    return corpus, validate_references(references, corpus)


def parser_source_hash(source_root=ROOT):
    paths = list((source_root / "src/Feedarr.Api/Services/Title").glob("*.cs")) + [
        source_root / "src/Feedarr.Api/Models/UnifiedCategory.cs",
        source_root / "src/Feedarr.Api/Services/Categories/UnifiedCategoryMappings.cs",
        source_root / "src/Feedarr.Api/Services/Categories/CategoryGroupCatalog.cs"]
    return digest({str(path.relative_to(source_root)): hashlib.sha256(path.read_bytes()).hexdigest()
                   for path in sorted(paths)})


def run_parser(corpus_path, iterations, warmup, source_root=ROOT):
    source_root = Path(source_root).resolve(strict=True)
    if ';' in str(source_root) or '%' in str(source_root):
        raise ValueError("unsupported_source_path")
    project = HERE / "Runner/Runner.csproj"
    source_hash = parser_source_hash(source_root)
    # Each invocation owns its build outputs: two source revisions cannot race on a DLL.
    with tempfile.TemporaryDirectory(prefix="feedarr-parser-runner-") as directory:
        build = Path(directory)
        properties = [f"-p:BaseIntermediateOutputPath={build / 'obj'}/",
                      f"-p:MSBuildProjectExtensionsPath={build / 'obj'}/",
                      f"-p:FeedarrSourceRoot={source_root / 'src/Feedarr.Api'}"]
        commands = [["dotnet", "restore", str(project), "--configfile", str(project.parent / "NuGet.Config"), "-p:NuGetAudit=false", *properties],
                    ["dotnet", "build", str(project), "-c", "Release", "--no-restore", "--output", str(build / "bin"), *properties]]
        for command in commands:
            result = subprocess.run(command, capture_output=True, timeout=180)
            if result.returncode:
                raise ValueError("offline_runner_build_failed")
        result = subprocess.run(["dotnet", str(build / "bin/Runner.dll"),
                                 str(Path(corpus_path).resolve()), str(iterations), str(warmup)],
                                capture_output=True, timeout=300)
    if result.returncode or source_hash != parser_source_hash(source_root):
        raise ValueError("runner_failed_or_sources_changed")
    return json.loads(result.stdout), source_hash


def evaluate(corpus, rows, references):
    refs = {row["id"]: row for row in references["entries"]}
    inputs = {row["id"]: row for row in corpus["entries"]}
    metrics = {"parser": {"validatedRows": 0, "exactOnAnnotatedFields": 0, "fields": {}},
               "association": {"validatedRows": 0, "exactOnAnnotatedFields": 0, "fields": {}}}
    for row in rows:
        reference = refs.get(row["id"], {"status": "unreviewed"})
        row["referenceStatus"] = reference["status"]
        row["differences"] = {"parser": {}, "association": {}}
        if reference["status"] != "validated":
            continue
        for domain, key, actual in (("parser", "parserExpected", row["parsed"]),
                                     ("association", "associationExpected", inputs[row["id"]].get("association") or {})):
            expected = reference.get(key, {})
            if not expected:
                continue
            stats = metrics[domain]
            stats["validatedRows"] += 1
            for field, value in expected.items():
                counter = stats["fields"].setdefault(field, {"compared": 0, "correct": 0})
                counter["compared"] += 1
                if field in actual and actual[field] == value:
                    counter["correct"] += 1
                else:
                    row["differences"][domain][field] = {"expected": value, "actual": actual.get(field), "actualPresent": field in actual}
            if not row["differences"][domain]:
                stats["exactOnAnnotatedFields"] += 1
    for stats in metrics.values():
        stats["accuracyOnAnnotatedFields"] = (stats["exactOnAnnotatedFields"] / stats["validatedRows"]
                                              if stats["validatedRows"] else None)
    return metrics


def descriptive(corpus, rows):
    inputs = {row["id"]: row for row in corpus["entries"]}
    category = collections.Counter()
    relations = collections.Counter()
    examples = collections.defaultdict(list)
    for row in rows:
        item = inputs[row["id"]]
        poster = "unknown" if item.get("hasPoster") is None else "present" if item["hasPoster"] else "missing"
        category[(item["category"], poster)] += 1
        flags = []
        if row["parsed"].get("Year") is None:
            flags.append("year_unspecified_not_necessarily_wrong")
        if re.search(r"\b(?:update|repack|1080p|x264|DTS)\b", row["parsed"]["TitleClean"], re.I):
            flags.append("technical_word_remaining_requires_review")
        if item.get("stored") and any(row["parsed"].get(k) != v for k, v in item["stored"].items()):
            flags.append("stored_output_differs_not_automatically_incorrect")
            flags.extend("stored_field_drift_" + key for key, value in item["stored"].items() if row["parsed"].get(key) != value)
        associated = any((item.get("association") or {}).get(k) is not None for k in ("tmdbId", "tvdbId", "providerId"))
        if associated and poster == "missing":
            flags.append("associated_without_poster")
        association = item.get("association") or {}
        if association.get("tmdbId") is not None and association.get("tmdbType") is None:
            flags.append("historical_tmdb_type_unspecified")
        if poster == "present" and not associated:
            flags.append("poster_without_exported_identity")
        for flag in flags:
            relations[(flag, item["category"], poster, item.get("matchStatus", "unknown"), associated)] += 1
            if len(examples[flag]) < 3:
                examples[flag].append({"id": row["id"], "raw": item["raw"], "parsed": row["parsed"]})
    return {"categoryPoster": [{"category": cat, "poster": poster, "count": count}
                               for (cat, poster), count in sorted(category.items())],
            "sourceGroups": len({row.get("sourceGroup", "unknown") for row in corpus["entries"]}),
            "ageBands": dict(sorted(collections.Counter(row.get("ageBand", "unknown") for row in corpus["entries"]).items())),
            "errorClasses": dict(sorted(collections.Counter(row.get("errorClass", "unknown") for row in corpus["entries"]).items())),
            "signals": [{"signal": key[0], "category": key[1], "poster": key[2], "matchStatus": key[3],
                         "associated": key[4], "count": count} for key, count in sorted(relations.items())],
            "examples": dict(sorted(examples.items()))}


def make_result(corpus, runner, references, source_hash):
    corpus = validate_corpus(corpus)
    references = validate_references(references, corpus)
    rows = [{"id": row["id"], "parsed": row["parsed"]} for row in runner["rows"]]
    if len(rows) != len(corpus["entries"]) or {row["id"] for row in rows} != {row["id"] for row in corpus["entries"]}:
        raise ValueError("runner_corpus_mismatch")
    for row in rows:
        validate_fields(row["parsed"])
        if set(row["parsed"]) != set(PARSED_FIELDS):
            raise ValueError("incomplete_parser_output")
    rows.sort(key=lambda row: row["id"])
    return {"schemaVersion": 1, "origin": corpus["origin"], "corpusSha256": corpus_hash(corpus),
            "referencesSha256": digest(references), "parserSourceSha256": source_hash,
            "count": len(rows), "metrics": evaluate(corpus, rows, references),
            "referenceCounts": dict(sorted(collections.Counter(row["referenceStatus"] for row in rows).items())),
            "descriptive": descriptive(corpus, rows), "rows": rows, "corpus": corpus, "references": references}


def verify_result(result):
    if sensitive_document(result) or not re.fullmatch(r"[a-f0-9]{64}", result.get("parserSourceSha256", "")):
        raise ValueError("invalid_result")
    checked = make_result(result["corpus"], {"rows": result["rows"]}, result["references"], result["parserSourceSha256"])
    for key in ("schemaVersion", "corpusSha256", "referencesSha256", "count"):
        if result.get(key) != checked[key]:
            raise ValueError("result_integrity_mismatch")
    # Recompute annotation scores/differences; never trust an edited score in a file.
    return checked


def markdown(result):
    metrics = result["metrics"]
    lines = ["# Feedarr Parser V2.1 — benchmark", "", f"Origin: `{result['origin']}`; entries: {result['count']}.",
             f"Corpus SHA-256: `{result['corpusSha256']}`.", f"Parser sources SHA-256: `{result['parserSourceSha256']}`.", "",
             "Timing is non-deterministic and is recorded separately in performance.json.",
             "No live provider requests. Poster presence is not ground truth. Signals below require review.", "",
             "| Domain | Validated rows | Exact on annotated fields | Accuracy |", "| --- | ---: | ---: | ---: |"]
    for domain, stats in metrics.items():
        score = "not measured" if stats["accuracyOnAnnotatedFields"] is None else f"{100*stats['accuracyOnAnnotatedFields']:.2f}%"
        lines.append(f"| {domain} | {stats['validatedRows']} | {stats['exactOnAnnotatedFields']} | {score} |")
    lines += ["", "Partial annotations are scored only on annotated fields; unreviewed and ambiguous rows are excluded.", "",
              "Reference statuses: " + canonical(result["referenceCounts"]), "",
              "## Category / poster reference", "", "| Category | Poster | Count |", "| --- | --- | ---: |"]
    for row in result["descriptive"]["categoryPoster"]:
        lines.append(f"| {row['category']} | {row['poster']} | {row['count']} |")
    if result["origin"] == "regression-fixtures":
        lines += ["", "Fixture scores check existing approved parser expectations only. They do not measure real-world precision or correct provider matches."]
    lines += ["", "## Descriptive signals, not confirmed parser errors", ""]
    counts = collections.Counter()
    for row in result["descriptive"]["signals"]:
        counts[row["signal"]] += row["count"]
    for key, count in sorted(counts.items(), key=lambda item: (-item[1], item[0])):
        lines.append(f"- {key}: {count}")
    for key, examples in result["descriptive"]["examples"].items():
        lines += ["", f"### {key}", ""]
        for row in examples:
            # Escape Markdown structural characters from release titles.
            raw = row["raw"].replace("`", "'").replace("\n", " ").replace("<", "&lt;").replace(">", "&gt;")
            title = row["parsed"]["TitleClean"].replace("`", "'").replace("\n", " ")
            lines.append(f"- {row['id']}: `{raw}` → `{title}`; Year={row['parsed']['Year']}.")
    return "\n".join(lines) + "\n"


def compare(before, after):
    before, after = verify_result(before), verify_result(after)
    for key in ("schemaVersion", "corpusSha256", "referencesSha256", "count"):
        if before.get(key) != after.get(key):
            raise ValueError("comparison_requires_identical_corpus_and_references")
    old = {row["id"]: row for row in before["rows"]}
    new = {row["id"]: row for row in after["rows"]}
    if set(old) != set(new) or len(old) != before["count"] or len(new) != after["count"]:
        raise ValueError("comparison_ids_mismatch")
    changes, regressions, improvements = [], [], []
    for key in sorted(old):
        fields = {field: {"before": old[key]["parsed"].get(field), "after": new[key]["parsed"].get(field)}
                  for field in PARSED_FIELDS if old[key]["parsed"].get(field) != new[key]["parsed"].get(field)}
        if fields:
            changes.append({"id": key, "fields": fields})
        for domain in ("parser", "association"):
            was = old[key].get("differences", {}).get(domain, {})
            now = new[key].get("differences", {}).get(domain, {})
            if old[key].get("referenceStatus") == new[key].get("referenceStatus") == "validated":
                for field in sorted(set(now) - set(was)):
                    regressions.append({"id": key, "domain": domain, "field": field})
                for field in sorted(set(was) - set(now)):
                    improvements.append({"id": key, "domain": domain, "field": field})
    return {"schemaVersion": 1, "corpusSha256": before["corpusSha256"], "changes": changes,
            "validatedRegressions": regressions, "validatedImprovements": improvements}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    command = sub.add_parser("import", help="Strict allowlist and privacy scan; keep local, manually review before sharing")
    command.add_argument("--input", required=True)
    command.add_argument("--output", required=True)
    command = sub.add_parser("fixtures", help="Reuse existing reviewed parser fixtures; no provider ground truth")
    command.add_argument("--golden", default=str(ROOT / "src/Feedarr.Api.Tests/Golden/parser-lot5-p0.json"))
    command.add_argument("--out", required=True)
    command = sub.add_parser("references", help="Create unreviewed annotation template, never infer correctness from posters")
    command.add_argument("--corpus", required=True)
    command.add_argument("--output", required=True)
    command = sub.add_parser("run")
    command.add_argument("--corpus", required=True)
    command.add_argument("--references")
    command.add_argument("--out", required=True)
    command.add_argument("--iterations", type=int, default=5, choices=range(1, 101))
    command.add_argument("--warmup", type=int, default=2, choices=range(1, 21))
    command.add_argument("--source-root", default=str(ROOT), help="Another checkout with the same parser API; no checkout or Git writes performed")
    command = sub.add_parser("compare")
    command.add_argument("--before", required=True)
    command.add_argument("--after", required=True)
    command.add_argument("--out", required=True)
    command.add_argument("--fail-on-change", action="store_true")
    command = sub.add_parser("check", help="Fail on differences against validated references only")
    command.add_argument("--result", required=True)
    args = parser.parse_args()
    try:
        if args.command == "import":
            write(args.output, validate_corpus(load(args.input)))
        elif args.command == "fixtures":
            corpus, refs = fixtures(args.golden)
            write(local_output(args.out) / "corpus.json", corpus)
            write(local_output(args.out) / "references.json", refs)
        elif args.command == "references":
            write(args.output, reference_template(validate_corpus(load(args.corpus))))
        elif args.command == "run":
            corpus = validate_corpus(load(args.corpus))
            refs = validate_references(load(args.references), corpus) if args.references else reference_template(corpus)
            # Run a canonical, validated local copy, never the untrusted raw input.
            out = local_output(args.out)
            write(out / "input.json", corpus)
            runner, source_hash = run_parser(out / "input.json", args.iterations, args.warmup, args.source_root)
            result = make_result(corpus, runner, refs, source_hash)
            write(out / "results.json", result)
            write_text(out / "report.md", markdown(result))
            write(out / "performance.json", {"corpusSha256": result["corpusSha256"],
                "parserSourceSha256": source_hash, **runner["performance"], "rows": [
                {"id": row["id"], "medianMicroseconds": row["medianMicroseconds"]} for row in runner["rows"]]})
        elif args.command == "compare":
            before, after = load(args.before), load(args.after)
            if sensitive_document(before) or sensitive_document(after):
                raise ValueError("sensitive_results")
            result = compare(before, after)
            write(local_output(args.out) / "comparison.json", result)
            write_text(local_output(args.out) / "comparison.md", "# Parser comparison\n\n" +
                       f"Changed entries: {len(result['changes'])}\n\nValidated field regressions: {len(result['validatedRegressions'])}\n\n" +
                       f"Validated field improvements: {len(result['validatedImprovements'])}\n\nSee comparison.json for structured field differences.\n")
            return int(bool(result["validatedRegressions"] or (args.fail_on_change and result["changes"])))
        elif args.command == "check":
            result = verify_result(load(args.result))
            if not any(stats["validatedRows"] for stats in result["metrics"].values()):
                raise ValueError("no_validated_references_to_check")
            return int(any(stats["exactOnAnnotatedFields"] != stats["validatedRows"] for stats in result["metrics"].values()))
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        print("Benchmark refused: validate schema, privacy, reference/corpus hashes and offline .NET SDK. No input values logged.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
