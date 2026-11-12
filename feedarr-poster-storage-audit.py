#!/usr/bin/env python3
"""Diagnostic only. No DELETE, UPDATE, cleanup, API calls or file writes.
Run with the entire Feedarr data directory mounted read-only.
Only aggregate counts are emitted; never filenames, titles, keys or URLs.
"""
import argparse, collections, json, os, sqlite3, stat, time
from pathlib import Path

PROVIDERS = {'tmdb','tvmaze','igdb','rawg','fanart','jikan','theaudiodb',
             'googlebooks','openlibrary','comicvine','musicbrainz'}
def provider(name):
    value = name.split('-', 1)[0].lower()
    return value if value in PROVIDERS else 'other'

def database_snapshot(db):
    with sqlite3.connect(db.as_uri() + '?mode=ro', uri=True, timeout=5) as c:
        c.execute('PRAGMA query_only=ON')
        c.execute('BEGIN')
        releases = c.execute('SELECT id,entity_id,poster_file,poster_store_dir FROM releases').fetchall()
        entities = c.execute('SELECT id,poster_file,poster_store_dir FROM media_entities').fetchall()
        refs = c.execute('SELECT store_dir,release_id FROM poster_store_refs').fetchall()
        cache = c.execute('SELECT poster_file FROM poster_matches').fetchall()
        return releases, entities, refs, cache

