#!/usr/bin/env python3
"""Offline P7 diagnostic projection, NOT a transport/ranker replay.

Excludes truncated diagnostic pools. Scores/guards are historical evidence;
missing titles/context/provenance are never invented. Acceptance here is only
conditional on that historical diagnostic and is not verified matching quality.
"""
import argparse
import csv,json,collections,hashlib
from pathlib import Path
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('csv',type=Path)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
p=args.csv
rows=list(csv.DictReader(p.open(encoding='utf-8-sig')))
profiles={'Actuel':(.90,.92,.08),'Leger':(.88,.91,.06),'Equilibre':(.85,.90,.05),'Permissif':(.82,.88,.04)}
outputs=[]; blockers=collections.Counter();statuses=collections.Counter()
def simrange(c):
 pos=set(c['PositiveReasons']);base=.1*('media-type-agrees' in pos)
 if pos & {'trusted-identity-agrees','verified-tv-structure','verified-cross-provider-tv-identity','verified-broadcast-network-suffix','verified-anime-structure','verified-pc-platform','audio-artist-agrees','author-agrees','isbn-agrees'}:base+=.1
 if 'year-agrees' in pos:base+=.1
 elif 'parsed-year-nearby' in pos:base+=.06
 score=c.get('EvidenceScore',c['Confidence']);score-=.1*('rss-id-and-official-synopsis-agree' in pos)
 if score>=.999999:lo=max(0,(1-base)/.8);hi=1
 else:lo=max(0,(score-base-.000001)/.8);hi=min(1,(score-base+.000001)/.8)
 if pos & {'exact-title','known-alternate-title-agrees'}:lo=hi=1
 return lo,hi
for row in rows:
 try:j=json.loads(row['ranking'])
 except:statuses['no_ranking']+=1;continue
 if j.get('Truncated') or j.get('CandidateCount')!=len(j['Candidates']):statuses['truncated']+=1;continue
 statuses['complete_diagnostic']+=1
 for c in j['Candidates']:blockers.update(x['Code'] for x in c['Contradictions'])
 entry={'releaseId':row['releaseId'],'category':row['category'],'mediaType':row['mediaType'],'query':row['query'],'stage':row['stage'],'historicalDecision':j.get('RankingDecision'),'profiles':{}}
 for name,(sim,acc,gap) in profiles.items():
  for relaxed in [False,True]:
   viable=[];unknown=False
   for c in j['Candidates']:
    codes={x['Code'] for x in c['Contradictions']}-{'close-competing-identities'}
    if relaxed:codes.discard('non-exact-title-needs-independent-evidence')
    if 'title-similarity-insufficient' in codes:
     lo,hi=simrange(c)
     if hi+1e-9<sim:continue
     if lo+1e-9<sim:unknown=True;continue
     codes.remove('title-similarity-insufficient')
    if codes:continue
    viable.append(c)
   viable.sort(key=lambda c:c.get('EvidenceScore',c['Confidence']),reverse=True)
   selected=None
   if viable and viable[0]['Confidence']+1e-9>=acc and (len(viable)==1 or viable[0].get('EvidenceScore',viable[0]['Confidence'])-viable[1].get('EvidenceScore',viable[1]['Confidence'])+1e-9>=gap):selected=viable[0]['Id']
   entry['profiles'][name+('_sans_veto_exact' if relaxed else '')]={'selected':selected,'similarity_uncertain':unknown}
 outputs.append(entry)
result={'csv':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'rows':len(rows),'statuses':statuses,'blockers':blockers,'rowsAnalysis':outputs}
args.output.write_text(json.dumps(result,indent=2,ensure_ascii=False))
print(json.dumps({'rows':len(rows),'statuses':statuses,'summary':{key:{'accept':sum(x['profiles'][key]['selected'] is not None for x in outputs),'newVsCurrent':sum(x['profiles'][key]['selected'] is not None and x['profiles']['Actuel']['selected'] is None for x in outputs),'uncertainSimilarity':sum(x['profiles'][key]['similarity_uncertain'] for x in outputs)} for key in outputs[0]['profiles']}},indent=2))
for x in outputs:
 if any(v['selected'] for k,v in x['profiles'].items() if k!='Actuel') and not x['profiles']['Actuel']['selected']:print(x['releaseId'],x['query'],json.dumps(x['profiles']))
