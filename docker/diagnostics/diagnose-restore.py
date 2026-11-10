#!/usr/bin/env python3
"""Feedarr 304572d: read-only Docker diagnosis. Python 3.8+, Docker CLI; no packages.
Run: sudo python3 -B diagnose-restore.py CONTAINER
Never prints raw inspect, HTTP bodies, logs, backup names, tokens or database contents.
No writes inside the container; no SQLite connection; no restore POST, restart or cleanup.
"""
import argparse
import collections
import datetime
import hashlib
import json
import posixpath
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

LIMIT = 4 * 1024 * 1024
GUID = re.compile(r"^[0-9a-f]{32}$", re.I)
HASH = re.compile(r"^[0-9a-f]{64}$", re.I)
STATES = {"none", "pending", "failed", "applied", "cancelled"}
ERRORS = {"restore_state_unreadable", "restore_startup_failed"}


def emit(section, **fields):
    print(json.dumps(dict(section=section, **fields), ensure_ascii=True), flush=True)


def run(args, timeout=25):
    try:
        p = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           timeout=timeout, check=False)
        # Never expose tool errors, which may contain credentials or arbitrary names.
        return p.returncode, p.stdout[:LIMIT], p.stderr[:LIMIT]
    except subprocess.TimeoutExpired:
        return 124, b"", b""
    except OSError:
        return 127, b"", b""


def decode_json(raw):
    try:
        return json.loads(raw)
    except (ValueError, UnicodeError):
        return None


def number(value):
    return value if type(value) is int else None


def boolean(value):
    return value if type(value) is bool else None


def status_summary(data):
    if not isinstance(data, dict):
        return dict(json_object=False)
    out = dict(json_object=True)
    for key in ("pending", "isBusy", "needsRestart", "lastSuccess", "syncBlocked"):
        if key in data:
            out[key] = boolean(data[key])
    for key in ("preparedAtTs", "missingPosters", "startedAtTs", "lastCompletedAtTs", "activeSyncActivities"):
        if key in data:
            out[key] = number(data[key])
    for key, allowed in (("state", STATES), ("operation", {"idle", "create", "prepare-restore", "delete", "purge"}),
                         ("phase", {"idle", "waiting-sync", "running"})):
        if key in data:
            out[key] = data[key] if isinstance(data[key], str) and data[key] in allowed else "other_redacted"
    if "id" in data:
        out["id"] = data["id"] if isinstance(data["id"], str) and GUID.fullmatch(data["id"]) else None
    for key in ("error", "lastError"):
        if key in data:
            value = data[key]
            out[key] = value if isinstance(value, str) and value in ERRORS else ("present_redacted" if value else None)
    if isinstance(data.get("restorePending"), dict):
        out["restorePending"] = status_summary(data["restorePending"])
    return out


def manifest_summary(data):
    if not isinstance(data, dict):
        return dict(valid_structure=False), None
    identity = data.get("Id")
    posters = data.get("Posters")
    name = data.get("BackupName")
    valid = (data.get("Version") == 1 and isinstance(identity, str) and bool(GUID.fullmatch(identity))
             and isinstance(data.get("DatabaseHash"), str) and bool(HASH.fullmatch(data["DatabaseHash"]))
             and type(data.get("PreparedAtTs")) is int and data["PreparedAtTs"] > 0
             and type(data.get("MissingPosters")) is int and data["MissingPosters"] >= 0
             and isinstance(name, str) and bool(name) and posixpath.basename(name) == name
             and isinstance(posters, dict) and all(isinstance(v, str) and HASH.fullmatch(v) for v in posters.values()))
    return dict(valid_structure=bool(valid), id=identity if isinstance(identity, str) and GUID.fullmatch(identity) else None,
                preparedAtTs=number(data.get("PreparedAtTs")), missingPosters=number(data.get("MissingPosters")),
                poster_count=len(posters) if isinstance(posters, dict) else None,
                backup_name_present=isinstance(name, str) and bool(name)), identity if valid else None


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def public_get(path):
    # Fixed authorized origin; no cookies, credentials, env proxy or redirects.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(urllib.request.Request("https://feedarr.dalg.ovh" + path,
                         headers={"Accept": "application/json"}), timeout=12) as response:
            return response.status, response.read(LIMIT)
    except urllib.error.HTTPError as error:
        return error.code, b""  # no auth errors or reverse-proxy content printed
    except Exception:
        return None, b""


