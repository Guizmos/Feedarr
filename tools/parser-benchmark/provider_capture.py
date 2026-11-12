#!/usr/bin/env python3
"""Explicit opt-in provider capture. No SQLite/configuration discovery or image downloads."""
import argparse
import collections
import datetime as dt
import hashlib
import email.utils
import math
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

import benchmark as b
import identification as identification

HERE = Path(__file__).resolve().parent
PROVIDERS = {'tmdb', 'tvmaze', 'igdb', 'rawg'}
CONTRACT = 'feedarr-provider-capture-v1'
CATEGORIES = {'Film', 'Serie', 'Animation', 'Emission', 'Spectacle', 'JeuWindows'}


def utc(): return dt.datetime.now(dt.timezone.utc).isoformat()


def local(path):
    path = Path(path).resolve()
    if not (HERE / 'local').resolve() in path.parents:
        raise ValueError('private_path_must_be_under_ignored_local')
    return path


def fingerprint():
    files = [*(b.ROOT / 'src/Feedarr.Api/Services/Title').glob('*.cs'),
             *(b.ROOT / 'src/Feedarr.Api/Services/Matching').glob('*.cs'),
             b.ROOT / 'src/Feedarr.Api/Services/Posters/PosterFetchService.cs',
             b.ROOT / 'src/Feedarr.Api/Services/Posters/PosterProviderSelector.cs',
             *(b.ROOT / 'src/Feedarr.Api/Services/Tmdb').glob('*.cs'),
             b.ROOT / 'src/Feedarr.Api/Services/TvMaze/TvMazeClient.cs',
             b.ROOT / 'src/Feedarr.Api/Services/Igdb/IgdbClient.cs',
             b.ROOT / 'src/Feedarr.Api/Services/Rawg/RawgClient.cs']
    return b.digest({str(p.relative_to(b.ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(files)})


def spec(provider, media, query, year=None, *, language=None, page_size=20, cover=False):
    if provider not in PROVIDERS or media not in ({'movie', 'series'} if provider == 'tmdb' else {'series'} if provider == 'tvmaze' else {'game'}):
        raise ValueError('invalid_provider_media_scope')
    if not isinstance(query, str) or type(page_size) is not int or not 1 <= page_size <= 100 or type(cover) is not bool:
        raise ValueError('invalid_request_fields')
    if year is not None and type(year) is not int: raise ValueError('invalid_year')
    if language is not None and (not isinstance(language, str) or not re.fullmatch(r'[a-z]{2}-[A-Z]{2}', language)): raise ValueError('invalid_language')
    query = query.strip()[:200] if provider in {'tmdb', 'tvmaze', 'igdb'} else query
    if provider == 'igdb': query = query.replace('\r', ' ').replace('\n', ' ')
    if not query or b.sensitive_document(query): raise ValueError('unsafe_query')
    # TVMaze and RAWG search do not filter by year. Keep year evidence in the
    # engine input, never mislabel it as a server filter in capture provenance.
    if provider in {'tvmaze', 'rawg'}: year = None
    if provider == 'tmdb' and year is not None and not 1800 <= year <= 2100: year = None
    if provider == 'igdb' and year is not None and not 1970 <= year <= 2100: year = None
    return {'provider': provider, 'mediaType': media, 'query': query, 'yearUsed': year,
            'language': language if provider == 'tmdb' else None,
            'pageSize': page_size, 'coverRequired': cover if provider == 'igdb' else False}


def key(request): return b.digest(request)


def tool_version():
    paths = [Path(__file__), HERE / 'capture_history.py', HERE / 'identification.py',
             *sorted((HERE / 'IdentificationRunner').glob('*.cs'))]
    return b.digest({str(p.relative_to(b.ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths})


def prepare(corpus, replay, selection, languages=(None, 'fr-FR')):
    corpus = b.validate_corpus(corpus)
    if replay['corpusSha256'] != b.corpus_hash(corpus): raise ValueError('replay_corpus_mismatch')
    entries = {e['id']: e for e in corpus['entries']}
    rows = {r['id']: r for r in replay['rows']}
    old = [e.get('input', e)['id'] for e in selection['entries']]
    if len(set(old)) != 120 or not set(old) <= entries.keys(): raise ValueError('expected_original_120')
    chosen = set(old)
    # Reserve two of the 30 additional slots for persisted manual corrections;
    # they remain diagnostic-only and never become expected identities.
    manual = sorted((i for i, r in rows.items() if i not in chosen and r['manual']), key=lambda i: b.digest(['manual-control', i]))
    chosen.update(manual[:2])
    buckets = collections.defaultdict(list)
    for identifier, entry in entries.items():
        if identifier in chosen: continue
        r = rows[identifier]
        stratum = (entry['category'], entry['hasPoster'], r['titleChanged'] or r['yearChanged'], r['historical']['ambiguity']['IsAmbiguous'])
        buckets[stratum].append(identifier)
    for values in buckets.values(): values.sort(key=lambda x: b.digest(['v23d-150', x]))
    strata = sorted(buckets, key=lambda x: b.digest(list(x)))
    while len(chosen) < 150 and strata:
        for group in list(strata):
            chosen.add(buckets[group].pop())
            if not buckets[group]: strata.remove(group)
            if len(chosen) == 150: break
    jobs = {}; releases = []
    for identifier in sorted(chosen):
        entry, r = entries[identifier], rows[identifier]
        historical = []; modern = []
        def add(target, request):
            k = key(request); jobs[k] = request
            if k not in target: target.append(k)
        for q in r['historical']['queryPlan']:
            provider, media, title, year = q['Provider'], q['MediaType'], q['Title'], q['Year']
            for language in languages if provider == 'tmdb' else [None]:
                add(historical, spec(provider, media, title, year, language=language,
                    page_size=5 if provider in {'igdb', 'rawg'} else 20, cover=provider == 'igdb'))
        ambiguity = r['historical']['ambiguity']
        if entry['category'] == 'Emission' or (entry['category'] == 'Serie' and ambiguity['SignificantTokenCount'] >= 2
                and (not ambiguity['IsCommonTitle'] or r['historical']['year'] is not None)):
            add(historical, spec('tvmaze', 'series', r['historical']['primaryQueryTitle']))
        for q in r['shadowProposals']:
            if entry['category'] == 'JeuWindows':
                for provider in ['igdb', 'rawg']:
                    for year in dict.fromkeys([q['Year'], None]):
                        add(modern, spec(provider, 'game', q['Title'], year))
            else:
                media = 'series' if entry['category'] in {'Serie', 'Emission'} else 'movie'
                if media == 'series': add(modern, spec('tvmaze', media, q['Title']))
                for year in dict.fromkeys([q['Year'], None]):
                    for language in languages:
                        add(modern, spec('tmdb', media, q['Title'], year, language=language))
        releases.append({'id': identifier, 'raw': entry['raw'], 'category': entry['category'], 'hasPoster': entry['hasPoster'],
            'originalReview': identifier in old, 'protected': r['hasStoredId'] or r['manual'],
            'manual': r['manual'], 'existingObservation': entry['association'],
            'historicalTitle': r['historical']['title'], 'historicalYear': r['historical']['year'],
            'historicalSeason': entry['stored'].get('Season'), 'historicalEpisode': entry['stored'].get('Episode'),
            'queries': r['shadowProposals'], 'parsed': r['parsed'],
            'historicalRequests': historical, 'v2Requests': modern})
    plan = {'contract': CONTRACT, 'corpusSha256': b.corpus_hash(corpus), 'sourceVersion': fingerprint(),
            'codeCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=b.ROOT).decode().strip(),
            'codeState': 'local-v23-sources-pinned-by-hash', 'captureToolVersion': tool_version(), 'languages': list(languages),
            'releases': releases, 'requests': jobs}
    return plan


def config_template():
    return {'version': 1, 'maxRequestsTotal': 500, 'maxPages': 3,
        'providers': {p: {'enabled': False, 'termsReviewed': False, 'maxRequestsPerDay': 100,
            'minIntervalSeconds': 1.0, **({'credentialEnv': {'bearer': 'FEEDARR_CAPTURE_TMDB_BEARER'}} if p == 'tmdb' else
            {'credentialEnv': {'clientId': 'FEEDARR_CAPTURE_IGDB_CLIENT_ID', 'bearer': 'FEEDARR_CAPTURE_IGDB_BEARER'}} if p == 'igdb' else
            {'credentialEnv': {'apiKey': 'FEEDARR_CAPTURE_RAWG_KEY'}} if p == 'rawg' else {'credentialEnv': {}})} for p in sorted(PROVIDERS)}}


def load_config(path):
    path = local(path)
    if os.name == 'posix' and path.stat().st_mode & 0o077: raise ValueError('config_permissions_must_be_0600')
    value = b.load(path)
    if set(value) != {'version', 'maxRequestsTotal', 'maxPages', 'providers'} or value['version'] != 1:
        raise ValueError('invalid_capture_config')
    if type(value['maxRequestsTotal']) is not int or not 1 <= value['maxRequestsTotal'] <= 5000 or type(value['maxPages']) is not int or not 1 <= value['maxPages'] <= 10:
        raise ValueError('invalid_capture_budget')
    if set(value['providers']) != PROVIDERS: raise ValueError('explicit_provider_config_required')
    for p, setting in value['providers'].items():
        if set(setting) != {'enabled', 'termsReviewed', 'maxRequestsPerDay', 'minIntervalSeconds', 'credentialEnv'}:
            raise ValueError('invalid_provider_setting')
        if type(setting['enabled']) is not bool or type(setting['termsReviewed']) is not bool:
            raise ValueError('invalid_provider_opt_in')
        if type(setting['maxRequestsPerDay']) is not int or not 1 <= setting['maxRequestsPerDay'] <= 5000:
            raise ValueError('invalid_daily_budget')
        if type(setting['minIntervalSeconds']) not in (int, float) or not 1 <= setting['minIntervalSeconds'] <= 60:
            raise ValueError('interval_must_be_1_to_60_seconds')
        required = {'bearer'} if p == 'tmdb' else {'clientId', 'bearer'} if p == 'igdb' else {'apiKey'} if p == 'rawg' else set()
        if set(setting['credentialEnv']) != required or any(not re.fullmatch(r'[A-Z][A-Z0-9_]{2,100}', name) for name in setting['credentialEnv'].values()):
            raise ValueError('credentials_must_be_explicit_environment_references')
    return value


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl): return None


def request_details(q, page, credentials):
    provider = q['provider']; params = {}; headers = {'User-Agent': 'Feedarr-offline-validation/1', 'Accept': 'application/json'}; body = None
    if provider == 'tmdb':
        kind = 'movie' if q['mediaType'] == 'movie' else 'tv'
        endpoint = f'https://api.themoviedb.org/3/search/{kind}'
        params = {'query': q['query'], 'page': page, 'include_adult': 'false'}
        if q['language']: params['language'] = q['language']
        if q['yearUsed'] is not None: params['year' if kind == 'movie' else 'first_air_date_year'] = q['yearUsed']
        headers['Authorization'] = 'Bearer ' + credentials['bearer']
    elif provider == 'tvmaze':
        endpoint = 'https://api.tvmaze.com/search/shows'; params = {'q': q['query']}
    elif provider == 'rawg':
        endpoint = 'https://api.rawg.io/api/games'; params = {'key': credentials['apiKey'], 'search': q['query'], 'page': page, 'page_size': q['pageSize']}
    elif provider == 'igdb':
        endpoint = 'https://api.igdb.com/v4/games'
        query = q['query'].replace('\\', '\\\\').replace('"', '\\"').replace('\r', ' ').replace('\n', ' ')
        where = []
        if q['coverRequired']: where.append('cover != null')
        if q['yearUsed'] is not None:
            year = q['yearUsed']; start = int(dt.datetime(year, 1, 1, tzinfo=dt.timezone.utc).timestamp()); end = int(dt.datetime(year + 1, 1, 1, tzinfo=dt.timezone.utc).timestamp())
            where += [f'first_release_date >= {start}', f'first_release_date < {end}']
        body = (f'search "{query}"; fields id,name,first_release_date,cover.url,alternative_names.name; '
                + ('where ' + ' & '.join(where) + '; ' if where else '')
                + f'limit {q["pageSize"]}; offset {(page - 1) * q["pageSize"]};').encode()
        headers.update({'Client-ID': credentials['clientId'], 'Authorization': 'Bearer ' + credentials['bearer'], 'Content-Type': 'text/plain'})
    else: raise ValueError('unsupported_provider')
    return endpoint, params, headers, body


def http_transport(q, page, credentials):
    endpoint, params, headers, body = request_details(q, page, credentials)
    # Fixed HTTPS origins only; ignore ambient proxies and refuse redirects. Never
    # persist the URL, headers, credential-bearing query string or exception text.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    request = urllib.request.Request(endpoint + ('?' + urllib.parse.urlencode(params) if params else ''), data=body, headers=headers)
    try:
        with opener.open(request, timeout=30) as response:
            raw = response.read(4_000_001)
            if len(raw) > 4_000_000: return {'status': 'oversized-response'}
            return {'status': 'ok', 'body': json.loads(raw), 'bodySha256': hashlib.sha256(raw).hexdigest()}
    except urllib.error.HTTPError as error:
        retry = error.headers.get('Retry-After', '')
        try:
            delay = int(retry) if retry.isdigit() else max(0, math.ceil((email.utils.parsedate_to_datetime(retry) - dt.datetime.now(dt.timezone.utc)).total_seconds()))
        except (ValueError, TypeError): delay = 60
        return {'status': 'http-error', 'httpStatus': error.code, 'retryAfterSeconds': delay}
    except (OSError, ValueError): return {'status': 'transport-or-json-error'}


def project_response(q, body):
    provider = q['provider']
    if provider in {'tmdb', 'rawg'}:
        if not isinstance(body, dict) or not isinstance(body.get('results'), list): raise ValueError('invalid_provider_schema')
        records = body['results']
        if provider == 'tmdb':
            if type(body.get('total_pages')) is not int or type(body.get('page')) is not int: raise ValueError('missing_pagination')
            more = body['page'] < body['total_pages']
        else:
            if 'next' not in body: raise ValueError('missing_pagination')
            more = bool(body['next'])  # Never follow or persist provider-supplied URLs.
    else:
        if not isinstance(body, list): raise ValueError('invalid_provider_schema')
        records = body; more = False if provider == 'tvmaze' else len(records) == q['pageSize']
    hits = []
    for record in records:
        item = record.get('show') if provider == 'tvmaze' and isinstance(record, dict) else record
        if not isinstance(item, dict) or type(item.get('id')) is not int or item['id'] <= 0: raise ValueError('invalid_provider_identity')
        if provider == 'tmdb':
            first, second = ('title', 'name') if q['mediaType'] == 'movie' else ('name', 'title')
            title = item.get(first) if item.get(first) is not None else item.get(second)
        else: title = item.get('name')
        if not isinstance(title, str) or not title.strip(): raise ValueError('invalid_provider_title')
        released = item.get('release_date') or item.get('first_air_date') if provider == 'tmdb' else item.get('premiered') if provider == 'tvmaze' else item.get('released')
        if released is not None and not isinstance(released, str): raise ValueError('invalid_release_date')
        if provider == 'igdb' and item.get('first_release_date') is not None and type(item['first_release_date']) is not int: raise ValueError('invalid_release_timestamp')
        year = None
        if provider == 'igdb' and item.get('first_release_date'):
            year = dt.datetime.fromtimestamp(item['first_release_date'], dt.timezone.utc).year
        elif isinstance(released, str) and re.match(r'^\d{4}', released): year = int(released[:4])
        first, second = ('original_name', 'original_title') if q['mediaType'] == 'series' else ('original_title', 'original_name')
        original = item.get(first) if item.get(first) is not None else item.get(second)
        aliases = [original.strip()] if isinstance(original, str) and original.strip() and original.strip() != title.strip() else []
        if provider == 'igdb': aliases += [a['name'] for a in item.get('alternative_names', []) if isinstance(a, dict) and isinstance(a.get('name'), str)]
        if provider == 'tmdb': image = item.get('poster_path')
        elif provider == 'tvmaze':
            images = item.get('image') or {}
            image = images.get('original') if images.get('original') is not None else images.get('medium')
        elif provider == 'igdb': image = (item.get('cover') or {}).get('url')
        else: image = item.get('background_image')
        if image is not None and not isinstance(image, str): raise ValueError('invalid_image_metadata')
        has_poster = bool(image and image.strip())
        hits.append({'Hit': {'Identity': {'Provider': provider, 'MediaType': {'movie': 'Movie', 'series': 'Series', 'game': 'Game'}[q['mediaType']], 'Id': str(item['id'])},
                    'Title': title, 'Year': year, 'AlternativeTitles': aliases}, 'HasPoster': has_poster,
                    'Released': released, 'FirstReleaseUnix': item.get('first_release_date') if provider == 'igdb' else None,
                    'TvdbId': item.get('externals', {}).get('thetvdb') if provider == 'tvmaze' else None})
    if b.sensitive_document(hits): raise ValueError('sensitive_projection_refused')
    return hits, more


def verify_plan(plan):
    if plan.get('contract') != CONTRACT or plan['sourceVersion'] != fingerprint() or plan.get('captureToolVersion') != tool_version(): raise ValueError('stale_source_plan')
    if any(key(q) != identifier for identifier, q in plan['requests'].items()): raise ValueError('request_hash_mismatch')
    for row in plan['releases']:
        if (not row['historicalRequests'] and row['historicalTitle'].strip()) or not row['v2Requests'] or any(k not in plan['requests'] for k in row['historicalRequests'] + row['v2Requests']):
            raise ValueError('incomplete_request_plan')
    for q in plan['requests'].values():
        if q['provider'] not in PROVIDERS or q['mediaType'] not in {'movie', 'series', 'game'}: raise ValueError('invalid_request_scope')
        if spec(q['provider'], q['mediaType'], q['query'], q['yearUsed'], language=q['language'], page_size=q['pageSize'], cover=q['coverRequired']) != q: raise ValueError('noncanonical_request')
        if not 1 <= q['pageSize'] <= 100: raise ValueError('invalid_page_size')


def _collect(plan, config, out, execute=False, transport=None, environ=None, sleep=time.sleep, quota_path=None, retry_incomplete=False):
    verify_plan(plan); out = local(out); out.mkdir(mode=0o700, parents=True, exist_ok=True)
    transport = transport or http_transport; environ = os.environ if environ is None else environ
    ledger_file = local(quota_path or HERE / 'local/provider-capture-quota.json'); ledger = b.load(ledger_file) if ledger_file.exists() else {}
    day = dt.datetime.now(dt.timezone.utc).date().isoformat(); calls = 0; stopped = set(); status = collections.Counter()
    for identifier, q in plan['requests'].items():
        path = out / (identifier + '.json'); p = q['provider']; cfg = config['providers'][p]
        if path.exists():
            cached = b.load(path)
            if cached.get('sourceVersion') != plan['sourceVersion'] or cached.get('request') != q: raise ValueError('stale_capture')
            if cached.get('complete') or not retry_incomplete:
                status['cached'] += 1; continue
        if not execute or not cfg['enabled'] or not cfg['termsReviewed']:
            status['not-configured-or-not-executing'] += 1; continue
        credentials = {k: environ.get(v, '') for k, v in cfg['credentialEnv'].items()}
        if any(not v for v in credentials.values()):
            status['not-configured-or-not-executing'] += 1; continue
        budget_key = day + ':' + p
        until = ledger.get('cooldown:' + p)
        if until and dt.datetime.fromisoformat(until) > dt.datetime.now(dt.timezone.utc): stopped.add(p)
        if p in stopped or calls >= config['maxRequestsTotal'] or ledger.get(budget_key, 0) >= cfg['maxRequestsPerDay']:
            status['budget-or-provider-stop'] += 1; continue
        if path.exists():
            previous = b.load(path)
            b.write(out / 'history' / (identifier + '-' + b.digest(previous) + '.json'), previous)
        capture = {'contract': CONTRACT, 'origin': 'provider-http' if transport is http_transport else 'synthetic-test',
            'requestKey': identifier, 'request': q, 'sourceVersion': plan['sourceVersion'], 'captureToolVersion': plan['captureToolVersion'], 'codeCommit': plan['codeCommit'],
            'collectedAtUtc': utc(), 'pages': [], 'status': 'partial', 'complete': False}
        for page in range(1, config['maxPages'] + 1):
            if calls >= config['maxRequestsTotal'] or ledger.get(budget_key, 0) >= cfg['maxRequestsPerDay']: break
            # Persist quota consumption before each attempt; timeouts/errors count too.
            ledger[budget_key] = ledger.get(budget_key, 0) + 1; calls += 1; b.write(ledger_file, ledger)
            sleep(cfg['minIntervalSeconds'])
            response = transport(q, page, credentials)
            record = {'page': page, 'offset': (page - 1) * q['pageSize'] if p == 'igdb' else None,
                      'collectedAtUtc': utc(), 'status': response['status']}
            if response['status'] != 'ok':
                record['httpStatus'] = response.get('httpStatus'); record['retryAfterSeconds'] = response.get('retryAfterSeconds'); capture['pages'].append(record)
                capture['status'] = response['status']; stopped.add(p)
                if response.get('httpStatus') == 429:
                    delay = response.get('retryAfterSeconds', 60)
                    try: until = dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=delay)
                    except OverflowError: until = dt.datetime.max.replace(tzinfo=dt.timezone.utc)
                    ledger['cooldown:' + p] = until.isoformat()
                    b.write(ledger_file, ledger)
                break
            try:
                if p == 'tmdb' and response['body'].get('page') != page: raise ValueError('wrong_provider_page')
                hits, more = project_response(q, response['body'])
                text = json.dumps(hits)
                if any(v in text for v in credentials.values() if v): raise ValueError('credential_echo')
            except (ValueError, TypeError, KeyError, OverflowError):
                record['status'] = 'invalid-or-sensitive-response'; capture['pages'].append(record)
                capture['status'] = record['status']; stopped.add(p); break
            record.update(hits=hits, hasMore=more, responseSha256=response.get('bodySha256'))
            capture['pages'].append(record)
            if not more: capture.update(status='complete', complete=True); break
        capture['projectionSha256'] = b.digest(capture['pages'])
        b.write(path, capture); status[capture['status']] += 1
    return {'httpAttempts': calls, 'statuses': dict(status), 'networkExecuted': calls > 0, 'executionRequested': execute,
            'providerResultsInvented': False, 'quotaDayUtc': day}


def collect(plan, config, out, execute=False, transport=None, environ=None, sleep=time.sleep, quota_path=None, retry_incomplete=False):
    # One collector process per machine quota ledger. Separate output directories
    # cannot reset the configured per-provider daily budget.
    quota = local(quota_path or HERE / 'local/provider-capture-quota.json')
    lock = quota.with_suffix('.lock'); lock.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    try: descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError: raise ValueError('capture_already_running_or_stale_lock')
    try:
        os.close(descriptor)
        return _collect(plan, config, out, execute, transport, environ, sleep, quota, retry_incomplete)
    finally: lock.unlink()


def load_hits(plan, capture_dir, requests, allow_synthetic=False):
    result = []; gaps = []
    for identifier in requests:
        path = local(capture_dir) / (identifier + '.json')
        if not path.exists(): gaps.append({'request': identifier, 'reason': 'missing'}); continue
        c = b.load(path)
        if c.get('captureToolVersion') != plan['captureToolVersion'] or c.get('requestKey') != identifier or c.get('request') != plan['requests'][identifier] or c.get('sourceVersion') != plan['sourceVersion'] or c.get('projectionSha256') != b.digest(c['pages']):
            raise ValueError('capture_provenance_mismatch')
        if c.get('origin') != 'provider-http' and not allow_synthetic: raise ValueError('synthetic_capture_not_real_evidence')
        if not c.get('complete') or c.get('status') != 'complete' or not c['pages'] or any(p['status'] != 'ok' for p in c['pages']) or c['pages'][-1]['hasMore']:
            gaps.append({'request': identifier, 'reason': 'incomplete-or-error'}); continue
        if [p['page'] for p in c['pages']] != list(range(1, len(c['pages']) + 1)) or any(not p['hasMore'] for p in c['pages'][:-1]): raise ValueError('pagination_gap')
        result.append({'Provider': c['request']['provider'], 'RequestKey': identifier,
            'Hits': [h for page in c['pages'] for h in page['hits']], 'PageSize': c['request']['pageSize'],
            'CollectedAtUtc': c['collectedAtUtc'], 'CapturedPageCount': len(c['pages'])})
    return result, gaps


def reference_template(plan, existing):
    old = {r['id']: r for r in existing['entries']}
    if any(r.get('providerIdentity', {}).get('status') == 'validated' for r in old.values()):
        raise ValueError('existing_validated_identity_requires_explicit_reference_import')
    return {'planSha256': b.digest(plan), 'entries': [
        {'id': r['id'], 'raw': r['raw'], 'category': r['category'],
         'historicalObservation': {'title': r['historicalTitle'], 'year': r['historicalYear']},
         'parsingObserved': r['parsed'], 'queries': r['queries'],
         'parsing': old.get(r['id'], {}).get('parsing', {'status': 'unreviewed', 'expected': {}}),
         'identity': {'status': 'unreviewed', 'expected': None, 'equivalents': [], 'reviewer': '', 'validatedAtUtc': '',
                      'independentEvidence': '', 'outcome': 'unknown'}, 'protectedDiagnosticOnly': r['protected']}
        for r in plan['releases']]}


def compare(plan, capture_dir, out, references=None):
    verify_plan(plan); inputs = []; coverage = []
    for row in plan['releases']:
        historical, a = load_hits(plan, capture_dir, row['historicalRequests'])
        modern, z = load_hits(plan, capture_dir, row['v2Requests'])
        gaps = a + z
        dates = [dt.datetime.fromisoformat(x['CollectedAtUtc']) for x in historical + modern]
        if any(d.tzinfo is None for d in dates): raise ValueError('capture_date_requires_timezone')
        span = (max(dates) - min(dates)).total_seconds() / 3600 if dates else None
        if span is not None and span > 24: gaps.append({'reason': 'capture-window-exceeds-24-hours'})
        coverage.append({'id': row['id'], 'sufficient': not gaps, 'gaps': gaps, 'collectionSpanHours': span})
        if gaps: continue  # Missing/error is never an empty result and never a reject.
        inputs.append({'Id': row['id'], 'Category': row['category'], 'HistoricalTitle': row['historicalTitle'],
            'HistoricalYear': row['historicalYear'], 'HistoricalSeason': row['historicalSeason'], 'HistoricalEpisode': row['historicalEpisode'],
            'Protected': row['protected'], 'Manual': row['manual'], 'Queries': row['queries'], 'Parsed': row['parsed'], 'ExistingObservation': row['existingObservation'],
            'HistoricalBatches': historical, 'V2Batches': modern})
    out = local(out); out.mkdir(mode=0o700, parents=True, exist_ok=True)
    decisions = []
    if inputs:
        payload = out / 'comparison-input.json'; b.write(payload, {'captureComparison': inputs})
        dll = identification.build_runner(out / 'build')
        execution = subprocess.run(['dotnet', str(dll), str(payload)], capture_output=True, timeout=120)
        if execution.returncode:
            b.write_text(out / 'comparison-runner-error.log', execution.stderr.decode(errors='replace'))
            raise ValueError('offline_capture_comparison_failed')
        decisions = json.loads(execution.stdout)['decisions']
    for decision in decisions:
        if not decision['Sufficient']:
            row = next(r for r in coverage if r['id'] == decision['Id'])
            row['sufficient'] = False; row['gaps'].append({'reason': decision['LimitReason']})
    metrics = measure(plan, decisions, references)
    cohorts = {'protectedDiagnosticOnly': measure(plan, [d for d in decisions if d['ProtectedDiagnosticOnly']], references),
               'unprotectedDiagnosticOnly': measure(plan, [d for d in decisions if not d['ProtectedDiagnosticOnly']], references)}
    result = {'planSha256': b.digest(plan), 'sourceVersion': plan['sourceVersion'], 'coverage': coverage,
              'requestAccounting': {'distinctPlannedQueries': len(plan['requests']), 'providerRequests': dict(collections.Counter(q['provider'] for q in plan['requests'].values())),
                  'historicalLogicalQueries': sum(len(r['historicalRequests']) for r in plan['releases']), 'v2LogicalQueries': sum(len(r['v2Requests']) for r in plan['releases'])},
              'decisions': decisions, 'metrics': metrics, 'cohortMetrics': cohorts, 'scope': 'diagnostic-pre-download-identity-only-no-production-changes'}
    b.write(out / 'comparison.json', result)
    return result


def measure(plan, decisions, references):
    metrics = {'validatedCovered': 0, 'historicalCorrect': 0, 'v2Correct': 0,
               'historicalFalsePositive': 0, 'v2FalsePositive': 0, 'historicalAcceptances': 0, 'v2Acceptances': 0,
               'unmappedIdentityScopes': 0, 'protectedDiagnosticCount': 0,
               'identificationRateHistorical': None, 'identificationRateV2': None,
               'precisionHistorical': None, 'precisionV2': None}
    if references is None: return metrics
    if references['planSha256'] != b.digest(plan): raise ValueError('stale_reference_plan')
    refs = {r['id']: r['identity'] for r in references['entries']}
    for d in decisions:
        r = refs.get(d['Id'], {})
        if r.get('status') != 'validated' or not d['Sufficient']: continue
        if not r.get('reviewer') or not r.get('validatedAtUtc') or not r.get('independentEvidence') or not isinstance(r.get('expected'), dict):
            raise ValueError('validated_reference_requires_independent_evidence')
        timestamp = dt.datetime.fromisoformat(r['validatedAtUtc'])
        if timestamp.tzinfo is None: raise ValueError('reference_date_requires_timezone')
        if r.get('outcome') != 'identity-verified': raise ValueError('identity_reference_not_independently_verified')
        expected = r['expected']
        if set(expected) != {'Provider', 'MediaType', 'Id'} or expected['Provider'] not in PROVIDERS or expected['MediaType'] not in {'Movie', 'Series', 'Game'} or not re.fullmatch(r'[0-9]{1,12}', expected['Id']):
            raise ValueError('invalid_validated_identity')
        verified = [expected] + r.get('equivalents', [])
        for item in verified:
            if set(item) != {'Provider', 'MediaType', 'Id'} or item['Provider'] not in PROVIDERS or item['MediaType'] != expected['MediaType'] or not re.fullmatch(r'[0-9]{1,12}', item['Id']):
                raise ValueError('invalid_verified_equivalent_identity')
        if len({x['Provider'] for x in verified}) != len(verified): raise ValueError('conflicting_reference_identities')
        identities = [d['Historical'].get('SelectedIdentity'), d['V2'].get('SelectedIdentity')]
        if any(x and x['Provider'] not in {v['Provider'] for v in verified} for x in identities):
            metrics['unmappedIdentityScopes'] += 1; continue
        metrics['validatedCovered'] += 1
        metrics['protectedDiagnosticCount'] += d['ProtectedDiagnosticOnly']
        # Abstentions remain in the identification-rate denominator. Precision uses
        # acceptances only. Unknown cross-provider mappings never become errors/truth.
        for prefix, identity in zip(['historical', 'v2'], identities):
            if identity is not None:
                metrics[prefix + 'Acceptances'] += 1
                metrics[prefix + ('Correct' if identity in verified else 'FalsePositive')] += 1
    n = metrics['validatedCovered']
    if n:
        metrics['identificationRateHistorical'] = metrics['historicalCorrect'] / n
        metrics['identificationRateV2'] = metrics['v2Correct'] / n
    for prefix, suffix in [('historical', 'Historical'), ('v2', 'V2')]:
        if metrics[prefix + 'Acceptances']:
            metrics['precision' + suffix] = metrics[prefix + 'Correct'] / metrics[prefix + 'Acceptances']
    return metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__); sub = parser.add_subparsers(dest='action', required=True)
    prepare_cmd = sub.add_parser('prepare'); prepare_cmd.add_argument('--out', required=True); prepare_cmd.add_argument('--languages', default='default,fr-FR', help='Explicit historical locale scenario: default plus one UI language')
    collect_cmd = sub.add_parser('collect'); collect_cmd.add_argument('--plan', required=True); collect_cmd.add_argument('--config', required=True); collect_cmd.add_argument('--out', required=True); collect_cmd.add_argument('--execute', action='store_true'); collect_cmd.add_argument('--retry-incomplete', action='store_true')
    compare_cmd = sub.add_parser('compare'); compare_cmd.add_argument('--plan', required=True); compare_cmd.add_argument('--captures', required=True); compare_cmd.add_argument('--out', required=True); compare_cmd.add_argument('--references')
    args = parser.parse_args()
    try:
        if args.action == 'prepare':
            p = HERE / 'local'; out = local(args.out)
            languages = [None if name == 'default' else name for name in args.languages.split(',')]
            if len(languages) != 2 or languages[0] is not None: raise ValueError('historical_locale_requires_default_and_ui_language')
            plan = prepare(b.load(p / 'v23c-corpus.json'), b.load(p / 'v23c-complete/results.json'), b.load(p / 'real-review-selection.json'), languages)
            verify_plan(plan)
            existing = out / 'plan.json'
            if existing.exists() and b.load(existing) != plan: raise ValueError('use_new_output_for_changed_plan')
            b.write(existing, plan)
            if not (out / 'provider-config.example.json').exists(): b.write(out / 'provider-config.example.json', config_template())
            if not (out / 'references.json').exists(): b.write(out / 'references.json', reference_template(plan, b.load(p / 'v23c-complete/manual-review-120.json')))
            print(json.dumps({'releases': len(plan['releases']), 'distinctRequests': len(plan['requests']), 'networkCalls': 0}))
        elif args.action == 'collect':
            result = collect(b.load(local(args.plan)), load_config(args.config), args.out, args.execute, retry_incomplete=args.retry_incomplete)
            b.write(local(args.out) / 'collection-summary.json', result); print(json.dumps(result))
        else:
            result = compare(b.load(local(args.plan)), args.captures, args.out, b.load(local(args.references)) if args.references else None)
            print(json.dumps({'covered': sum(r['sufficient'] for r in result['coverage']), 'metrics': result['metrics']}))
    except (ValueError, KeyError, TypeError, OSError, subprocess.SubprocessError):
        print('Capture operation refused; verify local configuration, provenance and coverage. No credentials logged.', file=__import__('sys').stderr)
        return 2
    return 0


if __name__ == '__main__': raise SystemExit(main())
