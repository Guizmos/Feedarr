#!/usr/bin/env python3
"""Single CLI for existing Feedarr Shadow/Replay APIs. Stdlib only; no credential discovery from SQLite."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
PRIVATE = ROOT / "tools/parser-benchmark/local"
ENDPOINT = "/api/diagnostics/matching-shadow/replay"
TABLES = ("releases", "media_entities", "poster_matches", "poster_store_refs", "release_arr_status", "media_entity_arr_status", "stats", "poster_stats")
MAX_BYTES = 10 * 1024 * 1024


class Failure(Exception):
    pass


class Parser(argparse.ArgumentParser):
    def error(self, _message):
        raise Failure("invalid_arguments")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *_args, **_kwargs):
        return None


def load_config(path=None):
    path = path or os.environ.get("FEEDARR_API_CONFIG")
    if not path:
        return {}
    try:
        value = json.loads(Path(path).read_text())
        if not isinstance(value, dict) or set(value) - {"baseUrl", "usernameEnv", "passwordEnv", "cookieEnv", "csrfEnv", "database", "posterDir"}:
            raise ValueError()
        return value
    except Exception:
        raise Failure("local_configuration_invalid") from None


def env_value(config, key, default):
    name = config.get(key, default)
    if not isinstance(name, str) or not name or len(name) > 128 or any(c not in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_" for c in name):
        raise Failure("credential_environment_reference_invalid")
    return os.environ.get(name)


class Api:
    def __init__(self, config, base_url=None, timeout=10):
        self.base = (base_url or os.environ.get("FEEDARR_BASE_URL") or os.environ.get("FEEDARR_BETA_URL") or config.get("baseUrl") or "http://localhost:5003").rstrip("/")
        parsed = urllib.parse.urlsplit(self.base)
        if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise Failure("api_base_url_invalid")
        if parsed.scheme == "http" and parsed.hostname not in ("localhost", "127.0.0.1", "::1"):
            raise Failure("https_required_for_remote_api")
        self.origin = urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, "", "", ""))
        self.timeout = timeout
        self.headers = {"Accept": "application/json", "Origin": self.origin}
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        username = env_value(config, "usernameEnv", "FEEDARR_USERNAME")
        password = env_value(config, "passwordEnv", "FEEDARR_PASSWORD")
        cookie = env_value(config, "cookieEnv", "FEEDARR_SESSION_COOKIE")
        csrf = env_value(config, "csrfEnv", "FEEDARR_CSRF_TOKEN")
        if any(v and ("\r" in v or "\n" in v) for v in (username, password, cookie, csrf)):
            raise Failure("credential_format_invalid")
        if cookie and (username or password):
            raise Failure("choose_one_authentication_method")
        if bool(username) != bool(password):
            raise Failure("basic_authentication_incomplete")
        if username:
            self.headers["Authorization"] = "Basic " + base64.b64encode(f"{username}:{password}".encode()).decode()
        if cookie:
            self.headers.update({"Cookie": cookie, "X-Feedarr-Web": "1"})
            if not csrf:
                state = self.request("GET", "/api/auth/session")
                csrf = state.get("csrfToken") if state.get("authenticated") is True else None
            if not isinstance(csrf, str) or not csrf or len(csrf) > 512 or "\n" in csrf or "\r" in csrf:
                raise Failure("authenticated_session_csrf_required")
            self.headers["X-Feedarr-CSRF"] = csrf

    def request(self, method, path, body=None, timeout=None):
        headers = dict(self.headers)
        if method != "GET":
            headers.update({"Content-Type": "application/json", "X-Feedarr-Request": "1"})
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.base + path, data=data, headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=timeout or self.timeout) as response:
                if "json" not in response.headers.get("Content-Type", ""):
                    raise Failure("api_response_not_json")
                raw = response.read(MAX_BYTES + 1)
                if len(raw) > MAX_BYTES:
                    raise Failure("api_response_too_large")
                result = json.loads(raw)
                if not isinstance(result, dict):
                    raise Failure("api_response_invalid")
                return result
        except urllib.error.HTTPError as error:
            # Never emit error bodies, URLs, authentication values or exception messages.
            raise Failure(f"api_http_{error.code}") from None
        except Failure:
            raise
        except Exception:
            raise Failure("api_unavailable_or_invalid_response") from None


def business_snapshot(database, posters=None):
    path = Path(database).expanduser().resolve(strict=True)
    result = {}
    connection = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=2)
    try:
        connection.execute("PRAGMA query_only=ON")
        connection.execute("BEGIN")
        tables = {r[0] for r in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if not {"releases", "media_entities"} <= tables:
            raise Failure("business_database_schema_invalid")
        for table in TABLES:
            if table not in tables:
                continue
            columns = connection.execute(f'PRAGMA table_info("{table}")').fetchall()
            primary = [r[1] for r in sorted(columns, key=lambda r: r[5]) if r[5] > 0]
            order = ",".join('"' + c.replace('"', '""') + '"' for c in primary) or "rowid"
            digest = hashlib.sha256()
            count = 0
            for row in connection.execute(f'SELECT * FROM "{table}" ORDER BY {order}'):
                digest.update(json.dumps(row, ensure_ascii=True, separators=(",", ":"), default=lambda v: {"blob": v.hex()}).encode() + b"\n")
                count += 1
            result[table] = (count, digest.hexdigest())
        result["schema_version"] = connection.execute("PRAGMA schema_version").fetchone()[0]
    finally:
        connection.close()
    if posters:
        directory = Path(posters).expanduser().resolve(strict=True)
        digest = hashlib.sha256()
        count = 0
        for root, directories, files in os.walk(directory, followlinks=False):
            directories.sort()
            for name in sorted(files + [d for d in directories if (Path(root) / d).is_symlink()]):
                file = Path(root) / name
                digest.update(str(file.relative_to(directory)).encode())
                if file.is_symlink():
                    digest.update(os.readlink(file).encode())
                else:
                    with file.open("rb") as stream:
                        while chunk := stream.read(1024 * 1024):
                            digest.update(chunk)
                count += 1
        result["poster_files"] = (count, digest.hexdigest())
    # Digests remain in memory. Never export rows, URLs, secrets or database contents.
    return result


def save_report(report, directory):
    directory = Path(directory).expanduser().resolve()
    if directory.is_relative_to(ROOT):
        ignored = subprocess.run(["git", "check-ignore", "--quiet", "--", str(directory / "report.json")], cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if ignored.returncode:
            raise Failure("report_directory_must_be_ignored_by_git")
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    path = directory / f"replay-{uuid.uuid4().hex}.json"
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "w") as stream:
        json.dump(report, stream, ensure_ascii=True)
    return path


def compare_reports(before, after):
    module = (ROOT / "src/Feedarr.Web/feedarr-web/src/pages/settings/matchingReplayComparison.js").as_uri()
    code = """import fs from 'node:fs';