def log_summary(raw):
    text = raw.decode("utf-8", "replace")
    categories = {
        "prepare_failed": "Backup restore preparation failed",
        "backup_failed": "Backup operation failed",
        "preview_failed": "Backup preview failed",
        "unhandled_exception": "Unhandled exception",
        "restore_cleanup_deferred": "[RESTORE] Unreferenced staging cleanup deferred",
        "directory_open_failed": "Unable to open restore directory for synchronization",
        "directory_fsync_failed": "Unable to synchronize restore directory",
        "sqlite_integrity_failed": "Restore SQLite integrity check failed",
        "staged_hash_mismatch": "Staged database checksum mismatch",
        "pending_conflict": "pending_restore_exists",
        "pending_changed": "pending_restore_changed",
        "operation_in_progress": "restore application already in progress",
        "access_denied": "Permission denied",
        "no_space": "No space left on device",
        "read_only_filesystem": "Read-only file system",
    }
    exceptions = ["IOException", "UnauthorizedAccessException", "SqliteException", "JsonException",
                  "InvalidDataException", "OperationCanceledException", "TaskCanceledException",
                  "BackupOperationException", "TimeoutException", "NullReferenceException"]
    counts = {key: text.count(marker) for key, marker in categories.items() if marker in text}
    exception_counts = {name: len(re.findall(r"\b" + name + r"\b", text)) for name in exceptions}
    frames = {method: text.count(method) for method in (
        "PendingRestoreStore.Publish", "PendingRestoreStore.CopyTree", "PendingRestoreStore.CopyDurable",
        "PendingRestoreStore.Inventory", "PendingRestoreStore.SyncDirectory", "PendingRestoreStore.WriteJson",
        "PendingRestoreStore.RepairUnavailablePosters", "PendingRestoreStore.ApplyAtStartup",
        "BackupService.RestoreBackupAsync", "BackupExecutionCoordinator.RunExclusiveAsync",
        "SystemApiCore.RestoreBackup") if method in text}
    source_locations = []
    for line in text.splitlines():
        for method in frames:
            if method in line and re.search(r"\bat\s+", line):
                match = re.search(r":line\s+(\d+)\s*$", line)
                entry = dict(method=method, line=int(match[1]) if match else None)
                if entry not in source_locations:
                    source_locations.append(entry)
    failure_details = []
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if categories["prepare_failed"] not in line:
            continue
        stamp = re.match(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z)\s", line)
        detail = dict(time=stamp[1] if stamp else None, exception_types=[], locations=[], cancellation_check_frame=False)
        for following in lines[index + 1:index + 51]:
            if re.search(r"\s(?:info|fail|warn|dbug|crit|trce):\s", following):
                break
            for name in exceptions:
                if re.search(r"\b" + name + r"\b", following) and name not in detail["exception_types"]:
                    detail["exception_types"].append(name)
            if "CancellationToken.ThrowIfCancellationRequested" in following or "CancellationToken.ThrowOperationCanceledException" in following:
                detail["cancellation_check_frame"] = True
            for method in frames:
                if method in following and re.search(r"\bat\s+", following):
                    match = re.search(r":line\s+(\d+)\s*$", following)
                    entry = dict(method=method, line=int(match[1]) if match else None)
                    if entry not in detail["locations"]:
                        detail["locations"].append(entry)
        failure_details.append(detail)
    events = []
    for line in lines:
        found = [key for key, marker in categories.items() if marker in line]
        if found:
            stamp = re.match(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z)\s", line)
            events.append(dict(time=stamp.group(1) if stamp else None, categories=found))
    return dict(categories=counts, stack_frame_counts=frames, source_locations=source_locations,
                preparation_failures=failure_details[-10:], exception_counts={k: v for k, v in exception_counts.items() if v},
                recent_events=events[-40:], bytes_scanned=len(raw),
                note="No raw log lines. Counts concern the bounded log sample; absence is not proof of success.")


