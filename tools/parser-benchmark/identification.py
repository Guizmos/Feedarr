#!/usr/bin/env python3
"""Offline identification replay. No provider requests, API host, or database writes."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

import benchmark as bench
import export_sqlite as exporter

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def historical_source(source):
    """Copy actual straight-line query/decision logic, fail closed on source drift.

    Network calls in the query method hit a typed in-memory sink. The decision
    block calls actual scoring/policy functions; only its logging loop is omitted.
    """
    start = source.index('    private async Task<List<TmdbClient.SearchResult>> GetTmdbCandidatesAsync(')
    end = source.index('    private static List<(TmdbClient.SearchResult Candidate', start)
    query = source[start:end]
    start = source.index('        var tmdbStrongThreshold = PosterProviderSelector.GetTmdbStrongThreshold(ambiguity);')
    end_text = '        var tmdbMatchForIds = (Candidate: idsCandidate.Candidate, Score: idsCandidate.Score);'
    end = source.index(end_text, start) + len(end_text)
    decision = source[start:end]
    a, z = decision.index('        foreach (var rejected'), decision.index('        var accepted =')
    decision = decision[:a] + decision[z:]
    if any(text in decision for text in ('await ', '_logger', '_releases', 'File.')):
        raise ValueError('historical_decision_no_longer_pure')
    if query.count('_tmdb.Search') != 10 or query.count('RunTmdbAsync') != 10:
        raise ValueError('historical_query_contract_changed')
    return '''using Feedarr.Api.Models;
using Feedarr.Api.Services.Matching;
using Feedarr.Api.Services.Posters;
using Feedarr.Api.Services.Tmdb;
internal sealed partial class HistoricalAdapter
{
''' + query + '''
    internal static object Assess(string title, int? year, UnifiedCategory unifiedCategory,
        TitleAmbiguityResult ambiguity, List<TmdbClient.SearchResult> tmdbCandidates, HistoricalDecisionContext context)
    {
''' + decision + '''
        return new {
            decision = accepted.Count == 0 ? "Reject" : "Accept",
            selectedId = tmdbMatchForIds.Candidate?.TmdbId,
            selectedMediaType = tmdbMatchForIds.Candidate?.MediaType,
            selectedHasPoster = tmdbMatch.Candidate is not null,
            assessments = assessed.Select(row => new { id = row.Candidate.TmdbId,
                type = row.Candidate.MediaType, score = row.Score, decision = row.Decision })
        };
    }
}
'''


def validate_responses(value, corpus):
    if not isinstance(value, dict) or set(value) != {'origin', 'responses'} or value['origin'] != 'synthetic-fixture':
        raise ValueError('invalid_response_container')
    rows = value['responses']
    if not isinstance(rows, dict) or not set(rows) <= {e['id'] for e in corpus['entries']} or bench.sensitive_document(value):
        raise ValueError('unknown_or_sensitive_response')
    def identity(x):
        if not isinstance(x, dict) or set(x) != {'Provider', 'MediaType', 'Id'}:
            raise ValueError('invalid_response_identity')
        if x['Provider'] not in exporter.PROVIDERS or x['MediaType'] not in {'Movie', 'Series', 'Game', 'Audio', 'Book', 'Comic'} or not re.fullmatch(r'[0-9]{1,12}', str(x['Id'])):
            raise ValueError('invalid_response_identity')
    for hits in rows.values():
        if not isinstance(hits, list) or len(hits) > 100:
            raise ValueError('response_limit')
        for item in hits:
            if not isinstance(item, dict) or set(item) != {'Hit', 'HasPoster'} or type(item['HasPoster']) is not bool:
                raise ValueError('invalid_recorded_hit')
            hit = item['Hit']
            if not isinstance(hit, dict) or not {'Identity', 'Title'} <= set(hit) or set(hit) - {
                    'Identity', 'Title', 'Year', 'AlternativeTitles', 'Category', 'Season', 'Episode', 'KnownExternalIdentities'}:
                raise ValueError('invalid_hit_fields')
            identity(hit['Identity'])
            if not isinstance(hit['Title'], str) or not 1 <= len(hit['Title']) <= 1000:
                raise ValueError('invalid_hit_title')
            for key in ['Year', 'Season', 'Episode']:
                if hit.get(key) is not None and (type(hit[key]) is not int or not 0 <= hit[key] <= 9999):
                    raise ValueError('invalid_hit_number')
            if hit.get('Category') is not None and hit['Category'] not in exporter.CATEGORIES:
                raise ValueError('invalid_hit_category')
            aliases = hit.get('AlternativeTitles') or []
            if not isinstance(aliases, list) or len(aliases) > 50 or any(not isinstance(t, str) or len(t) > 1000 for t in aliases):
                raise ValueError('invalid_hit_alias')
            known = hit.get('KnownExternalIdentities') or []
            if not isinstance(known, list) or len(known) > 20:
                raise ValueError('invalid_external_identities')
            for item in known: identity(item)
    return value


def build_runner(directory):
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    project = HERE / 'IdentificationRunner/IdentificationRunner.csproj'
    source = (ROOT / 'src/Feedarr.Api/Services/Posters/PosterFetchService.cs').read_text()
    generated = directory / 'HistoricalPlan.cs'
    import capture_history
    generated.write_text('using System.Text.Json.Serialization;\n' + historical_source(source) + capture_history.generated())
    prop = f'-p:HistoricalPlanSource={generated}'
    commands = [
        ['dotnet', 'restore', str(project), '--configfile', str(HERE / 'Runner/NuGet.Config'), '-p:NuGetAudit=false', prop],
        ['dotnet', 'build', str(project), '-c', 'Release', '--no-restore', '--output', str(directory / 'bin'), prop],
    ]
    for command in commands:
        result = subprocess.run(command, capture_output=True, timeout=240)
        bench.write_text(directory / ('restore.log' if command[1] == 'restore' else 'build.log'), result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace'))
        if result.returncode:
            # Build diagnostics only in ignored private local output; never print input.
            bench.write_text(directory / 'build-error.log', result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace'))
            raise ValueError('offline_identification_build_failed')
    return directory / 'bin/Feedarr.Api.Tests.dll'


def run(corpus, out, response_document=None):
    corpus = bench.validate_corpus(corpus)
    out = bench.local_output(out)
    out.mkdir(mode=0o700, parents=True, exist_ok=True)
    sources = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
               for folder in ['src/Feedarr.Api/Services/Title', 'src/Feedarr.Api/Services/Matching']
               for p in (ROOT / folder).glob('*.cs')}
    for name in ['src/Feedarr.Api/Services/Posters/PosterProviderSelector.cs', 'src/Feedarr.Api/Services/Categories/UnifiedCategoryMappings.cs', 'src/Feedarr.Api/Models/UnifiedCategory.cs']:
        sources[name] = hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
    for file in [HERE / 'identification.py', HERE / 'export_sqlite.py', HERE / 'benchmark.py', *sorted((HERE / 'IdentificationRunner').glob('*.cs')), HERE / 'IdentificationRunner/IdentificationRunner.csproj']:
        sources[str(file.relative_to(ROOT))] = hashlib.sha256(file.read_bytes()).hexdigest()
    historical_file = ROOT / 'src/Feedarr.Api/Services/Posters/PosterFetchService.cs'
    sources[str(historical_file.relative_to(ROOT))] = hashlib.sha256(historical_file.read_bytes()).hexdigest()
    payload = {'corpus': corpus}
    if response_document is not None:
        response_document = validate_responses(response_document, corpus)
        payload['responses'] = response_document['responses']
    bench.write(out / 'input.json', payload)
    # Deliberately outside the API host, no appsettings/configuration is read by runner.
    dll = build_runner(out / 'build')
    result = subprocess.run(['dotnet', str(dll), str(out / 'input.json')], capture_output=True, timeout=300)
    if result.returncode:
        bench.write_text(out / 'runner-error.log', result.stderr.decode(errors='replace'))
        raise ValueError('offline_replay_failed')
    parsed = json.loads(result.stdout)
    result_rows = parsed['rows']
    if {r['id'] for r in result_rows} != {e['id'] for e in corpus['entries']} or len(result_rows) != len(corpus['entries']):
        raise ValueError('replay_population_mismatch')
    for name, expected in sources.items():
        if hashlib.sha256((ROOT / name).read_bytes()).hexdigest() != expected:
            raise ValueError('replay_sources_changed')
    metadata = {'corpusSha256': bench.corpus_hash(corpus), 'sources': sources,
                'responsesOrigin': response_document['origin'] if response_document else 'unavailable'}
    bench.write(out / 'results.json', {**metadata, 'rows': result_rows})
    bench.write(out / 'performance.json', {**metadata, **parsed['performance']})
    return result_rows


def summarize(corpus, rows):
    entries = {e['id']: e for e in corpus['entries']}
    summary = {'population': corpus['selection'].get('populationTotal'), 'admissible': len(rows),
               'exclusions': corpus['selection']['excluded'], 'categories': {},
               'rankingAvailability': dict(collections.Counter(r['rankingAvailability'] for r in rows)),
               'providerIdentificationAccuracy': None, 'providerGain': None,
               'identificationDecisions': {'accept': None, 'ambiguous': None, 'reject': None},
               'actualProviderResponses': 0,
               'syntheticFixtureBatches': sum(r['rankingAvailability'] == 'synthetic-fixture-evaluated' for r in rows)}
    for category in sorted({e['category'] for e in entries.values()}):
        group = [r for r in rows if entries[r['id']]['category'] == category]
        summary['categories'][category] = {
            'count': len(group), 'withPoster': sum(entries[r['id']]['hasPoster'] for r in group),
            'guardedCandidateCoverage': sum(bool(r['proposals']) for r in group),
            'guardedCandidates': sum(len(r['proposals']) for r in group),
            'shadowVariants': sum(len(r['shadowProposals']) > 1 for r in group),
            'historicalAmbiguous': sum(r['historical']['ambiguity']['IsAmbiguous'] for r in group),
            'storedTitleDifferences': sum(r['titleChanged'] for r in group),
            'storedYearDifferences': sum(r['yearChanged'] for r in group),
            'protectedStoredIds': sum(r['hasStoredId'] for r in group),
            'manualProtected': sum(r['manual'] for r in group),
            'historicalPlanQueries': sum(len(r['historical']['queryPlan']) for r in group),
            'primaryQueryTextDifferences': sum(bool(r['shadowProposals']) and r['shadowProposals'][0]['Title'] != r['historical']['primaryQueryTitle'] for r in group),
        }
    summary['replacementProposals'] = sum(bool(r['ranking'] and r['ranking']['SelectedIdentity']) and (r['hasStoredId'] or r['manual']) for r in rows)
    summary['protectedIdentityTextChanges'] = sum(r['hasStoredId'] and (r['titleChanged'] or r['yearChanged']) for r in rows)
    summary['unknownTmdbScope'] = sum(bool(e.get('association', {}).get('tmdbId')) and not e['association'].get('tmdbType') for e in entries.values())
    return summary


def review_template(corpus, rows, selection, prior_references=None):
    selected = [e.get('input', e) for e in selection['entries']]
    ids = {e['id'] for e in selected}
    entries = {e['id']: e for e in corpus['entries']}
    replay = {r['id']: r for r in rows}
    if not ids <= set(replay): raise ValueError('review_selection_no_longer_admissible')
    if any(e.get('raw', entries[e['id']]['raw']) != entries[e['id']]['raw'] for e in selected):
        raise ValueError('review_source_title_changed')
    prior = {e['id']: e for e in (prior_references or {}).get('entries', [])}
    return {'corpusSha256': bench.corpus_hash(corpus), 'entries': [
        {'id': key, 'raw': entries[key]['raw'], 'storedObservation': entries[key].get('stored'),
         'associationObservation': entries[key]['association'], 'replayed': replay[key]['parsed'],
         'proposals': replay[key]['shadowProposals'],
         'parsing': {'status': prior.get(key, {}).get('status', 'unreviewed'),
                     'expected': prior.get(key, {}).get('parserExpected', {}),
                     'evidence': prior.get(key, {}).get('evidence', '')},
         'providerIdentity': {'status': 'unreviewed', 'expected': {}, 'evidence': ''},
         'legitimateAmbiguity': {'status': 'unreviewed'}, 'wrongAssociation': {'status': 'unreviewed'}}
        for key in sorted(ids)]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--corpus', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--review-selection', help='Existing 120-case review selection')
    parser.add_argument('--parser-references', help='Existing partial lexical annotations, never provider truth')
    parser.add_argument('--responses', help='Explicit synthetic adapter fixtures only; real capture provenance is not implemented yet')
    parser.add_argument('--synthetic-fixtures', action='store_true', help='Allow expressly synthetic responses for tool tests, never for accuracy')
    args = parser.parse_args()
    try:
        corpus = bench.validate_corpus(bench.load(args.corpus))
        responses = bench.load(args.responses) if args.responses else None
        if responses and responses.get('origin') == 'synthetic-fixture' and not args.synthetic_fixtures:
            raise ValueError('synthetic_responses_require_explicit_fixture_mode')
        rows = run(corpus, Path(args.out), responses)
        summary = summarize(corpus, rows)
        if args.review_selection:
            review = review_template(corpus, rows, bench.load(args.review_selection),
                                     bench.load(args.parser_references) if args.parser_references else None)
            bench.write(Path(args.out) / 'manual-review-120.json', review)
            summary['review'] = {'total': len(review['entries']), 'partialLexicalValidated': sum(e['parsing']['status'] == 'validated' for e in review['entries']), 'providerIdentityValidated': 0}
        bench.write(Path(args.out) / 'summary.json', summary)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print('Offline identification replay refused; check private logs, schema, hashes and local SDK.', file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