def analyze(root, rows):
    releases, entities, refs, cache = rows
    es = {r[0]: r[1:] for r in entities}
    live_entities = {r[1] for r in releases if r[1] in es}
    direct_files = {r[2] for r in releases if r[2]}
    entity_files = {r[1] for r in entities if r[1]}
    all_files = direct_files | entity_files
    cache_files = {r[0] for r in cache if r[0]}
    direct_dirs = {r[3] for r in releases if r[3]}
    entity_dirs = {r[2] for r in entities if r[2]}
    linked_entity_dirs = {es[i][1] for i in live_entities if es[i][1]}
    protected_dirs = direct_dirs | entity_dirs  # deliberately conservative
    effective_files = collections.Counter()
    effective_dirs = collections.Counter()
    for _, eid, f, d in releases:
        ef, ed = es.get(eid, (None, None))
        # Match SQL COALESCE, including its treatment of empty strings.
        f = f if f is not None else ef
        d = d if d is not None else ed
        if f: effective_files[f] += 1
        if d: effective_dirs[d] += 1
    by_id = {r[0]:r[3] for r in releases}
    gc_refs = {d for d, rid in refs if rid in by_id and by_id[rid] == d}
    metrics = collections.Counter()
    groups = collections.defaultdict(lambda: {'files':0,'bytes':0})
    sizes = collections.Counter()
    errors = collections.Counter()
    root_names, store_names = set(), set()
    store_files = collections.defaultdict(list)
    latest = {}
    now = time.time()
    poster_root = root / 'posters'
    def onerror(e): errors[type(e).__name__] += 1
    for parent, dirs, files in os.walk(poster_root, followlinks=False, onerror=onerror):
        parent = Path(parent)
        rel_parent = parent.relative_to(poster_root)
        safe_dirs = []
        for d in dirs:
            child = parent / d
            if child.is_symlink(): metrics['symlinks_skipped'] += 1
            else:
                safe_dirs.append(d)
                if rel_parent == Path('store'):
                    store_names.add(d)
                    try: latest[d] = child.stat().st_mtime
                    except OSError as e: onerror(e)
        dirs[:] = safe_dirs
        for name in files:
            path = parent / name
            try:
                s = path.lstat()
                if not stat.S_ISREG(s.st_mode):
                    metrics['non_regular_files_skipped'] += 1
                    continue
            except OSError as e:
                onerror(e); continue
            rel = path.relative_to(poster_root)
            parts = rel.parts
            metrics['physical_files'] += 1
            metrics['apparent_bytes'] += s.st_size
            if len(parts) == 1:
                root_names.add(name)
                group = 'flat/' + provider(name)
                metrics['flat_files'] += 1
                if name not in all_files:
                    metrics['flat_without_release_or_entity_reference'] += 1
                    metrics['flat_without_reference_bytes'] += s.st_size
                    if name in cache_files: metrics['flat_cache_only'] += 1
                    if path.suffix.lower() in {'.jpg','.jpeg','.png','.webp'}:
                        metrics['flat_unreferenced_image_files'] += 1
            elif parts[0] == 'store' and len(parts) >= 3:
                store, child = parts[1], '/'.join(parts[2:])
                group = 'store/' + provider(store)
                store_files[store].append(child)
                latest[store] = max(latest.get(store, 0), s.st_mtime)
                metrics['store_files'] += 1
                if store not in protected_dirs:
                    metrics['store_without_any_db_reference_bytes'] += s.st_size
            else:
                group = 'historical_or_other_subdirectories'
                metrics['historical_or_other_files'] += 1
            groups[group]['files'] += 1
            groups[group]['bytes'] += s.st_size
            size_bin = '<100KiB' if s.st_size < 102400 else ('100KiB-1MiB' if s.st_size < 1048576 else '>=1MiB')
            sizes[size_bin] += 1
            if name.endswith('.tmp'): metrics['temporary_files'] += 1
    ages = collections.Counter()
    for d in store_names:
        fs = store_files[d]
        originals = [f for f in fs if '/' not in f and f.startswith('original.') and not f.endswith('.tmp')]
        thumbs = {'w342.webp','w500.webp','w780.webp'}
        metrics['stores_missing_original'] += not bool(originals)
        metrics['missing_thumb_slots'] += len(thumbs - set(fs))
        metrics['extra_original_files'] += max(0, len(originals)-1)
        metrics['unexpected_store_files'] += sum(f not in originals and f not in thumbs for f in fs)
        if d not in protected_dirs:
            age = max(0, now - latest.get(d, now))/86400
            ages['<1d' if age < 1 else ('1-7d' if age < 7 else ('7-30d' if age < 30 else '>=30d'))] += 1
    restore = root / 'restore-state'
    restore_count = restore_bytes = 0
    if restore.is_symlink(): raise ValueError('restore-state must not be a symlink')
    for parent, dirs, files in os.walk(restore, followlinks=False, onerror=onerror) if restore.exists() else []:
        dirs[:] = [d for d in dirs if not (Path(parent)/d).is_symlink()]
        for f in files:
            try:
                s=(Path(parent)/f).lstat()
                if stat.S_ISREG(s.st_mode): restore_count += 1; restore_bytes += s.st_size
            except OSError as e: onerror(e)
    return {
      'warning':'Inventory only. No candidate is certified safe to delete; publishers may be active. No production cleanup authorized.',
      'database': {
        'releases':len(releases),'releases_with_effective_poster_name':sum(effective_files.values()),
        'distinct_effective_poster_names':len(effective_files),
        'distinct_effective_stores':len(effective_dirs),
        'shared_effective_stores':sum(v>1 for v in effective_dirs.values()),
        'all_entities':len(entities),'entities_without_linked_release':len(entities)-len(live_entities),
        'flat_names_protected_by_db':len(all_files),
        'flat_names_protected_only_by_unlinked_entities':len(entity_files-direct_files-{es[i][0] for i in live_entities}),
        'direct_release_stores':len(direct_dirs),'linked_entity_stores':len(linked_entity_dirs),
        'all_entity_stores':len(entity_dirs)},
      'disk':dict(metrics),'providers':dict(groups),'file_size_distribution':dict(sizes),
      'stores':{'physical_directories':len(store_names),
        'protected_by_any_release_or_entity':len(store_names & protected_dirs),
        'without_any_release_or_entity_reference':len(store_names-protected_dirs),
        'current_gc_candidates':len(store_names-gc_refs),
        'current_gc_candidates_with_entity_reference':len((store_names-gc_refs)&entity_dirs),
        'db_referenced_but_missing_directory':len(protected_dirs-store_names),
        'unreferenced_age_since_latest_mtime':dict(ages)},
      'flat':{'db_names_missing_on_disk':len(all_files-root_names)},
      'restore':{'pending_pointer_present':(restore/'pending.json').exists(),
        'journal_present':(restore/'operation.json').exists(),'files':restore_count,'bytes':restore_bytes},
      'scan_errors':dict(errors),'elapsed_snapshot_is_not_atomic_with_filesystem':True
    }

def main():
    a=argparse.ArgumentParser(description=__doc__)
    a.add_argument('data_dir',type=Path)
    a.add_argument('--database',default='feedarr.db')
    args=a.parse_args()
    root=args.data_dir.resolve(strict=True)
    if not (os.statvfs(root).f_flag & os.ST_RDONLY):
        raise SystemExit('Refused: mount the entire data directory read-only first (SQLite WAL/SHM safety).')
    db=(root/args.database).resolve(strict=True)
    db.relative_to(root)
    for p in (root,db.parent,root/'posters'):
        if not (os.statvfs(p).f_flag & os.ST_RDONLY):
            raise SystemExit('Refused: mount the entire data directory read-only first (SQLite WAL/SHM safety).')
    if (root/'posters').is_symlink(): raise SystemExit('Refused: posters is a symlink')
    started=time.time()
    result=analyze(root,database_snapshot(db))
    result['scan_seconds']=round(time.time()-started,3)
    print(json.dumps(result,indent=2,sort_keys=True))

if __name__=='__main__': main()