import {parseReplayReport,compareReplayReports,replayReportMaxBytes} from MODULE;
try { const reports=process.argv.slice(1).map(p=>{if(fs.statSync(p).size>replayReportMaxBytes)throw Error();return parseReplayReport(fs.readFileSync(p,'utf8'));});
console.log(JSON.stringify(compareReplayReports(...reports))); } catch { process.exitCode=2; }
""".replace("MODULE", json.dumps(module))
    try:
        result = subprocess.run(["node", "--input-type=module", "-e", code, str(before), str(after)], capture_output=True, text=True, timeout=20)
        if result.returncode:
            raise Failure("comparison_inputs_invalid")
        return json.loads(result.stdout)
    except Failure:
        raise
    except Exception:
        raise Failure("comparison_requires_node_and_valid_reports") from None


def execute(api, request, timeout, poll):
    deadline = time.monotonic() + timeout

    def call(method, path, body=None):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise Failure("replay_timeout")
        return api.request(method, path, body, timeout=min(api.timeout, remaining))

    state = call("GET", ENDPOINT)
    if state.get("running"):
        raise Failure("another_replay_is_active")
    request = {**request, "generation": state["generation"]}
    preview = call("POST", ENDPOINT + "/preview", request)
    request.update(generation=preview["generation"], expectedIds=preview["selectedIds"])
    state = call("POST", ENDPOINT, request)
    session_id = str(uuid.UUID(state["sessionId"]))
    try:
        while state.get("running"):
            if time.monotonic() >= deadline:
                raise Failure("replay_timeout")
            time.sleep(min(poll, max(0, deadline - time.monotonic())))
            state = call("GET", ENDPOINT)
            if state.get("sessionId") != session_id:
                raise Failure("replay_session_replaced")
        if state.get("status") != "completed":
            raise Failure("replay_did_not_complete")
        exported = call("GET", ENDPOINT + "/export")
        if exported.get("sessionId") != session_id:
            raise Failure("replay_session_replaced")
        return exported
    except BaseException:
        # Atomic ownership/generation guard; never stop a replacement session. No blind POST retries.
        try:
            api.request("POST", ENDPOINT + "/stop", {"sessionId": session_id, "generation": state["generation"]}, timeout=2)
        except Exception:
            pass
        raise


def main(argv=None):
    parser = Parser(description=__doc__)
    parser.add_argument("command", choices=["probe", "diagnostics", "run", "compare"])
    parser.add_argument("--config")
    parser.add_argument("--base-url")
    parser.add_argument("--database")
    parser.add_argument("--poster-dir")
    parser.add_argument("--api-only", action="store_true", help="Explicitly mark business integrity as unverified (e.g. remote API without local DB).")
    parser.add_argument("--ids", help="Comma-separated positive release IDs, maximum 200.")
    parser.add_argument("--categories", default="films,series,animation,emissions,spectacle")
    parser.add_argument("--filter", choices=["all", "without_poster", "without_identity", "with_identity"], default="all")
    parser.add_argument("--divergences", action="store_true")
    parser.add_argument("--count", type=int, choices=[10, 25, 50, 100, 200], default=10)
    parser.add_argument("--mode", choices=["offline", "tmdb"], default="offline")
    parser.add_argument("--budget", type=int, choices=range(1, 31), default=30)
    parser.add_argument("--fair-budget", action="store_true")
    parser.add_argument("--repeat", type=int, choices=range(1, 11), default=1)
    parser.add_argument("--timeout", type=float, default=120)
    parser.add_argument("--poll", type=float, default=0.5)
    parser.add_argument("--output-dir", default=str(PRIVATE / "api-replay"))
    parser.add_argument("--before")
    parser.add_argument("--after")
    args = parser.parse_args(argv)
    if args.command == "compare":
        if not args.before or not args.after:
            raise Failure("comparison_reports_required")
        return compare_reports(args.before, args.after)
    if not 0 < args.timeout <= 3600 or not 0.01 <= args.poll <= 10:
        raise Failure("invalid_time_limits")
    config = load_config(args.config)
    api = Api(config, args.base_url, timeout=min(10, args.timeout))
    if args.command == "probe":
        replay = api.request("GET", ENDPOINT)
        shadow = api.request("GET", "/api/diagnostics/matching-shadow")
        return {"authorized": True, "replayRunning": replay.get("running") is True, "shadowEnabled": shadow.get("enabled") is True}
    if args.command == "diagnostics":
        shadow = api.request("GET", "/api/diagnostics/matching-shadow")
        replay = api.request("GET", ENDPOINT)
        path = save_report({"shadow": shadow, "replay": replay}, args.output_dir)
        return {"report": str(path), "shadow": {k: shadow.get(k) for k in
            ("enabled", "observations", "releases", "accept", "ambiguous", "reject", "notEvaluable", "divergences", "averageDurationMs")},
            "replayRunning": replay.get("running") is True}
    database = args.database or config.get("database")
    posters = args.poster_dir or config.get("posterDir")
    if not database and not args.api_only:
        raise Failure("local_database_required_for_integrity_or_explicit_api_only")
    if args.ids and args.divergences:
        raise Failure("choose_ids_or_divergences")
    ids = []
    if args.ids:
        tokens = args.ids.replace(";", ",").replace(" ", ",").split(",")
        if len(args.ids) > 4000 or any(not t.isascii() or not t.isdigit() or t.startswith("0") for t in tokens if t):
            raise Failure("release_ids_invalid")
        ids = [int(t) for t in tokens if t]
        if not ids or len(ids) > 200 or len(set(ids)) != len(ids) or any(not 1 <= i <= 9007199254740991 for i in ids):
            raise Failure("release_ids_invalid")
    categories = args.categories.split(",")
    if not categories or len(categories) > 5 or len(set(categories)) != len(categories) or any(c not in {"films", "series", "animation", "emissions", "spectacle"} for c in categories):
        raise Failure("categories_invalid")
    before = business_snapshot(database, posters) if database else None
    reports = []
    previous = args.before
    failure = None
    try:
        for _ in range(args.repeat):
            request = {"count": args.count, "mode": args.mode, "budget": args.budget, "selection": {
                "method": "ids" if ids else "shadow_divergences" if args.divergences else "sample",
                "categories": categories, "releaseIds": ids, "filter": args.filter, "fairBudget": args.fair_budget}}
            report = execute(api, request, args.timeout, args.poll)
            unchanged = business_snapshot(database, posters) == before if database else None
            report["automation"] = {"businessIntegrity": "unchanged" if unchanged is True else "changed" if unchanged is False else "not_verified_remote"}
            path = save_report(report, args.output_dir)
            if unchanged is False:
                raise Failure("business_data_changed_no_rollback_attempted")
            item = {"report": str(path), "processed": report.get("processed"), "requests": report.get("requestsUsed"),
                    "errors": report.get("errors"), "decisions": {k: report.get("summary", {}).get(k) for k in ("accept", "ambiguous", "reject", "notEvaluable")}, "divergences": sum(bool(e.get("detail", {}).get("divergence")) for e in report.get("entries", []))}
            if previous:
                comparison = compare_reports(previous, path)
                item["comparison"] = {k: v for k, v in comparison.items() if k != "entries"}
            reports.append(item)
            previous = path
            ids = report.get("selectedIds") or []  # Never resample an empty or changed cohort on repetition.
            if not ids:
                break
    except BaseException as error:
        failure = error
    unchanged = business_snapshot(database, posters) == before if database else None
    if unchanged is False:
        raise Failure("business_data_changed_no_rollback_attempted")
    if failure:
        raise failure
    return {"businessIntegrity": "unchanged" if unchanged else "not_verified_remote", "runs": reports}


if __name__ == "__main__":
    try:
        print(json.dumps(main(), ensure_ascii=True))
    except KeyboardInterrupt:
        print(json.dumps({"error": "interrupted"})); sys.exit(130)
    except Failure as error:
        print(json.dumps({"error": str(error)})); sys.exit(2)
    except Exception:
        print(json.dumps({"error": "automation_failed"})); sys.exit(2)
