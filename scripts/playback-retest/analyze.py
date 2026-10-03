import json,bisect,statistics,argparse,csv
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('mode',choices=['public','local']);p.add_argument('--root',default='artifacts/sync-followup/desktop');a=p.parse_args();root=Path(a.root)
def read(path):
 out=[]
 lines=path.read_text(encoding='utf-8-sig').splitlines()
 for i,line in enumerate(lines):
  try:out.append(json.loads(line))
  except json.JSONDecodeError:
   if i!=len(lines)-1:raise
   # Live snapshot may end with an incomplete final write.
 return out
def stats(values):
 d=sorted(values);n=len(d)
 return dict(samples=n,medianMs=d[int((n-1)*.5)] if n else None,p95Ms=d[int((n-1)*.95)] if n else None,maxMs=max(d) if n else None,over500=sum(v>500 for v in d),over1200=sum(v>1200 for v in d))
logs={r:read(root/f'{a.mode}-{r}/diagnostics.jsonl') for r in ['host','guest']}
phases=read(root/f'{a.mode}-phases.jsonl');begin=phases[0]['serverMs'];end=phases[-1]['serverMs']+phases[-1]['wait']*1000
samples={r:[dict(x['data'],monoMs=x['monoMs']) for x in logs[r] if x['kind']=='sample' and begin<=x['data']['serverMs']<=end] for r in logs}
playing={r:[x for x in samples[r] if x['playing'] and x['targetPlaying'] and x['ready'] and x['connected']] for r in logs}
pairs=[];g=sorted(playing['guest'],key=lambda x:x['serverMs']);times=[x['serverMs'] for x in g]
for h in playing['host']:
 i=bisect.bisect_left(times,h['serverMs']);candidate=[(k,g[k]) for k in range(max(0,i-2),min(len(g),i+3)) if abs(g[k]['serverMs']-h['serverMs'])<=150 and g[k]['revision']==h['revision']]
 if not candidate:continue
 k,y=min(candidate,key=lambda t:abs(t[1]['serverMs']-h['serverMs']))
 pairs.append(dict(serverMs=h['serverMs'],revision=h['revision'],difference=abs(h['position']-y['position']),estimatedDifference=abs((h['estimated']-h['desired'])-(y['estimated']-y['desired'])),targetAlignedDifference=abs((h['position']-h['desired'])-(y['position']-y['desired'])),skewMs=abs(h['serverMs']-y['serverMs']),guestIndex=k))
# Report a separate distribution excluding the first 3 seconds of each revision. Full results still include transitions.
revisionTimes={r:{} for r in logs}
for r in logs:
 for x in logs[r]:
  if x['kind']=='revision':revisionTimes[r][x['data']['revision']]=x['data'].get('serverMs',0)
steady=[]
for x in pairs:
 onset=max(revisionTimes[r].get(x['revision'],begin) for r in logs)
 if x['serverMs']>=onset+3000:steady.append(x)
client={}
for r in logs:
 s=samples[r];play=playing[r];lo=min((x['monoMs'] for x in s),default=0);hi=max((x['monoMs'] for x in s),default=0)
 ev=[x for x in logs[r] if lo<=x['monoMs']<=hi]
 client[r]=dict(playingSamples=len(play),targetPlayingSamples=sum(bool(x['targetPlaying']) and x['ready'] and x['connected'] for x in s),targetError=stats([abs(x['position']-x['desired']) for x in play]),estimatedTargetError=stats([abs(x['estimated']-x['desired']) for x in play]),disconnectedSamples=sum(not x['connected'] for x in s),seekCount=sum(x['kind']=='seek' for x in ev),settle=stats([x['data']['elapsedMs'] for x in ev if x['kind']=='settle-end']),settleTimeouts=sum(x['kind']=='settle-end' and x['data']['reason']=='timeout' for x in ev),rangeLatency=stats([x['data']['elapsedMs'] for x in ev if x['kind']=='range-read']),rangeErrors=sum(x['kind']=='range-error' and x['data']['error'] not in ('TaskCanceledException','OperationCanceledException') for x in ev),rangeCancellations=sum(x['kind']=='range-error' and x['data']['error'] in ('TaskCanceledException','OperationCanceledException') for x in ev),rateFailures=sum(x['kind']=='rate' and x['data']['result']!=0 for x in ev),maxClockCorrectionMs=max([abs(x['data']['correctionMs']) for x in ev if x['kind']=='clock'] or [0]),dropped=max([x.get('dropped',0) for x in logs[r]] or [0]),worst=sorted(play,key=lambda x:abs(x['position']-x['desired']),reverse=True)[:5])
summary=[]
for i,phase in enumerate(phases):
 stop=phases[i+1]['serverMs'] if i+1<len(phases) else end
 paired=[x for x in pairs if phase['serverMs']<=x['serverMs']<stop]
 last={r:next((x for x in reversed(samples[r]) if x['serverMs']<stop-250),None) for r in samples}
 summary.append(dict(phase=phase,paired=stats([x['difference'] for x in paired]),estimatedPaired=stats([x['estimatedDifference'] for x in paired]),last=last))
controls={}
if len(summary)==14:
 def both(i):return summary[i]['last']
 def at(i,position):return all(x and not x['targetPlaying'] and not x['playing'] and x['state'] in ('Paused','Stopped') and abs(x['position']-position)<=200 for x in both(i).values())
 controls['pausedSeek90']=at(2,90000)
 controls['authorizedGuestSeek150']=at(10,150000)
 controls['stopZero']=at(13,0)
 controls['guestDeniedBeforeAuthorization']=all(both(6)[r] and both(5)[r] and both(6)[r]['revision']==both(5)[r]['revision'] for r in logs)
 controls['guestDeniedAfterRevocation']=all(both(12)[r] and both(11)[r] and both(12)[r]['revision']==both(11)[r]['revision'] and abs(both(12)[r]['position']-150000)<=200 for r in logs)
 controls['allCommandsExecuted']=all(any(x['kind']=='command' and x['data']['sequence']==ph['sequence'] and x['data']['action']==ph['action'] for x in logs[ph['role']]) for ph in phases)
result=dict(controls=controls,mode=a.mode,paired=stats([x['difference'] for x in pairs]),estimatedPaired=stats([x['estimatedDifference'] for x in pairs]),targetAlignedPaired=stats([x['targetAlignedDifference'] for x in pairs]),steadyPaired=stats([x['difference'] for x in steady]),steadyEstimatedPaired=stats([x['estimatedDifference'] for x in steady]),hostPairCoverage=len(pairs)/len(playing['host']) if playing['host'] else 0,uniqueGuestPairs=len(set(x['guestIndex'] for x in pairs)),pairToleranceMs=150,clients=client,phases=summary)
(root/f'{a.mode}-results.json').write_text(json.dumps(result,indent=2))
with (root/f'{a.mode}-pairs.csv').open('w',newline='') as f:
 w=csv.DictWriter(f,fieldnames=['serverMs','revision','difference','estimatedDifference','targetAlignedDifference','skewMs','guestIndex']);w.writeheader();w.writerows(pairs)
print(json.dumps({k:v for k,v in result.items() if k not in ('clients','phases')},indent=2))
for r,c in client.items():print(r,json.dumps({k:v for k,v in c.items() if k!='worst'}))
