#!/usr/bin/env python3
"""Private local matching benchmark. Standard-library only; never starts Feedarr."""
import fcntl
import argparse, collections, datetime, hashlib, json, os, pathlib, re, sqlite3, subprocess, sys, time

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[1]
DEFAULT = pathlib.Path.home() / "Documents/Projets/Feedarr-benchmarks"
NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}\Z")
SENSITIVE = re.compile(r"https?://|magnet:|www\.|@|[/\\]|(?:api.?key|token|password|passkey|secret)\s*[=:]|\b(?:\d{1,3}\.){3}\d{1,3}\b", re.I)

def digest(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for block in iter(lambda: f.read(1024*1024), b''): h.update(block)
    return h.hexdigest()

def write(p, obj):
    tmp = p.with_suffix(p.suffix + '.new')
    with open(tmp, 'x', encoding='utf-8') as f:
        os.chmod(tmp, 0o600); json.dump(obj, f, ensure_ascii=False, indent=2); f.write('\n')
    os.replace(tmp, p)

def load(p):
    if p.stat().st_size > 64*1024*1024: raise ValueError('file_too_large')
    return json.loads(p.read_text())

def safe(value):
    return '[titre masqué]' if isinstance(value, str) and SENSITIVE.search(value) else value

def identity(x):
    return None if x is None else f"{x['provider']}:{x['type']}:{x['id']}"

def compare(a, b, labels):
    if not a.get('completed') or not b.get('completed'): raise ValueError('incomplete_run')
    if a['datasetHash'] != b['datasetHash'] or [x['id'] for x in a['entries']] != [x['id'] for x in b['entries']]:
        raise ValueError('different_dataset_or_selected_ids')
    rows = []
    for old, new in zip(a['entries'], b['entries']):
        x, y = old.get('entry'), new.get('entry'); ref = labels.get(str(new['id']), {})
        why = None
        if not x or not y or not x['providerEvidenceComplete'] or not y['providerEvidenceComplete']:
            why = 'incomplete_or_unsupported'
        elif old.get('benchmarkStatus') or new.get('benchmarkStatus'): why = 'benchmark_transport_incomplete'
        elif not (a.get('captureSetHash') and a.get('captureSetHash')==b.get('captureSetHash')) and (x['providerEvidenceFingerprint'] != y['providerEvidenceFingerprint'] or x['queryPlanFingerprint'] != y['queryPlanFingerprint'] or x['searchLocale'] != y['searchLocale']):
            why = 'different_queries_or_provider_evidence'
        pi, qi = identity(x['detail']['selectedIdentity']) if x else None, identity(y['detail']['selectedIdentity']) if y else None
        accepted = bool(y and y['detail']['decision'] == 'accept')
        reviewed = ref.get('verified') is True and isinstance(ref.get('correctIdentities'), list)
        correct = ref.get('correctIdentities') or []
        if not isinstance(correct,list): raise ValueError('invalid_reference_identities')
        previous_correct = bool(x and x['detail']['decision'] == 'accept' and pi in correct)
        now_correct = accepted and qi in correct
        false_positive = reviewed and accepted and not now_correct
        if why: category = 'non_evaluable'
        elif not reviewed: category = 'a_revoir'
        elif previous_correct and not now_correct: category = 'regression'
        elif false_positive: category = 'faux_positif'
        elif now_correct and not previous_correct: category = 'nouvelle_association_correcte'
        elif now_correct: category = 'correcte_stable'
        elif not accepted and (not correct or ref.get('abstentionJustified') is True): category = 'abstention_justifiee'
        else: category = 'abstention_a_revoir'
        evidence_changed = bool(x and y and (x['providerEvidenceFingerprint'],x['queryPlanFingerprint'],x['searchLocale']) != (y['providerEvidenceFingerprint'],y['queryPlanFingerprint'],y['searchLocale']))
        valid_review = reviewed and not why
        rows.append(dict(id=new['id'], category=category, reason=why, evidenceChanged=evidence_changed, before=pi, after=qi,
                         beforeDecision=x['detail']['decision'] if x else None, afterDecision=y['detail']['decision'] if y else None,
                         beforeCorrect=previous_correct if valid_review else None, afterCorrect=now_correct if valid_review else None,
                         beforeFalsePositive=(x['detail']['decision']=='accept' and pi not in correct) if valid_review else None,
                         falsePositive=false_positive if valid_review else None,
                         decisionChanged=bool(x and y and (x['detail']['decision'],pi)!=(y['detail']['decision'],qi))))
    return dict(datasetHash=a['datasetHash'], before=a['engine'], after=b['engine'],
                counts=dict(collections.Counter(x['category'] for x in rows)),
                decisionChanges=sum(x['decisionChanged'] for x in rows),
                reviewedComparable=sum(x['afterCorrect'] is not None for x in rows),
                beforeCorrect=sum(x['beforeCorrect'] is True for x in rows), afterCorrect=sum(x['afterCorrect'] is True for x in rows),
                beforeFalsePositives=sum(x['beforeFalsePositive'] is True for x in rows),
                afterFalsePositives=sum(x['falsePositive'] is True for x in rows), entries=rows)

def main():
    os.umask(0o077)
    ap=argparse.ArgumentParser(description=__doc__); ap.add_argument('--root',type=pathlib.Path,default=DEFAULT)
    sub=ap.add_subparsers(dest='command',required=True)
    cap=sub.add_parser('capture');cap.add_argument('reference');cap.add_argument('--db',type=pathlib.Path,default=REPO/'.tmp/local-dev/data/feedarr.db')
    run=sub.add_parser('replay');run.add_argument('reference');run.add_argument('name');run.add_argument('--record',action='store_true');run.add_argument('--keys',type=pathlib.Path,default=REPO/'.tmp/local-dev/data/keys');run.add_argument('--ids');run.add_argument('--same-as');run.add_argument('--limit',type=int);run.add_argument('--max-requests',type=int,default=60)
    cmp=sub.add_parser('compare');cmp.add_argument('reference');cmp.add_argument('before');cmp.add_argument('after')
    args=ap.parse_args()
    for n in [args.reference, *([args.name] if args.command=='replay' else [args.before,args.after] if args.command=='compare' else [])]:
        if not NAME.fullmatch(n): raise ValueError('invalid_name')
    root=args.root.expanduser().resolve()
    if not root.is_relative_to(pathlib.Path.home().resolve()) or root==REPO or REPO in root.parents or any((parent/'.git').exists() for parent in [root,*root.parents]):
        raise ValueError('benchmark_root_must_be_persistent_local_and_outside_git')
    root.mkdir(parents=True,exist_ok=True,mode=0o700);os.chmod(root,0o700); folder=root/args.reference
    lock = open(root/".benchmark.lock", "a")
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if args.command=='capture':
        db=args.db.resolve()
        if not db.is_relative_to(REPO) or not db.is_file(): raise ValueError('source_must_be_local_worktree_database')
        folder.mkdir(mode=0o700);start=time.monotonic()
        try:
            with sqlite3.connect(db.as_uri()+'?mode=ro',uri=True) as src, sqlite3.connect(folder/'snapshot.db') as dst:
                src.execute('PRAGMA query_only=ON')
                def progress(*_):
                    if time.monotonic()-start>30: raise TimeoutError('backup_deadline')
                src.backup(dst,pages=256,progress=progress,sleep=.05)
                if (folder/'snapshot.db').stat().st_size>256*1024*1024: raise ValueError('snapshot_too_large')
                if dst.execute('PRAGMA quick_check').fetchone()[0]!='ok': raise ValueError('snapshot_invalid')
                dst.row_factory=sqlite3.Row
                rows=dst.execute('''SELECT r.id,r.title,r.unified_category,r.title_clean,r.year,r.season,r.episode,
                  r.media_type,r.resolution,r.source,r.codec,r.release_group,r.entity_id,me.match_revision,
                  coalesce(me.ext_title,r.ext_title) existing_title,r.std_category_id,r.spec_category_id,r.category_ids,
                  coalesce(me.tmdb_id,r.tmdb_id) tmdb_id,coalesce(me.tmdb_media_type,r.tmdb_media_type) tmdb_type,
                  coalesce(me.tvdb_id,r.tvdb_id) tvdb_id,coalesce(me.ext_provider,r.ext_provider) provider,
                  coalesce(me.ext_provider_id,r.ext_provider_id) provider_id,
                  me.manual_match_provider,me.manual_match_type,me.manual_match_id,r.title_manual_override
                  FROM releases r LEFT JOIN media_entities me ON me.id=r.entity_id ORDER BY r.id LIMIT 2001''').fetchall()
                if len(rows)>2000: raise ValueError('corpus_exceeds_2000_releases')
            os.chmod(folder/'snapshot.db',0o600)
            corpus=[{k:safe(v) for k,v in dict(r).items()} for r in rows]
            write(folder/'manifest.json',dict(version=1,createdUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),datasetHash=digest(folder/'snapshot.db'),entries=corpus))
            write(folder/'labels.json',{str(r['id']):dict(verified=False,correctIdentities=None,abstentionJustified=False) for r in rows})
            (folder/'captures').mkdir(mode=0o700);(folder/'runs').mkdir(mode=0o700)
            print(f'Reference {folder}: {len(rows)} releases; snapshot prive, aucune synchronisation.')
        except BaseException:
            # Keep failure explicit; never leave a partial file looking like a valid reference.
            (folder/'INVALID').touch(mode=0o600);raise
        return
    manifest=load(folder/'manifest.json')
    if (folder/'INVALID').exists() or digest(folder/'snapshot.db')!=manifest['datasetHash']: raise ValueError('snapshot_modified_or_invalid')
    if args.command=='compare':
        result=compare(load(folder/'runs'/f'{args.before}.json'),load(folder/'runs'/f'{args.after}.json'),load(folder/'labels.json'))
        target=folder/f'comparison-{args.before}-{args.after}.json';write(target,result)
        print(json.dumps(dict(counts=result['counts'],decisionChanges=result['decisionChanges']),ensure_ascii=False));print(target);return
    if not 1<=args.max_requests<=1000: raise ValueError('invalid_request_budget')
    if len(list((folder/'runs').glob('*.json')))>=20: raise ValueError('twenty_runs_limit_remove_old_results')
    target=folder/'runs'/f'{args.name}.json'
    if target.exists(): raise ValueError('run_name_exists')
    ids=[r['id'] for r in manifest['entries']]
    if args.same_as:
        if args.ids or args.limit is not None or not NAME.fullmatch(args.same_as): raise ValueError('invalid_same_as_selection')
        previous=load(folder/'runs'/f'{args.same_as}.json')
        if previous['datasetHash']!=manifest['datasetHash'] or not previous.get('completed'): raise ValueError('invalid_previous_run')
        ids=previous['selectedIds']
    if args.ids:
        selected=[int(v) for v in args.ids.split(',')]
        if len(set(selected))!=len(selected) or not set(selected)<=set(ids): raise ValueError('ids_not_in_snapshot_or_duplicated')
        ids=sorted(selected)
    if args.limit is not None:
        if args.limit<1: raise ValueError('invalid_limit')
        ids=ids[:args.limit]
    if not ids: raise ValueError('empty_selection')
    if args.record and (not args.keys.resolve().is_relative_to(REPO) or not (args.keys/'.master-key').is_file()): raise ValueError('keys_must_be_existing_local_worktree_keyring')
    build=root/'.build';build.mkdir(exist_ok=True,mode=0o700)
    with open(build/'build.log','w') as log:
        completed=subprocess.run(['dotnet','build',str(HERE/'Runner/Runner.csproj'),'-o',str(build/'out'),'-v','quiet'],stdout=log,stderr=log,cwd=REPO)
    if completed.returncode: raise ValueError('build_failed_see_private_build_log')
    request=dict(snapshot=str(folder/'snapshot.db'),captures=str(folder/'captures'),ids=ids,record=args.record,keys=str(args.keys.resolve()),maxRequests=args.max_requests,output=str(target),datasetHash=manifest['datasetHash'])
    request_file=build/'request.json';write(request_file,request)
    try:
        rc=subprocess.run(['dotnet',str(build/'out/Feedarr.Api.Tests.dll'),str(request_file)],cwd=REPO).returncode
        if rc: raise ValueError('runner_failed')
    finally: request_file.unlink(missing_ok=True)
    if digest(folder/'snapshot.db')!=manifest['datasetHash']: raise ValueError('snapshot_modified_by_run')
    result=load(target)
    capture_hash=hashlib.sha256()
    for f in sorted((folder/'captures').glob('*.json')): capture_hash.update((f.name+':'+digest(f)).encode())
    result['captureSetHash']=capture_hash.hexdigest()
    result['engine']['commit']=subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO,text=True).strip()
    result['engine']['dirty']=bool(subprocess.check_output(['git','diff','--name-only'],cwd=REPO,text=True).strip());write(target,result)
    print(f'{len(result["entries"])} releases; HTTP reels={result["networkRequests"]}; {target}')

if __name__=='__main__':
    try: main()
    except (ValueError,OSError,sqlite3.Error,TimeoutError) as exc:
        print(f'Benchmark refuse/echoue: {type(exc).__name__}: {str(exc) if isinstance(exc,ValueError) else "voir les fichiers prives; aucun secret affiche"}',file=sys.stderr);sys.exit(1)
