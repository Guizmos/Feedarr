import copy
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import benchmark as bench
import export_sqlite as exporter


def entry(index=1, poster=False):
    return {"id": f"case-{index}", "raw": f"Public Film {index} 2020 WEB-DL",
            "category": "Film", "hasPoster": poster, "sourceGroup": "source-1",
            "ageBand": "older", "complexity": "simple"}


def corpus(entries=None):
    return {"schemaVersion": 1, "origin": "imported-local", "seed": "fixed",
            "entries": entries if entries is not None else [entry()]}


def output(index=1):
    values = {key: None for key in exporter.PARSED_FIELDS}
    values.update(TitleClean="Public Film", Year=2020, Source="WEB-DL", MediaType="movie")
    return {"id": f"case-{index}", "parsed": values}


class CorpusTests(unittest.TestCase):
    def test_allowlist_and_privacy_fail_closed(self):
        for text in ["https://private.example/a?api_key=abc", "private.local", "user@example.org",
                     "password=abc", "Movie 192.168.1.1", "Movie " + "abcdef12" * 4,
                     "/volume1/private/file", "Movie\x00text"]:
            data = corpus()
            data["entries"][0]["raw"] = text
            with self.subTest(text=text), self.assertRaises(ValueError):
                bench.validate_corpus(data)
        data = corpus()
        data["entries"][0]["download_url"] = "innocent-looking-value"
        with self.assertRaises(ValueError): bench.validate_corpus(data)
        data = corpus()
        data["selection"] = {"api_key": "innocent-looking-value"}
        with self.assertRaises(ValueError): bench.validate_corpus(data)

    def test_duplicates_unknown_categories_invalid_fields_rejected(self):
        with self.assertRaises(ValueError): bench.validate_corpus(corpus([entry(), entry()]))
        data = corpus()
        data["entries"][0]["category"] = "Movie"
        with self.assertRaises(ValueError): bench.validate_corpus(data)
        data = corpus()
        data["entries"][0]["stored"] = {"Year": True}
        with self.assertRaises(ValueError): bench.validate_corpus(data)

    def test_hash_ignores_input_order_not_input_content(self):
        data = corpus([entry(2), entry(1)])
        reverse = corpus(copy.deepcopy(list(reversed(data["entries"]))))
        self.assertEqual(bench.corpus_hash(data), bench.corpus_hash(reverse))
        reverse["entries"][0]["raw"] += " changed"
        self.assertNotEqual(bench.corpus_hash(data), bench.corpus_hash(reverse))

    def test_references_never_inferred_from_posters(self):
        data = corpus([entry(1, True), entry(2, False)])
        refs = bench.reference_template(data)
        result = bench.make_result(data, {"rows": [output(1), output(2)]}, refs, "a" * 64)
        self.assertEqual(0, result["metrics"]["parser"]["validatedRows"])
        self.assertIsNone(result["metrics"]["parser"]["accuracyOnAnnotatedFields"])
        self.assertEqual({"unreviewed": 2}, result["referenceCounts"])

    def test_only_validated_explicit_fields_are_scored(self):
        data = corpus([entry(1), entry(2), entry(3)])
        data["entries"][0]["association"] = {"tmdbId": 10, "tmdbType": "movie"}
        refs = bench.reference_template(data)
        refs["entries"][0].update(status="validated", reviewer="manual reviewer", evidence="explicit source review",
                                   parserExpected={"Year": 2020}, associationExpected={"tmdbId": 10})
        refs["entries"][1].update(status="ambiguous", parserExpected={"Year": 1999})
        result = bench.make_result(data, {"rows": [output(1), output(2), output(3)]}, refs, "a" * 64)
        self.assertEqual(1, result["metrics"]["parser"]["validatedRows"])
        self.assertEqual(1, result["metrics"]["parser"]["accuracyOnAnnotatedFields"])
        self.assertEqual({"Year"}, set(result["metrics"]["parser"]["fields"]))
        self.assertEqual(1, result["metrics"]["association"]["validatedRows"])

    def test_evidence_and_corpus_identity_required(self):
        data = corpus()
        refs = bench.reference_template(data)
        refs["entries"][0].update(status="validated", parserExpected={"Year": 2020})
        with self.assertRaises(ValueError): bench.validate_references(refs, data)
        refs["entries"][0].update(reviewer="reviewer", evidence="evidence")
        data["entries"][0]["raw"] += " modified"
        with self.assertRaises(ValueError): bench.validate_references(refs, data)

    def test_comparison_detects_regression_even_if_serialized_metrics_edited(self):
        data = corpus()
        refs = bench.reference_template(data)
        refs["entries"][0].update(status="validated", reviewer="reviewer", evidence="evidence", parserExpected={"Year": 2020})
        before = bench.make_result(data, {"rows": [output()]}, refs, "a" * 64)
        after = copy.deepcopy(before)
        after["rows"][0]["parsed"]["Year"] = None
        compared = bench.compare(before, after)
        self.assertEqual([{"id": "case-1", "domain": "parser", "field": "Year"}], compared["validatedRegressions"])
        after["corpusSha256"] = "b" * 64
        with self.assertRaises(ValueError): bench.compare(before, after)

    def test_unlabelled_change_is_not_claimed_as_regression(self):
        data = corpus()
        refs = bench.reference_template(data)
        before = bench.make_result(data, {"rows": [output()]}, refs, "a" * 64)
        changed = output()
        changed["parsed"]["Year"] = None
        after = bench.make_result(data, {"rows": [changed]}, refs, "b" * 64)
        comparison = bench.compare(before, after)
        self.assertEqual(1, len(comparison["changes"]))
        self.assertEqual([], comparison["validatedRegressions"])

    def test_results_and_markdown_are_deterministic_timing_separate(self):
        data = corpus()
        refs = bench.reference_template(data)
        a = bench.make_result(data, {"rows": [output()], "performance": {"median": 10}}, refs, "a" * 64)
        b = bench.make_result(data, {"rows": [output()], "performance": {"median": 999}}, refs, "a" * 64)
        self.assertEqual(bench.canonical(a), bench.canonical(b))
        self.assertEqual(bench.markdown(a), bench.markdown(b))

    def test_existing_174_fixtures_are_reused_without_provider_truth(self):
        data, refs = bench.fixtures(bench.ROOT / "src/Feedarr.Api.Tests/Golden/parser-lot5-p0.json")
        self.assertEqual(174, len(data["entries"]))
        self.assertTrue(all(row["hasPoster"] is None for row in data["entries"]))
        self.assertTrue(all(not row["associationExpected"] for row in refs["entries"]))

    def test_output_on_nas_is_refused(self):
        with self.assertRaises(ValueError): bench.local_output("/volume1/Docker/Feedarr/data/output.json")
        with self.assertRaises(ValueError): bench.local_output(bench.ROOT / "accidental-export.json")
        self.assertEqual(HERE / "local/export.json", bench.local_output(HERE / "local/export.json"))

    def test_check_exit_codes_and_integrity(self):
        data = corpus()
        refs = bench.reference_template(data)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.json"
            result = bench.make_result(data, {"rows": [output()]}, refs, "a" * 64)
            bench.write(path, result)
            def check():
                return subprocess.run([sys.executable, str(HERE / "benchmark.py"), "check", "--result", str(path)], capture_output=True).returncode
            self.assertEqual(2, check())
            refs["entries"][0].update(status="validated", reviewer="reviewer", evidence="evidence", parserExpected={"Year": 2020})
            result = bench.make_result(data, {"rows": [output()]}, refs, "a" * 64)
            bench.write(path, result)
            self.assertEqual(0, check())
            result["rows"][0]["parsed"]["Year"] = None
            bench.write(path, result)
            self.assertEqual(1, check())

    def test_real_corpus_reference_template_is_unreviewed_even_with_ids(self):
        data = corpus()
        data["origin"] = "production-export"
        data["entries"][0].update(hasPoster=True, association={"tmdbId": 123, "tmdbType": "movie"})
        self.assertEqual("unreviewed", bench.reference_template(data)["entries"][0]["status"])

    def test_unknown_provider_and_sensitive_provider_ids_are_rejected(self):
        for values in ({"provider": "private-tracker"}, {"providerId": "https://private.local/123"}):
            data = corpus()
            data["entries"][0]["association"] = values
            with self.assertRaises(ValueError): bench.validate_corpus(data)


class ExportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.db = self.root / "fixture.sqlite"
        connection = sqlite3.connect(self.db)
        connection.executescript("""
          CREATE TABLE sources(id INTEGER PRIMARY KEY,name TEXT,api_key TEXT,torznab_url TEXT);
          INSERT INTO sources VALUES(1,'PrivateTrackerName','super-secret','https://private.local/?token=secret');
          CREATE TABLE media_entities(id INTEGER PRIMARY KEY,poster_file TEXT,tmdb_id INTEGER,tmdb_media_type TEXT);
          INSERT INTO media_entities VALUES(1,'entity-poster.jpg',55,'tv');
          CREATE TABLE releases(id INTEGER PRIMARY KEY,source_id INTEGER,title TEXT,unified_category TEXT,
            poster_file TEXT,entity_id INTEGER,published_at_ts INTEGER,title_clean TEXT,year INTEGER,
            poster_last_error TEXT,poster_last_attempt_ts INTEGER,tmdb_id INTEGER,tmdb_media_type TEXT);
        """)
        categories = ["Film", "Serie", "Anime", "Emission", "Spectacle", "JeuWindows"]
        for i in range(1, 181):
            connection.execute("INSERT INTO releases VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)", (
                i, i % 3 + 1, f"Public Work {i} 2020 WEB-DL", categories[i % 6],
                None if i % 2 else "poster.jpg", None, 1_700_000_000 if i % 3 else 1_500_000_000,
                f"Public Work {i}", 2020, "HTTP 401 https://private.local/?token=super-secret" if i % 5 == 0 else None,
                1_700_000_000 if i % 5 == 0 else None, None, None))
        connection.execute("INSERT INTO releases VALUES(181,1,'PrivateTrackerName Film','Film',NULL,NULL,1,NULL,NULL,NULL,NULL,NULL,NULL)")
        connection.execute("INSERT INTO releases VALUES(182,1,'Film https://private.local/?token=secret','Film',NULL,NULL,1,NULL,NULL,NULL,NULL,NULL,NULL)")
        connection.execute("INSERT INTO releases VALUES(183,1,'Entity inherited','Film',NULL,1,1,NULL,NULL,NULL,NULL,10,NULL)")
        connection.execute("INSERT INTO releases VALUES(184,1,'Empty release shadows entity','Film','',1,1,NULL,NULL,NULL,NULL,NULL,NULL)")
        connection.commit()
        connection.close()
        self.db.chmod(0o444)
        self.root.chmod(0o555)

    def tearDown(self):
        self.root.chmod(0o755)
        self.db.chmod(0o644)
        self.temporary.cleanup()

    def test_refuse_writable_database_or_sidecars(self):
        self.root.chmod(0o755)
        with self.assertRaises(ValueError): exporter.readonly_connection(self.db)
        self.root.chmod(0o555)
        self.db.chmod(0o644)
        with self.assertRaises(ValueError): exporter.readonly_connection(self.db)

    def test_sqlite_and_authorizer_deny_writes_and_unsafe_pragmas(self):
        with exporter.readonly_connection(self.db) as connection:
            for sql in ["INSERT INTO sources VALUES(9,'a','b','c')", "PRAGMA journal_mode=WAL",
                        "CREATE TEMP TABLE forbidden(a)", "ATTACH DATABASE ':memory:' AS extra",
                        "SELECT api_key FROM sources", "SELECT torznab_url FROM sources"]:
                with self.subTest(sql=sql), self.assertRaises(sqlite3.Error): connection.execute(sql)

    def test_export_deterministic_stratified_bounded_private_and_readonly(self):
        before = self.db.read_bytes()
        files = sorted(path.name for path in self.root.iterdir())
        with exporter.readonly_connection(self.db) as connection:
            first = exporter.export(connection, 60, "fixed")
            second = exporter.export(connection, 60, "fixed")
            other = exporter.export(connection, 60, "different")
        self.assertEqual(first, second)
        self.assertNotEqual(first["entries"], other["entries"])
        self.assertEqual(60, len(first["entries"]))
        self.assertEqual(42, sum(not row["hasPoster"] for row in first["entries"]))
        self.assertEqual(set(exporter.CATEGORIES) & {"Film", "Serie", "Anime", "Emission", "Spectacle", "JeuWindows"},
                         {row["category"] for row in first["entries"]})
        self.assertEqual(3, len({row["sourceGroup"] for row in first["entries"]}))
        serialized = bench.canonical(first)
        for private in ["PrivateTrackerName", "super-secret", "private.local", "api_key", "torznab_url"]:
            self.assertNotIn(private, serialized)
        self.assertFalse(exporter.sensitive(first))
        self.assertEqual(first, bench.validate_corpus(first))
        self.assertEqual(before, self.db.read_bytes())
        self.assertEqual(files, sorted(path.name for path in self.root.iterdir()))

    def test_entity_poster_fallback_matches_repository_and_keeps_ambiguous_identity(self):
        with exporter.readonly_connection(self.db) as connection:
            data = exporter.export(connection, 2000, "fixed")
        by_title = {row["raw"]: row for row in data["entries"]}
        self.assertTrue(by_title["Entity inherited"]["hasPoster"])
        self.assertFalse(by_title["Empty release shadows entity"]["hasPoster"])
        association = by_title["Entity inherited"]["association"]
        self.assertEqual(10, association["tmdbId"])
        self.assertIsNone(association["tmdbType"])  # Do not pair release ID 10 with entity 55's type.

    def test_cli_error_does_not_disclose_path_or_credentials(self):
        result = subprocess.run([sys.executable, str(HERE / "export_sqlite.py"), "--db", str(self.root / "private-credential.sqlite")], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertEqual("", result.stdout)
        self.assertNotIn(str(self.root), result.stderr)

    def test_live_wal_is_read_without_checkpoint_or_sidecar_write(self):
        self.root.chmod(0o755)
        self.db.chmod(0o644)
        writer = sqlite3.connect(self.db)
        writer.execute("PRAGMA journal_mode=WAL")
        writer.execute("INSERT INTO releases(id,source_id,title,unified_category) VALUES(999,1,'Committed WAL entry','Film')")
        writer.commit()
        files = [self.db, Path(str(self.db) + "-wal"), Path(str(self.db) + "-shm")]
        before = {path.name: path.read_bytes() for path in files}
        try:
            for path in files: path.chmod(0o444)
            self.root.chmod(0o555)
            # A fresh process matches the standalone exporter. SQLite can otherwise reuse
            # this test process's writable SHM mapping created by the writer above.
            result = subprocess.run([sys.executable, str(HERE / "export_sqlite.py"), "--db", str(self.db), "--size", "2000"],
                                    capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("Committed WAL entry", {row["raw"] for row in json.loads(result.stdout)["entries"]})
            self.assertEqual(before, {path.name: path.read_bytes() for path in files})
        finally:
            self.root.chmod(0o755)
            for path in files:
                if path.exists(): path.chmod(0o644)
            writer.close()


if __name__ == "__main__":
    unittest.main()
