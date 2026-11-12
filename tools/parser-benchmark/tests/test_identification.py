import copy
import hashlib
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import benchmark as bench
import export_sqlite as exporter
import identification as identification


def entry(index, manual=False):
    return {'id': f'fixture-{index}', 'raw': 'A.Distinct.Film.2020.1080p.WEB-DL.x264-TEAM',
            'category': 'Film', 'stored': {'TitleClean': 'A Distinct Film', 'Year': 2020},
            'association': {'tmdbId': None, 'tmdbType': None, 'tvdbId': None,
                            'provider': None, 'providerId': None, 'manualMatch': manual},
            'hasPoster': False, 'sourceGroup': 'source-fixture', 'ageBand': 'recent', 'complexity': 'simple'}


def corpus():
    return {'schemaVersion': 1, 'origin': 'regression-fixtures', 'seed': 'synthetic',
            'selection': {'populationTotal': 5, 'allAdmissible': True, 'excluded': {}},
            'entries': [entry(1), entry(2), entry(3, True), entry(4), entry(5)]}


def hit(identity='1', media='Movie'):
    return {'Hit': {'Identity': {'Provider': 'tmdb', 'MediaType': media, 'Id': identity},
                    'Title': 'A Distinct Film', 'Year': 2020}, 'HasPoster': True}


class IdentificationTests(unittest.TestCase):
    def test_all_admissible_does_not_sample_at_2000(self):
        with sqlite3.connect(':memory:') as connection:
            connection.row_factory = sqlite3.Row
            connection.execute('CREATE TABLE releases(id INTEGER,source_id INTEGER,title TEXT,unified_category TEXT,poster_file TEXT)')
            connection.executemany('INSERT INTO releases VALUES(?,?,?,?,?)',
                                   [(i, 1, f'Public Title {i}', 'Film', None) for i in range(2101)])
            connection.commit()
            result = exporter.export(connection, all_admissible=True)
            self.assertEqual(2101, len(result['entries']))
            self.assertEqual(2101, result['selection']['populationTotal'])
            bench.validate_corpus(result)

    def test_complete_export_refuses_silent_truncation(self):
        with sqlite3.connect(':memory:') as connection:
            connection.row_factory = sqlite3.Row
            connection.execute('CREATE TABLE releases(id INTEGER,source_id INTEGER,title TEXT,unified_category TEXT,poster_file TEXT)')
            connection.executemany('INSERT INTO releases VALUES(?,?,?,?,?)', [(i, 1, 'Public Work', 'Film', None) for i in range(10001)])
            connection.commit()
            with self.assertRaises(ValueError): exporter.export(connection, all_admissible=True)

    def test_actual_historical_source_is_used_and_drift_fails_closed(self):
        source = (bench.ROOT / 'src/Feedarr.Api/Services/Posters/PosterFetchService.cs').read_text()
        generated = identification.historical_source(source)
        self.assertIn('_tmdb.SearchTvListAsync(title, year, innerCt, TmdbCandidateLimit)', generated)
        self.assertIn('TmdbMatchPolicy.Evaluate(title, year,', generated)
        self.assertNotIn('_logger', generated)
        with self.assertRaises(ValueError): identification.historical_source(source.replace('_tmdb.SearchMovieListAsync', '_tmdb.Other'))

    def test_responses_without_explicit_fixture_provenance_or_with_secrets_are_refused(self):
        c = corpus()
        for value in [{'origin': 'recorded-offline', 'responses': {}},
                      {'origin': 'synthetic-fixture', 'responses': {'unknown-id': []}},
                      {'origin': 'synthetic-fixture', 'responses': {'fixture-1': [hit() | {'token': 'private'}]}},
                      {'origin': 'synthetic-fixture', 'responses': {'fixture-1': [hit() | {'HasPoster': 'yes'}]}}]:
            with self.subTest(value=value), self.assertRaises(ValueError): identification.validate_responses(value, c)

    def test_review_template_never_turns_observations_into_identity_truth(self):
        c = corpus(); rows = [{'id': e['id'], 'parsed': {}, 'shadowProposals': []} for e in c['entries']]
        refs = {'entries': [{'id': 'fixture-1', 'status': 'validated', 'parserExpected': {'TitleClean': 'A Distinct Film'}}]}
        result = identification.review_template(c, rows, c, refs)
        self.assertEqual('validated', result['entries'][0]['parsing']['status'])
        self.assertTrue(all(e['providerIdentity']['status'] == 'unreviewed' for e in result['entries']))
        self.assertTrue(all(e['providerIdentity']['expected'] == {} for e in result['entries']))

    def test_offline_adapters_distinguish_missing_empty_close_and_locked_results(self):
        c = corpus(); original = copy.deepcopy(c)
        responses = {'origin': 'synthetic-fixture', 'responses': {
            'fixture-1': [hit('1'), hit('2')], 'fixture-2': [hit('1', 'Series')], 'fixture-5': []}}
        with tempfile.TemporaryDirectory(dir=HERE / 'local') as directory:
            rows = identification.run(c, Path(directory), responses)
            r = {row['id']: row for row in rows}
            self.assertEqual('Ambiguous', r['fixture-1']['ranking']['Decision'])
            self.assertEqual('Accept', r['fixture-1']['historical']['decision']['decision'])
            self.assertEqual('Reject', r['fixture-2']['ranking']['Decision'])
            self.assertEqual('Reject', r['fixture-2']['historical']['decision']['decision'])
            self.assertTrue(r['fixture-3']['ranking']['PreserveExisting'])
            self.assertIsNone(r['fixture-4']['ranking'])
            self.assertEqual('provider-responses-unavailable', r['fixture-4']['rankingAvailability'])
            self.assertEqual('Reject', r['fixture-5']['ranking']['Decision'])
            self.assertEqual(4, len(r['fixture-1']['historical']['queryPlan']))
            self.assertTrue(all(q['Limit'] == 10 for q in r['fixture-1']['historical']['queryPlan']))
            summary = identification.summarize(c, rows)
            self.assertEqual(0, summary['actualProviderResponses'])
            self.assertEqual(3, summary['syntheticFixtureBatches'])
        self.assertEqual(original, c)


if __name__ == '__main__': unittest.main()