class Diagnosis:
    def __init__(self, container):
        self.container = container
        self.runtime_user = None

    def execute(self, script, *args, timeout=25, as_user=None):
        # Scripts are fixed literals; paths are positional arguments, never interpolated as shell code.
        return run(["docker", "exec", *(["--user", as_user] if as_user else []), self.container, "sh", "-c", script, "diagnose-restore", *args], timeout)

    def read(self, path):
        # Refuse symlinks in every ancestor to avoid reading outside the selected tree.
        rc, raw, _ = self.execute('p="$1"; q="$p"; while [ "$q" != / ] && [ "$q" != . ]; do '
                                 '[ ! -L "$q" ] || exit 42; q=$(dirname "$q"); done; '
                                 '[ -f "$p" ] || exit 44; head -c 4194305 -- "$p"', path)
        return rc, raw if len(raw) < LIMIT else b""

    def stat(self, path, label):
        rc, raw, _ = self.execute('stat -c "%F|%a|%u|%g|%s|%Y|%d|%i" -- "$1"', path)
        parts = raw.decode("utf-8", "replace").strip().split("|")
        if rc == 0 and len(parts) == 8 and all(x.isdigit() for x in parts[1:]):
            emit("file", label=label, kind=parts[0] if parts[0] in {"directory", "regular file", "regular empty file", "symbolic link"} else "other",
                 mode=parts[1], uid=int(parts[2]), gid=int(parts[3]), bytes=int(parts[4]),
                 modified_epoch=int(parts[5]), device=int(parts[6]), inode=int(parts[7]))
        else:
            emit("file", label=label, metadata_available=False, command_exit=rc)

    def main(self):
        emit("report", format_version=2, time_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
             expected_sha="304572d", read_only=True)
        rc, raw, _ = run(["docker", "inspect", "--type", "container", self.container])
        parsed = decode_json(raw)
        if rc or not isinstance(parsed, list) or not parsed:
            emit("fatal", reason="container_inspect_unavailable", command_exit=rc)
            return 1
        obj = parsed[0]
        cfg, state = obj.get("Config", {}), obj.get("State", {})
        env = dict(item.split("=", 1) for item in cfg.get("Env", []) if "=" in item)
        version = env.get("FEEDARR_VERSION", "")
        safe_version = version if re.fullmatch(r"v?\d+\.\d+\.\d+(?:[-+.][A-Za-z0-9.-]+)?|beta-[a-f0-9]+", version) else "unavailable_or_redacted"
        emit("container", id=obj.get("Id", "")[:12] if re.fullmatch(r"[a-f0-9]{64}", obj.get("Id", "")) else None,
             version=safe_version, matches_expected_sha="304572d" in safe_version,
             running=boolean(state.get("Running")), restarting=boolean(state.get("Restarting")),
             oom_killed=boolean(state.get("OOMKilled")), exit_code=number(state.get("ExitCode")),
             restart_count=number(obj.get("RestartCount")),
             started_at=state.get("StartedAt") if re.fullmatch(r"[0-9TZ:.-]+", str(state.get("StartedAt", ""))) else None,
             health=state.get("Health", {}).get("Status") if state.get("Health", {}).get("Status") in {"healthy", "unhealthy", "starting"} else None,
             readonly_rootfs=boolean(obj.get("HostConfig", {}).get("ReadonlyRootfs")),
             docker_state_error_present=bool(state.get("Error")))
        image = obj.get("Image", "")
        if re.fullmatch(r"sha256:[a-f0-9]{64}", image):
            rc, raw, _ = run(["docker", "image", "inspect", image])
            images = decode_json(raw)
            image_cfg = images[0].get("Config", {}) if isinstance(images, list) and images else {}
            image_env = dict(item.split("=", 1) for item in image_cfg.get("Env", []) if "=" in item)
            image_version = image_env.get("FEEDARR_VERSION", "")
            revision = image_cfg.get("Labels", {}).get("org.opencontainers.image.revision", "") if isinstance(image_cfg.get("Labels"), dict) else ""
            emit("image", content_id=image, inspect_available=rc == 0,
                 image_version=image_version if re.fullmatch(r"v?\d+\.\d+\.\d+(?:[-+.][A-Za-z0-9.-]+)?|beta-[a-f0-9]+", image_version) else "unavailable_or_redacted",
                 revision=revision if re.fullmatch(r"[a-f0-9]{7,40}", revision) else None,
                 digest_count=len(images[0].get("RepoDigests", [])) if isinstance(images, list) and images else None,
                 note="Immutable content ID identifies the real local image; mutable tags are not proof of version.")
        rc, identity_raw, _ = self.execute('sed -n "/^Uid:/p; /^Gid:/p" /proc/1/status')
        identities = {}
        for kind in ("Uid", "Gid"):
            match = re.search(r"^" + kind + r":\s+(\d+)\s+(\d+)", identity_raw.decode("utf-8", "replace"), re.M)
            emit("process_identity", kind=kind, real=int(match[1]) if match else None, effective=int(match[2]) if match else None)
            if match:
                identities[kind] = match[2]
        if len(identities) == 2:
            self.runtime_user = identities["Uid"] + ":" + identities["Gid"]
        # Resolve normal .NET JSON/environment configuration without outputting other settings.
        data, database = "data", "feedarr.db"
        rc, cwd_raw, _ = self.execute('readlink /proc/1/cwd')
        cwd = cwd_raw.decode("utf-8", "replace").strip() if rc == 0 else "/app"
        if not cwd.startswith("/") or "\n" in cwd:
            cwd = "/app"
        files = [posixpath.join(cwd, "appsettings.json")]
        environment = env.get("ASPNETCORE_ENVIRONMENT", env.get("DOTNET_ENVIRONMENT", "Production"))
        if re.fullmatch(r"[A-Za-z0-9_-]{1,50}", environment):
            files.append(posixpath.join(cwd, "appsettings." + environment + ".json"))
        for path in files:
            rc, raw = self.read(path)
            settings = decode_json(raw)
            app = settings.get("App", {}) if isinstance(settings, dict) else {}
            if isinstance(app, dict):
                data, database = app.get("DataDir", data), app.get("DbFileName", database)
        env_ci = {k.lower(): v for k, v in env.items()}
        data = env_ci.get("app__datadir", data)
        database = env_ci.get("app__dbfilename", database)
        rc, cmdline, _ = self.execute('head -c 65536 /proc/1/cmdline')
        argv = cmdline.decode("utf-8", "replace").split("\0") if rc == 0 else []
        for index, arg in enumerate(argv):
            for setting in ("App:DataDir", "App:DbFileName"):
                value = None
                if arg.lower().startswith("--" + setting.lower() + "="):
                    value = arg.split("=", 1)[1]
                elif arg.lower() == "--" + setting.lower() and index + 1 < len(argv):
                    value = argv[index + 1]
                if value is not None:
                    if setting == "App:DataDir":
                        data = value
                    else:
                        database = value
        if not isinstance(data, str) or not data or any(ord(c) < 32 for c in data):
            emit("fatal", reason="invalid_data_path")
            return 1
        data = posixpath.normpath(data if data.startswith("/") else posixpath.join(cwd, data))
        if data == "/":
            emit("fatal", reason="data_path_is_root_refusing_scan")
            return 1
        emit("configuration", data_dir=data, database_name_redacted=True, database_filename_valid=isinstance(database, str) and bool(database) and posixpath.basename(database) == database,
             note="DataDir resolved from JSON, environment and standard --App:DataDir process arguments; no other config emitted.")
        covering_mounts = 0
        for index, mount in enumerate(obj.get("Mounts", [])):
            target = mount.get("Destination", "")
            if not isinstance(target, str):
                continue
            relevant = data == target or data.startswith(target.rstrip("/") + "/") or target.startswith(data + "/")
            if relevant:
                if data == target or data.startswith(target.rstrip("/") + "/"):
                    covering_mounts += 1
                source = mount.get("Source", "")
                emit("data_mount", index=index, type=mount.get("Type") if mount.get("Type") in {"bind", "volume", "tmpfs"} else "other",
                     destination=target, writable=boolean(mount.get("RW")),
                     source_path=source if isinstance(source, str) and source.startswith("/") and not any(ord(c) < 32 for c in source) else "redacted",
                     source_fingerprint=hashlib.sha256(str(source).encode()).hexdigest()[:16],
                     covers_data=data == target or data.startswith(target.rstrip("/") + "/"))
        emit("data_persistence", covering_mount_count=covering_mounts, persistent_mount_found=covering_mounts > 0)
        root = posixpath.join(data, "restore-state")
        for path, label in ((data, "data"), (root, "restore-state"), (root + "/stages", "stages"),
                            (root + "/rescue", "rescue"), (data + "/backups", "backups"), (data + "/posters", "posters"),
                            (root + "/process.lock", "process.lock"), (root + "/operation.lock", "operation.lock"),
                            (data + "/restore.restart-required.flag", "legacy_restart_flag")):
            self.stat(path, label)
        if self.runtime_user:
            for path, label in ((data, "data"), (root, "restore-state"), (root + "/pending.json", "pending.json"),
                                (root + "/stages", "stages"), (data + "/posters", "posters")):
                rc, raw, _ = self.execute('for flag in r w x; do if test -"$flag" "$1"; then '
                    'printf "1 "; else printf "0 "; fi; done', path, as_user=self.runtime_user)
                bits = raw.split()
                emit("application_permission_probe", label=label, command_exit=rc,
                     readable=bits[0] == b"1" if len(bits) == 3 else None,
                     writable=bits[1] == b"1" if len(bits) == 3 else None,
                     executable_or_traversable=bits[2] == b"1" if len(bits) == 3 else None,
                     note="test -r/-w/-x only; no write attempted. UID/GID probe does not reproduce supplementary groups.")
        rc, raw, _ = self.execute('df -Pk -- "$1" | tail -n 1', data)
        # Only disk capacity, not filesystem/mount names.
        text = raw.decode("utf-8", "replace")
        parts = text.splitlines()[-1].split() if text.splitlines() else []
        if len(parts) >= 6 and all(p.isdigit() for p in parts[-5:-2]):
            emit("disk", blocks_kib=int(parts[-5]), used_kib=int(parts[-4]), available_kib=int(parts[-3]))
        if getattr(self, "storage_summary", False):
            started = time.monotonic()
            rc, raw, errors = self.execute('p="$1"; q="$p"; while [ "$q" != / ]; do '
                '[ ! -L "$q" ] || exit 42; q=$(dirname "$q"); done; '
                '[ -d "$p" ] || exit 44; find "$p" -type f -printf "%s\\n" | '
                'awk \'{n++; bytes+=$1} END {printf "%.0f %.0f\\n",n,bytes}\'', data + "/posters", timeout=90)
            fields = raw.split()
            valid = rc == 0 and not errors and len(fields) == 2 and all(x.isdigit() for x in fields)
            emit("poster_storage_metadata", command_exit=rc, partial_or_error=not valid,
                 file_count=int(fields[0]) if valid else None, total_bytes=int(fields[1]) if valid else None,
                 scan_seconds=round(time.monotonic() - started, 3),
                 note="Metadata count only; no poster contents opened or copied. This is not a copy/fsync benchmark.")
        for filename in ("pending.json", "pending.json.tmp", "operation.json", "operation.json.tmp", "result.json"):
            self.stat(root + "/" + filename, filename)
            rc, raw = self.read(root + "/" + filename)
            value = decode_json(raw)
            if filename.startswith("pending"):
                summary, identity = manifest_summary(value)
                emit("manifest", file=filename, read_exit=rc, **summary)
                if identity and filename == "pending.json":
                    stage = root + "/stages/" + identity
                    self.stat(stage, "pending_stage")
                    self.stat(stage + "/incoming.db", "pending_incoming_db")
                    self.stat(stage + "/posters", "pending_posters")
                    sr, staged = self.read(stage + "/manifest.json")
                    emit("stage_manifest", read_exit=sr, matches_pointer=decode_json(staged) == value)
                    # Hash only the staged DB, never SQLite active or backup ZIPs; refuse ancestor symlinks.
                    hr, digest, _ = self.execute('p="$1"; q="$p"; while [ "$q" != / ]; do '
                        '[ ! -L "$q" ] || exit 42; q=$(dirname "$q"); done; '
                        '[ -f "$p" ] || exit 44; sha256sum -- "$p"', stage + "/incoming.db", timeout=120)
                    actual = digest.decode("utf-8", "replace").split(" ", 1)[0].strip()
                    emit("stage_database_checksum", command_exit=hr,
                         matches_manifest=actual.upper() == value["DatabaseHash"].upper() if hr == 0 and HASH.fullmatch(actual) else None)
            elif filename.startswith("operation"):
                emit("journal", file=filename, read_exit=rc, json_object=isinstance(value, dict),
                     version=number(value.get("Version")) if isinstance(value, dict) else None,
                     id_valid=bool(isinstance(value, dict) and isinstance(value.get("Id"), str) and GUID.fullmatch(value["Id"])),
                     rescue_hash_present=isinstance(value, dict) and bool(value.get("RescueHash")))
            else:
                emit("result", read_exit=rc, **status_summary(value))
        # List top-level staging identities only; no backup/poster filenames or file contents.
        rc, raw, _ = self.execute('p="$1"; q="$p"; while [ "$q" != / ]; do '
            '[ ! -L "$q" ] || exit 42; q=$(dirname "$q"); done; '
            'find "$p" -maxdepth 2 -type d -printf "%P\\n"', root)
        names = raw.decode("utf-8", "replace").splitlines()
        counts = collections.Counter()
        for name in names:
            if re.fullmatch(r"building-[a-f0-9]{32}|(?:stages|rescue)/[a-f0-9]{32}", name):
                kind = "building" if name.startswith("building-") else name.split("/")[0]
                counts[kind] += 1
                if counts[kind] <= 30:
                    self.stat(root + "/" + name, name)
                    for child in ("incoming.db", "manifest.json", "posters"):
                        self.stat(root + "/" + name + "/" + child, name + "/" + child)
        emit("staging_inventory", command_exit=rc, counts=dict(counts), per_kind_display_limit=30,
             note="No stage is deleted. Building directories may belong to an operation still running.")
        rc, html = self.read(posixpath.join(cwd, "wwwroot/index.html"))
        http_status, public_html = public_get("/")
        emit("frontend_document", local_read_exit=rc, public_http_status=http_status,
             local_sha256=hashlib.sha256(html).hexdigest() if rc == 0 and html else None,
             public_sha256=hashlib.sha256(public_html).hexdigest() if http_status == 200 and public_html else None,
             same_document=html == public_html if rc == 0 and html and http_status == 200 and public_html else None,
             note="HTML bytes are not displayed. A difference can reflect proxy injection, auth/setup or a stale deployment; it is not by itself a diagnosis.")
        for path in ("/api/system/backups/state", "/api/system/backups/restore-state"):
            rc, body, headers = self.execute('wget --no-proxy --max-redirect=0 --timeout=12 --tries=1 '
                '--header="Accept: application/json" -S -O - "$1"', "http://127.0.0.1:8080" + path, timeout=18)
            statuses = re.findall(rb"HTTP/\d(?:\.\d)?\s+(\d{3})", headers)
            emit("endpoint_local", route=path, command_exit=rc, status=int(statuses[-1]) if statuses else None,
                 **status_summary(decode_json(body)))
            status, body = public_get(path)
            emit("endpoint_public", route=path, status=status, **status_summary(decode_json(body)))
        rc, logs, errors = run(["docker", "logs", "--timestamps", "--since", "168h", "--tail", "5000", self.container], timeout=30)
        emit("docker_logs", command_exit=rc, **log_summary(logs + b"\n" + errors))
        emit("limits", notes=["No POST/DELETE, no restart, no chmod/chown, no file writes in container, no direct SQLite access.",
            "GETs can produce normal access logs. HTTP 401/403 is reported without attempting authentication or bypass.",
            "A previous restore POST response cannot be reconstructed from GETs; browser Network capture may still be needed.",
            "Inventory and API calls are successive observations, not an atomic snapshot. Rerun unchanged if needed.",
            "Checksums validate staged DB bytes only; poster contents and SQLite integrity are deliberately not opened.",
            "JSON read capped at 4 MiB. Logs capped at last 5000 lines/168h and 4 MiB per stream; raw data never emitted."])
        return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("container", help="Exact existing Docker container name or ID")
    parser.add_argument("--storage-summary", action="store_true", help="Add poster file count and total size (metadata only; up to 90s)")
    parser.add_argument("--logs-only", action="store_true", help="Only reread and sanitize existing Docker logs; no exec or HTTP")
    args = parser.parse_args()
    if args.logs_only and args.storage_summary:
        parser.error("--logs-only and --storage-summary are mutually exclusive")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", args.container):
        parser.error("invalid container name or ID")
    try:
        if args.logs_only:
            emit("report", format_version=2, mode="logs_only", expected_sha="304572d", read_only=True)
            rc, logs, errors = run(["docker", "logs", "--timestamps", "--since", "168h", "--tail", "5000", args.container], timeout=30)
            emit("docker_logs", command_exit=rc, **log_summary(logs + b"\n" + errors))
            return 0 if rc == 0 else 1
        diagnosis = Diagnosis(args.container)
        diagnosis.storage_summary = args.storage_summary
        return diagnosis.main()
    except Exception:
        emit("fatal", reason="diagnostic_failed_details_redacted")
        return 1


if __name__ == "__main__":
    sys.exit(main())
