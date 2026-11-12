import copy
import datetime as dt
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import benchmark as b
import provider_capture as capture


def plan_for(requests):
    return {'contract': capture.CONTRACT, 'sourceVersion': capture.fingerprint(), 'codeCommit': 'test-fixture',
            'captureToolVersion': capture.tool_version(), 'requests': {capture.key(q): q for q in requests}, 'releases': []}


def response_for(q, empty=False, more=False):
    if q['provider'] == 'tmdb': return {'page': 1, 'total_pages': 2 if more else 1, 'results': [] if empty else [
        {'id': 1, 'title': 'Public Film', 'release_date': '2020-01-01', 'original_title': 'Public Film', 'poster_path': '/not-downloaded.jpg'}]}
    if q['provider'] == 'tvmaze': return [] if empty else [{'show': {'id': 2, 'name': 'Public Series', 'premiered': '2020-01-01', 'image': None, 'externals': {'thetvdb': 42}}}]
    if q['provider'] == 'igdb': return [] if empty else [{'id': 3, 'name': 'Public Game', 'first_release_date': 1577836800, 'cover': None, 'alternative_names': [{'name': 'Known Alias'}]}]
    return {'next': None, 'results': [] if empty else [{'id': 4, 'name': 'Public Game', 'released': '2020-01-01', 'background_image': None}]}


def enabled():
    c = capture.config_template()
    for value in c['providers'].values(): value.update(enabled=True, termsReviewed=True)
    return c


def env_for(config):
    return {name: 'test-credential-not-for-network' for p in config['providers'].values() for name in p['credentialEnv'].values()}


class ProviderCaptureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=HERE / 'local'); self.root = Path(self.temp.name)
    def tearDown(self): self.temp.cleanup()
    def collect(self, plan, config=None, fake=None, execute=True, out=None):
        config = config or enabled()
        return capture.collect(plan, config, out or self.root / 'captures', execute=execute,
            transport=fake or (lambda q, p, c: {'status': 'ok', 'body': response_for(q)}),
            environ=env_for(config), sleep=lambda n: None, quota_path=self.root / 'quota.json')

    def test_no_opt_in_or_terms_means_no_network(self):
        q = capture.spec('tvmaze', 'series', 'Public Series'); plan = plan_for([q]); calls = []
        fake = lambda *args: calls.append(args)
        for c, execution in [(enabled(), False), (capture.config_template(), True), (enabled(), True)]:
            if execution and c['providers']['tvmaze']['enabled']: c['providers']['tvmaze']['termsReviewed'] = False
            result = self.collect(plan, c, fake, execution)
            self.assertEqual(0, result['httpAttempts'])
        self.assertFalse(calls)

    def test_missing_credentials_do_not_become_empty_results(self):
        q = capture.spec('tmdb', 'movie', 'Public Film'); config = enabled(); calls = []
        result = capture.collect(plan_for([q]), config, self.root / 'captures', True,
            transport=lambda *a: calls.append(a), environ={}, sleep=lambda n: None, quota_path=self.root / 'quota.json')
        self.assertEqual(0, result['httpAttempts']); self.assertFalse(calls)
        self.assertFalse((self.root / 'captures' / (capture.key(q) + '.json')).exists())

    def test_all_four_projections_preserve_scope_dates_and_no_images(self):
        for p, t in [('tmdb', 'movie'), ('tvmaze', 'series'), ('igdb', 'game'), ('rawg', 'game')]:
            q = capture.spec(p, t, 'Public Work')
            hits, more = capture.project_response(q, response_for(q))
            self.assertEqual(p, hits[0]['Hit']['Identity']['Provider']); self.assertEqual(2020, hits[0]['Hit']['Year'])
            self.assertFalse(more); self.assertNotIn('https:', json.dumps(hits))
        q = capture.spec('tvmaze', 'series', 'Title')
        self.assertEqual(([], False), capture.project_response(q, []))
        with self.assertRaises(ValueError): capture.project_response(q, None)

    def test_transport_endpoints_cannot_be_supplied_by_config(self):
        for p, t in [('tmdb', 'movie'), ('tvmaze', 'series'), ('igdb', 'game'), ('rawg', 'game')]:
            endpoint, params, headers, body = capture.request_details(capture.spec(p, t, 'Public Work'), 1,
                {'bearer': 'secret', 'clientId': 'secret', 'apiKey': 'secret'})
            self.assertTrue(endpoint.startswith('https://')); self.assertNotIn('image', endpoint)
            if p == 'igdb': self.assertEqual('POST', 'POST' if body else 'GET')
        with self.assertRaises(ValueError): capture.spec('tmdb', 'game', 'Bad scope')

    def test_budget_is_shared_across_output_directories(self):
        a = capture.spec('tvmaze', 'series', 'First Work'); z = capture.spec('tvmaze', 'series', 'Other Work')
        c = enabled(); c['providers']['tvmaze']['maxRequestsPerDay'] = 1
        self.assertEqual(1, self.collect(plan_for([a]), c)['httpAttempts'])
        self.assertEqual(0, self.collect(plan_for([z]), c, out=self.root / 'other-output')['httpAttempts'])

    def test_quota_reuse_and_cache_do_not_make_second_request(self):
        q = capture.spec('tvmaze', 'series', 'Public Work'); p = plan_for([q]); calls = []
        def fake(q, page, credentials): calls.append(page); return {'status': 'ok', 'body': []}
        self.assertEqual(1, self.collect(p, fake=fake)['httpAttempts'])
        self.assertEqual(0, self.collect(p, fake=fake)['httpAttempts']); self.assertEqual([1], calls)
        with self.assertRaises(ValueError): capture.load_hits(p, self.root / 'captures', [capture.key(q)])
        hits, gaps = capture.load_hits(p, self.root / 'captures', [capture.key(q)], allow_synthetic=True)
        self.assertFalse(gaps); self.assertEqual([], hits[0]['Hits'])

    def test_http_failure_and_429_never_become_empty_and_cooldown_persists(self):
        q = capture.spec('tvmaze', 'series', 'Public Work'); p = plan_for([q])
        self.collect(p, fake=lambda *a: {'status': 'http-error', 'httpStatus': 429, 'retryAfterSeconds': 3600})
        result, gaps = capture.load_hits(p, self.root / 'captures', [capture.key(q)], allow_synthetic=True)
        self.assertFalse(result); self.assertEqual('incomplete-or-error', gaps[0]['reason'])
        other = plan_for([capture.spec('tvmaze', 'series', 'Different')])
        self.assertEqual(0, self.collect(other, out=self.root / 'different')['httpAttempts'])

    def test_page_limit_remains_incomplete_and_missing_is_distinct(self):
        q = capture.spec('tmdb', 'movie', 'Public Film'); p = plan_for([q]); c = enabled(); c['maxPages'] = 1
        self.collect(p, c, fake=lambda q, page, cred: {'status': 'ok', 'body': response_for(q, more=True)})
        result, gaps = capture.load_hits(p, self.root / 'captures', [capture.key(q)], allow_synthetic=True)
        self.assertFalse(result); self.assertEqual('incomplete-or-error', gaps[0]['reason'])
        result, gaps = capture.load_hits(p, self.root / 'missing', [capture.key(q)], allow_synthetic=True)
        self.assertEqual('missing', gaps[0]['reason'])

    def test_provenance_or_capture_tampering_is_refused(self):
        q = capture.spec('tvmaze', 'series', 'Public'); p = plan_for([q]); self.collect(p)
        path = self.root / 'captures' / (capture.key(q) + '.json'); data = b.load(path); data['request']['query'] = 'Tampered'; b.write(path, data)
        with self.assertRaises(ValueError): capture.load_hits(p, path.parent, [capture.key(q)], allow_synthetic=True)
        stale = copy.deepcopy(p); stale['sourceVersion'] = 'wrong'
        with self.assertRaises(ValueError): capture.verify_plan(stale)

    def test_secrets_echoed_by_provider_are_never_persisted(self):
        q = capture.spec('tmdb', 'movie', 'Public'); p = plan_for([q])
        data = response_for(q); data['results'][0]['title'] = 'test-credential-not-for-network'
        self.collect(p, fake=lambda *a: {'status': 'ok', 'body': data})
        text = (self.root / 'captures' / (capture.key(q) + '.json')).read_text()
        self.assertNotIn('test-credential-not-for-network', text)
        self.assertIn('invalid-or-sensitive-response', text)

    def test_metric_denominator_keeps_abstentions_and_unvalidated_stays_unvalidated(self):
        p = plan_for([]); expected = {'Provider': 'tmdb', 'MediaType': 'Movie', 'Id': '1'}
        refs = {'planSha256': b.digest(p), 'entries': [{'id': 'a', 'identity': {'status': 'validated', 'expected': expected,
            'outcome': 'identity-verified', 'reviewer': 'independent-human', 'validatedAtUtc': '2026-11-12T12:00:00+00:00', 'independentEvidence': 'independent manual check'}}]}
        d = {'Id': 'a', 'Sufficient': True, 'ProtectedDiagnosticOnly': False, 'Historical': {'SelectedIdentity': expected}, 'V2': {'SelectedIdentity': None}}
        m = capture.measure(p, [d], refs)
        self.assertEqual(1, m['validatedCovered']); self.assertEqual(0, m['identificationRateV2']); self.assertIsNone(m['precisionV2'])
        refs['entries'][0]['identity']['status'] = 'unreviewed'
        self.assertIsNone(capture.measure(p, [d], refs)['identificationRateHistorical'])

    def test_config_requires_local_explicit_env_refs_and_permissions(self):
        path = self.root / 'config.json'; b.write(path, enabled()); self.assertEqual(1, capture.load_config(path)['version'])
        config = enabled(); config['providers']['tmdb']['credentialEnv']['bearer'] = 'actual-secret-value'
        b.write(path, config)
        with self.assertRaises(ValueError): capture.load_config(path)
        if os.name == 'posix':
            b.write(path, enabled()); path.chmod(0o644)
            with self.assertRaises(ValueError): capture.load_config(path)


    def test_offline_captured_comparison_uses_all_real_selection_functions(self):
        import identification
        q = {'Title': 'Public Work', 'Year': 2020, 'YearEvidence': 'Parsed', 'Origin': 'Primary'}
        def hit(provider, media, identifier):
            return {'Hit': {'Identity': {'Provider': provider, 'MediaType': media, 'Id': str(identifier)},
                           'Title': 'Public Work', 'Year': 2020, 'AlternativeTitles': []},
                    'HasPoster': True, 'Released': '2020-01-01', 'FirstReleaseUnix': 1577836800, 'TvdbId': 42}
        def batch(provider, hits): return {'Provider': provider, 'RequestKey': 'synthetic-fixture', 'Hits': hits, 'PageSize': 20}
        def row(identifier, category, historical, modern, protected=False):
            return {'Id': identifier, 'Category': category, 'HistoricalTitle': 'Public Work', 'HistoricalYear': 2020,
                    'HistoricalSeason': None, 'HistoricalEpisode': None, 'Protected': protected, 'Manual': False,
                    'Queries': [q], 'Parsed': {'Season': None, 'Episode': None}, 'ExistingObservation': {},
                    'HistoricalBatches': historical, 'V2Batches': modern}
        movie = hit('tmdb', 'Movie', 1); movie2 = hit('tmdb', 'Movie', 2)
        maze = hit('tvmaze', 'Series', 3); igdb = hit('igdb', 'Game', 4); rawg = hit('rawg', 'Game', 5)
        inputs = [row('movie', 'Film', [batch('tmdb', [movie])], [batch('tmdb', [movie, movie2])], True),
                  row('series', 'Serie', [batch('tvmaze', [maze])], [batch('tvmaze', [maze])]),
                  row('igdb', 'JeuWindows', [batch('igdb', [igdb]), batch('rawg', [])], [batch('igdb', [igdb])]),
                  row('rawg', 'JeuWindows', [batch('igdb', []), batch('rawg', [rawg])], [batch('rawg', [rawg])])]
        payload = self.root / 'synthetic-comparison.json'; b.write(payload, {'captureComparison': inputs})
        dll = identification.build_runner(self.root / 'build')
        import subprocess
        result = subprocess.run(['dotnet', str(dll), str(payload)], capture_output=True, timeout=60)
        self.assertEqual(0, result.returncode)
        decisions = {r['Id']: r for r in json.loads(result.stdout)['decisions']}
        self.assertEqual('Ambiguous', decisions['movie']['V2']['Decision'])
        self.assertTrue(decisions['movie']['ActiveGuard']['PreserveExisting'])
        for name, provider in [('movie', 'tmdb'), ('series', 'tvmaze'), ('igdb', 'igdb'), ('rawg', 'rawg')]:
            self.assertEqual('Accept', decisions[name]['Historical']['Decision'])
            self.assertEqual(provider, decisions[name]['Historical']['SelectedIdentity']['Provider'])


    def test_reference_scope_maps_do_not_turn_an_abstention_into_an_error(self):
        p = plan_for([]); expected = {'Provider': 'igdb', 'MediaType': 'Game', 'Id': '1'}
        other = {'Provider': 'rawg', 'MediaType': 'Game', 'Id': '2'}
        ref = {'status': 'validated', 'outcome': 'identity-verified', 'expected': expected, 'equivalents': [other],
               'reviewer': 'human', 'validatedAtUtc': '2026-11-12T12:00:00+00:00', 'independentEvidence': 'both identities independently checked'}
        refs = {'planSha256': b.digest(p), 'entries': [{'id': 'a', 'identity': ref}]}
        row = {'Id': 'a', 'Sufficient': True, 'ProtectedDiagnosticOnly': True,
               'Historical': {'SelectedIdentity': expected}, 'V2': {'SelectedIdentity': other}}
        metrics = capture.measure(p, [row], refs)
        self.assertEqual(1, metrics['v2Correct']); self.assertEqual(1, metrics['protectedDiagnosticCount'])
        ref['equivalents'] = []
        self.assertEqual(1, capture.measure(p, [row], refs)['unmappedIdentityScopes'])


    def test_sample_is_deterministic_preserves_120_and_includes_manual_controls(self):
        categories = ['Film', 'Serie', 'Animation', 'Emission', 'Spectacle', 'JeuWindows']
        entries = []; replay = []
        for i in range(160):
            identifier = f'case-{i}'
            entries.append({'id': identifier, 'raw': f'Public Work {i}', 'category': categories[i % 6],
                            'hasPoster': i % 2 == 0, 'stored': {}, 'association': {}})
            replay.append({'id': identifier, 'manual': i >= 158, 'hasStoredId': i % 2 == 0,
                           'titleChanged': True, 'yearChanged': False, 'parsed': {},
                           'shadowProposals': [{'Title': 'Public Work', 'Year': None}],
                           'historical': {'queryPlan': [], 'title': 'Public Work', 'year': None,
                              'primaryQueryTitle': 'Public Work', 'ambiguity': {'IsAmbiguous': False, 'SignificantTokenCount': 3, 'IsCommonTitle': False}}})
        corpus = {'schemaVersion': 1, 'origin': 'regression-fixtures', 'seed': 'fixed', 'entries': entries}
        selection = {'entries': entries[:120]}; result = {'corpusSha256': b.corpus_hash(corpus), 'rows': replay}
        a = capture.prepare(corpus, result, selection); z = capture.prepare(corpus, result, selection)
        self.assertEqual(a, z); self.assertEqual(150, len(a['releases']))
        self.assertEqual(120, sum(r['originalReview'] for r in a['releases']))
        self.assertEqual(2, sum(r['manual'] for r in a['releases']))


    def test_explicit_retry_archives_incomplete_capture_and_consumes_quota(self):
        q = capture.spec('tmdb', 'movie', 'Public Film'); p = plan_for([q]); c = enabled(); c['maxPages'] = 1
        self.collect(p, c, fake=lambda *a: {'status': 'ok', 'body': response_for(q, more=True)})
        result = capture.collect(p, c, self.root / 'captures', True,
            transport=lambda *a: {'status': 'ok', 'body': response_for(q, empty=True)},
            environ=env_for(c), sleep=lambda n: None, quota_path=self.root / 'quota.json', retry_incomplete=True)
        self.assertEqual(1, result['httpAttempts'])
        self.assertEqual(1, len(list((self.root / 'captures/history').glob('*.json'))))
        records, gaps = capture.load_hits(p, self.root / 'captures', [capture.key(q)], allow_synthetic=True)
        self.assertFalse(gaps); self.assertEqual([], records[0]['Hits'])
        self.assertEqual(2, sum(v for k, v in b.load(self.root / 'quota.json').items() if not k.startswith('cooldown:')))

    def test_quota_lock_blocks_concurrent_collector(self):
        (self.root / 'quota.lock').write_text('')
        with self.assertRaises(ValueError): self.collect(plan_for([capture.spec('tvmaze', 'series', 'Public')]))


    def test_image_presence_preserves_historical_null_coalescing_without_downloading(self):
        q = capture.spec('rawg', 'game', 'Public')
        data = response_for(q); data['results'][0]['background_image'] = '   '
        self.assertFalse(capture.project_response(q, data)[0][0]['HasPoster'])
        q = capture.spec('tvmaze', 'series', 'Public')
        data = response_for(q); data[0]['show']['image'] = {'original': '', 'medium': 'https://not-downloaded.invalid/image'}
        self.assertFalse(capture.project_response(q, data)[0][0]['HasPoster'])


if __name__ == '__main__': unittest.main()
