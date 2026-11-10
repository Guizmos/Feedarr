"""Read-only command and redaction regression tests; never contacts Docker or production."""
import contextlib
import importlib.util
import io
import json
import pathlib
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("diagnose_restore", pathlib.Path(__file__).parents[1] / "diagnose-restore.py")
diag = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diag)
SECRET = "SECRET_API_TOKEN_BACKUP_NAME_DO_NOT_PRINT"
ID = "a" * 32


class DiagnosisTests(unittest.TestCase):
    def test_full_report_with_pending_and_sensitive_inputs(self):
        manifest = dict(Version=1, Id=ID, BackupName=SECRET + ".zip", PreparedAtTs=123,
                        DatabaseHash="b" * 64, Posters={SECRET: "c" * 64}, MissingPosters=0)
        container = {"Id": "a" * 64, "Image": "sha256:" + "d" * 64,
            "Config": {"Env": ["App__DataDir=/app/data", "API_KEY=" + SECRET, "FEEDARR_VERSION=2.5.18-beta-sha.304572d"]},
            "State": {"Running": True, "Error": SECRET},
            "HostConfig": {}, "Mounts": [{"Type": "bind", "Destination": "/app/data", "Source": "/volume1/docker/feedarr", "RW": True}]}
        commands = []

        def fake_run(args, timeout=25):
            commands.append(args)
            if args[:2] == ["docker", "inspect"]:
                return 0, json.dumps([container]).encode(), b""
            if args[:3] == ["docker", "image", "inspect"]:
                return 0, json.dumps([{"Config": {"Env": ["API_TOKEN=" + SECRET]}, "RepoDigests": []}]).encode(), b""
            if args[:2] == ["docker", "logs"]:
                return 0, ("2026-11-10T12:00:00Z Backup restore preparation failed for " + SECRET + "\nSystem.IO.IOException: " + SECRET + "\nPendingRestoreStore.SyncDirectory").encode(), b""
            self.assertEqual(args[:2], ["docker", "exec"])
            script = args[args.index("sh") + 2]
            if "sed -n" in script:
                return 0, b"Uid: 10001 10001 10001 10001\nGid: 10001 10001 10001 10001\n", b""
            if "for flag" in script:
                return 0, b"1 1 1 ", b""
            if "awk" in script:
                return 0, b"3 12345\n", b""
            if "readlink" in script:
                return 0, b"/app\n", b""
            if "stat -c" in script:
                return 0, b"regular file|600|10001|10001|4096|123|1|2\n", b""
            if "sha256sum" in script:
                return 0, ("b" * 64 + "  /app/data/restore-state/stages/" + ID + "/incoming.db").encode(), b""
            if "head -c" in script:
                path = args[-1]
                if path.endswith("pending.json") or path.endswith("manifest.json"):
                    return 0, json.dumps(manifest).encode(), b""
                if path.endswith("result.json"):
                    return 0, json.dumps({"state": "failed", "error": SECRET}).encode(), b""
                return 44, b"", b""
            if "wget" in script:
                return 0, json.dumps({"isBusy": True, "backupName": SECRET, "lastError": SECRET,
                    "restorePending": {"pending": True, "id": ID, "backupName": SECRET}}).encode(), ("HTTP/1.1 200 OK\nSet-Cookie: " + SECRET).encode()
            if "find" in script:
                return 0, ("stages/" + ID + "\nbuilding-" + ID + "\n" + SECRET).encode(), b""
            return 0, b"", b""

        output = io.StringIO()
        with patch.object(diag, "run", fake_run), patch.object(diag, "public_get", return_value=(401, b"")), contextlib.redirect_stdout(output):
            diagnosis = diag.Diagnosis("feedarr")
            diagnosis.storage_summary = True
            self.assertEqual(diagnosis.main(), 0)
        self.assertNotIn(SECRET, output.getvalue())
        reports = [json.loads(line) for line in output.getvalue().splitlines()]
        storage = next(x for x in reports if x["section"] == "poster_storage_metadata")
        self.assertEqual(storage["file_count"], 3)
        self.assertEqual(storage["total_bytes"], 12345)
        checksum = next(x for x in reports if x["section"] == "stage_database_checksum")
        self.assertTrue(checksum["matches_manifest"])
        self.assertTrue(next(x for x in reports if x["section"] == "stage_manifest")["matches_pointer"])
        self.assertTrue(next(x for x in reports if x["section"] == "data_persistence")["persistent_mount_found"])
        self.assertTrue(any(x["section"] == "application_permission_probe" and x["writable"] for x in reports))
        logs = next(x for x in reports if x["section"] == "docker_logs")
        self.assertEqual(logs["categories"]["prepare_failed"], 1)
        for args in commands:
            self.assertIn(args[1], {"inspect", "image", "exec", "logs"})
            if args[1] == "exec":
                script = args[args.index("sh") + 2]
                for forbidden in ("rm ", "mv ", "chmod", "chown", "sqlite", "POST", "DELETE", "touch", "mkdir", ">"):
                    self.assertNotIn(forbidden, script)

    def test_invalid_json_and_untrusted_error_do_not_escape(self):
        self.assertIsNone(diag.decode_json(b'{"broken":'))
        summary = diag.status_summary({"state": SECRET, "operation": SECRET, "error": SECRET, "id": SECRET})
        self.assertNotIn(SECRET, json.dumps(summary))
        manifest, identity = diag.manifest_summary({"Id": SECRET, "BackupName": SECRET})
        self.assertFalse(manifest["valid_structure"])
        self.assertIsNone(identity)
        self.assertNotIn(SECRET, json.dumps(manifest))

    def test_real_shell_reader_refuses_symlinks_and_does_not_execute_path_text(self):
        import tempfile
        import os
        import subprocess
        with tempfile.TemporaryDirectory(dir=os.path.realpath(tempfile.gettempdir())) as temp:
            root = pathlib.Path(temp)
            filename = root / "$(touch SHOULD_NOT_EXIST).json"
            filename.write_text('{"Version":1}')
            link = root / "link"
            link.symlink_to(filename)
            d = diag.Diagnosis("test")
            def local_execute(script, *args, **kwargs):
                p = subprocess.run(["sh", "-c", script, "test", *args], capture_output=True)
                return p.returncode, p.stdout, p.stderr
            with patch.object(d, "execute", side_effect=local_execute):
                rc, raw = d.read(str(filename))
                self.assertEqual(rc, 0)
                self.assertEqual(diag.decode_json(raw), {"Version": 1})
                self.assertEqual(d.read(str(link))[0], 42)
                self.assertEqual(d.read(str(root / "absent"))[0], 44)
            self.assertEqual(sorted(p.name for p in root.iterdir()), [filename.name, "link"])

    def test_cancellation_stack_locations_are_reported_without_raw_messages_or_paths(self):
        raw = ("2026-11-10T11:15:54.757962016Z Backup restore preparation failed for " + SECRET + "\n"
            "2026-11-10T11:15:54.757962016Z System.OperationCanceledException: " + SECRET + "\n"
            "2026-11-10T11:15:54.757962016Z at System.Threading.CancellationToken.ThrowIfCancellationRequested()\n"
            "2026-11-10T11:15:54.757962016Z at Feedarr.Api.Services.Backup.PendingRestoreStore.CopyTree(String " + SECRET + ") in /src/" + SECRET + ":line 331\n"
            "2026-11-10T11:15:54.757962016Z at Feedarr.Api.Services.Backup.PendingRestoreStore.Publish(String " + SECRET + ") in /src/" + SECRET + ":line 92\n"
            "2026-11-10T11:15:54.757962016Z info: New log record\n"
            "System.TimeoutException: unrelated " + SECRET).encode()
        result = diag.log_summary(raw)
        self.assertNotIn(SECRET, json.dumps(result))
        event = result["preparation_failures"][0]
        self.assertEqual(event["exception_types"], ["OperationCanceledException"])
        self.assertTrue(event["cancellation_check_frame"])
        self.assertIn({"method": "PendingRestoreStore.CopyTree", "line": 331}, event["locations"])
        self.assertIn({"method": "PendingRestoreStore.Publish", "line": 92}, event["locations"])

    def test_timeout_and_tool_errors_are_not_printed(self):
        import subprocess
        with patch.object(diag.subprocess, "run", side_effect=subprocess.TimeoutExpired("command " + SECRET, 1)):
            self.assertEqual(diag.run(["docker", "inspect", "feedarr"]), (124, b"", b""))


if __name__ == "__main__":
    unittest.main()
