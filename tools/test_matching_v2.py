import http.server
import threading
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location('cli', Path(__file__).with_name('matching-v2.py'))
cli = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cli)


class FakeApi:
    timeout = 1
    def __init__(self, running=False):
        self.calls = []
        self.running = running
        self.owner = str(uuid.uuid4())
    def request(self, method, path, body=None, timeout=None):
        self.calls.append((method, path, body))
        if path.endswith('/preview'):
            return {'generation': 0, 'selectedIds': [1, 2]}
        if path.endswith('/stop'):
            return {}
        if method == 'POST':
            return {'generation': 1, 'sessionId': self.owner, 'running': self.running, 'status': 'completed'}
        if path.endswith('/export'):
            return {'sessionId': self.owner, 'selectedIds': [1, 2], 'entries': [], 'processed': 2, 'summary': {}}
        return {'generation': 0, 'running': False}


class CliTests(unittest.TestCase):
    def test_real_http_cookie_csrf_and_private_export(self):
        fake = FakeApi()
        received = []
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def handle_request(self):
                received.append((self.command, self.path, dict(self.headers)))
                if self.path == '/api/auth/session':
                    result = {'authenticated': True, 'csrfToken': 'csrf-canary'}
                else:
                    if self.command == 'POST':
                        if self.headers.get('X-Feedarr-CSRF') != 'csrf-canary' or not self.headers.get('Origin'):
                            self.send_error(403)
                            return
                    body = json.loads(self.rfile.read(int(self.headers['Content-Length']))) if self.command == 'POST' else None
                    result = fake.request(self.command, self.path, body)
                data = json.dumps(result).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(data)
            do_GET = handle_request
            do_POST = handle_request
        with patch('socket.getfqdn', return_value='localhost'):
            server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory() as temp, patch.dict(os.environ, {'FEEDARR_SESSION_COOKIE': 'session=cookie-canary'}, clear=True):
                result = cli.main(['run', '--api-only', '--base-url', f'http://127.0.0.1:{server.server_port}', '--output-dir', temp])
                exported = Path(result['runs'][0]['report']).read_text()
                self.assertNotIn('cookie-canary', exported + str(result))
                self.assertNotIn('csrf-canary', exported + str(result))
                self.assertEqual('not_verified_remote', result['businessIntegrity'])
                self.assertTrue(any(headers.get('X-Feedarr-Request') == '1' for _, _, headers in received))
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_offline_compare_uses_existing_comparator(self):
        with tempfile.TemporaryDirectory() as temp:
            before = Path(temp) / 'before.json'
            after = Path(temp) / 'after.json'
            row = {'coverage': 'complete', 'detail': {'releaseId': 1, 'decision': 'reject', 'rawTitle': 'private-canary'}}
            before.write_text(json.dumps({'entries': [row]}))
            row['detail']['decision'] = 'ambiguous'
            after.write_text(json.dumps({'entries': [row]}))
            result = cli.compare_reports(before, after)
            self.assertEqual(1, result['decisions'])
            self.assertNotIn('private-canary', str(result))

    def test_diagnostics_exports_existing_bounded_snapshots(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(cli, 'Api') as api:
            api.return_value.request.side_effect = [{'divergences': 2, 'entries': []}, {'running': False}]
            result = cli.main(['diagnostics', '--output-dir', temp])
            self.assertEqual(2, result['shadow']['divergences'])
            self.assertTrue(Path(result['report']).exists())
            self.assertTrue(all(call.args[0] == 'GET' for call in api.return_value.request.call_args_list))

    def test_preview_ids_are_pinned(self):
        api = FakeApi()
        result = cli.execute(api, {'mode': 'offline'}, 1, .01)
        self.assertEqual([1, 2], result['selectedIds'])
        self.assertEqual([1, 2], api.calls[2][2]['expectedIds'])
        self.assertEqual(0, api.calls[2][2]['generation'])

    def test_timeout_stop_is_owner_guarded(self):
        api = FakeApi(True)
        with self.assertRaises(cli.Failure):
            cli.execute(api, {}, .001, .01)
        stop = api.calls[-1]
        self.assertTrue(stop[1].endswith('/stop'))
        self.assertEqual({'sessionId': api.owner, 'generation': 1}, stop[2])

    def test_busy_replay_not_stopped(self):
        api = FakeApi()
        api.request = lambda *a, **k: {'running': True}
        with self.assertRaisesRegex(cli.Failure, 'another_replay'):
            cli.execute(api, {}, 1, .01)

    def test_integrity_reads_only_and_detects_changes(self):
        with tempfile.TemporaryDirectory() as temp:
            db = Path(temp) / 'test.db'
            with sqlite3.connect(db) as conn:
                conn.executescript('CREATE TABLE releases(id INTEGER PRIMARY KEY,title TEXT); CREATE TABLE media_entities(id INTEGER PRIMARY KEY); INSERT INTO releases VALUES(1,"private-canary");')
            original = db.read_bytes()
            before = cli.business_snapshot(db)
            self.assertEqual(original, db.read_bytes())
            self.assertNotIn('private-canary', str(before))
            with sqlite3.connect(db) as conn:
                conn.execute('UPDATE releases SET title="changed"')
            self.assertNotEqual(before, cli.business_snapshot(db))

    def test_reports_private_permissions_and_git_guard(self):
        with tempfile.TemporaryDirectory() as temp:
            report = cli.save_report({'entries': []}, temp)
            self.assertEqual(0o600, report.stat().st_mode & 0o777)
        with self.assertRaisesRegex(cli.Failure, 'ignored'):
            cli.save_report({}, cli.ROOT / 'docs')

    def test_url_and_auth_validation(self):
        with patch.dict(os.environ, {}, clear=True):
            for url in ['http://example.com', 'https://user:secret@example.com', 'https://example.com?secret=x']:
                with self.assertRaises(cli.Failure):
                    cli.Api({}, url)
            with patch.dict(os.environ, {'FEEDARR_USERNAME': 'user'}):
                with self.assertRaisesRegex(cli.Failure, 'incomplete'):
                    cli.Api({})

    def test_invalid_arguments_do_not_echo_input(self):
        with self.assertRaisesRegex(cli.Failure, '^invalid_arguments$'):
            cli.main(['run', '--secret-canary'])

    def test_run_requires_integrity_or_explicit_opt_out(self):
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaisesRegex(cli.Failure, 'local_database_required'):
                cli.main(['run'])

    def test_changed_business_data_fails_without_rollback(self):
        with patch.object(cli, 'Api'), patch.object(cli, 'business_snapshot', side_effect=[{'x': 1}, {'x': 2}, {'x': 2}]), patch.object(cli, 'execute', return_value={'entries': []}), patch.object(cli, 'save_report'):
            with self.assertRaisesRegex(cli.Failure, 'business_data_changed_no_rollback'):
                cli.main(['run', '--database', 'fixture'])

    def test_repeat_reuses_exact_ids(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(cli, 'Api'), patch.object(cli, 'execute', return_value={'selectedIds': [7, 9], 'entries': []}) as run, patch.object(cli, 'compare_reports', return_value={}):
            result = cli.main(['run', '--api-only', '--repeat', '2', '--output-dir', temp])
            self.assertEqual('not_verified_remote', result['businessIntegrity'])
            self.assertEqual([7, 9], run.call_args_list[1].args[1]['selection']['releaseIds'])

if __name__ == '__main__':
    unittest.main()
