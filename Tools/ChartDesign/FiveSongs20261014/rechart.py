"""5曲のフレーズ譜面。再生成方法と参照動画は RECHART.md を参照。"""
import argparse
import collections
import copy
import hashlib
import json
import math
from pathlib import Path

import numpy as np

from analyze import analyze, anchors, time_at
from build_pack import ORIGINS, path_for, save
from spatial_layout import apply_layout

# 小節単位のランダムな差替えをしない。音源の反復と変速区間に対応する設計。
# (開始四分拍, 終了四分拍, リズム, 空間配置, 展開名)
FORM = {
 'ManicSonic': [(0,32,'pulse','vertical','導入'),(32,64,'drive','diagonal','主拍'),
   (64,128,'drive','arc','主題A'),(128,160,'answer','diagonal','応答'),
   (160,199,'sparse','vertical','減速と余韻'),(199,224,'pulse','vertical','再導入'),
   (224,320,'drive','arc','主題Aの発展'),(320,368,'answer','diagonal','主題の回帰'),
   (368,379,'sparse','vertical','ブレイク'),(379,395,'slow','arc','BPM60'),
   (395,403,'pulse','vertical','再加速'),(403,416,'finish','diagonal','終止')],
 'escapade': [(0,16,'rest','vertical','前奏無音'),(16,80,'skip','diagonal','裏拍の主題'),
   (80,144,'skip','arc','主題の応答'),(144,160,'sparse','vertical','つなぎ'),
   (160,208,'pulse','vertical','表拍へ'),(208,240,'sync','diagonal','シンコペーション'),
   (240,272,'sparse','arc','間奏'),(272,304,'skip','diagonal','主題の回帰'),
   (304,340,'outro','vertical','余韻')],
 'logic': [(0,32,'sparse','vertical','8/8の導入'),(32,80,'answer','arc','応答'),
   (80,141,'logic','diagonal','8分の裏拍'),(141,176,'pulse','vertical','BPM120へ'),
   (176,224,'drive','arc','中盤の展開'),(224,236,'slow','vertical','段階的減速'),
   (236,320,'offbeat','diagonal','裏拍の主題B'),(320,384,'offbeat','arc','主題Bの発展'),
   (384,400.5,'sparse','vertical','つなぎ'),(400.5,440,'answer','diagonal','BPM160へ回帰'),
   (440,456,'outro','vertical','終止')],
 'BenkyouRemaster': [(0,32,'answer','diagonal','導入'),(32,80,'drive','arc','主題A'),
   (80,114,'slow','diagonal','BPM100'),(114,144,'pulse','vertical','主拍へ復帰'),
   (144,168,'drive','arc','短い展開'),(168,208,'sparse','vertical','小節頭と余韻'),
   (208,240,'drive','diagonal','主題Aの回帰'),(240,256,'pulse','vertical','主拍'),
   (256,280,'drive','arc','主題Aの発展'),(280,304,'slow','arc','BPM70'),
   (304,328,'doubletime','vertical','BPM360・実打は半速')],
 'Aobasou': [(0,32,'pulse','vertical','導入'),(32,64,'answer','diagonal','歌の応答'),
   (64,128,'pulse','arc','主拍の主題'),(128,160,'drive','diagonal','展開'),
   (160,176,'answer','vertical','つなぎ'),(176,224,'vocal','arc','歌の裏拍'),
   (224,256,'pulse','vertical','主拍への回帰'),(256,304,'vocal','diagonal','歌の主題の発展'),
   (304,324,'outro','arc','終止')]
}

# 1小節の骨格を4小節単位で展開。Normalは主拍、Hardは実音の裏拍を足す。
# すべて四分音符を1とする（logicの8/8も1小節=4四分拍）。
RHYTHMS = {
 'rest': ([],[],[]),
 'sparse': ([0],[0,2],[0,1,2,3]),
 'pulse': ([0,2],[0,1,2,3],[0,1,2,3,3.5]),
 'answer': ([0,2],[0,1,2,3],[0,1,1.5,2,3,3.5]),
 'drive': ([0,2],[0,1,2,3],[0,.5,1,2,2.5,3]),
 'skip': ([0,2],[0,.5,2,2.5],[0,.5,1.5,2,2.5,3.5]),
 'sync': ([0,3],[0,1,2.5,3],[0,1,1.5,2.5,3]),
 'logic': ([.5,2.5],[.5,1.5,2.5,3],[0,.5,1.5,2,2.5,3]),
 'offbeat': ([.5,2.5],[.5,1.5,2.5,3.5],[0,.5,1.5,2,2.5,3,3.5]),
 'slow': ([0,2],[0,1,2,3],[0,.5,1,1.5,2,2.5,3,3.5]),
 'vocal': ([0,2],[0,1.5,2,3.5],[0,1,1.5,2,3,3.5]),
 'outro': ([0,2],[0,2,3.5],[0,.5,1.5,2,2.5,3.5]),
 'finish': ([0,2],[0,1,2,3],[0,.5,1,2,3]),
 'doubletime': ([0],[0,2],[0,2]),
}
DIFFS=('easy','normal','hard')

class Music:
    def __init__(self,spec,features):
        self.spec=spec
        self.analysis=json.loads((features/spec['id']/'analysis.json').read_text(encoding='utf8'))
        self.points=anchors(spec['tempos'],ORIGINS[spec['id']])
        f=np.load(features/spec['id']/'features.npz')
        self.times,self.onset,self.energy=(f[k] for k in ('times','onset','energy'))
        self.duration=self.analysis['duration']
        self.last_beat=self.beat(self.analysis['lastActiveSeconds']-.18)
        self.cache={}
    def t(self,b): return time_at(b,self.points)
    def beat(self,t):
        b,s,bpm=next((p for p in reversed(self.points) if p[1]<=t),self.points[0])
        return b+(t-s)*bpm/60
    def bpm(self,b): return next(p[2] for p in reversed(self.points) if p[0]<=b)
    def ev(self,b):
        if b not in self.cache:
            t=self.t(b); ids=np.where(abs(self.times-t)<=.045)[0]
            i=ids[np.argmax(self.onset[ids])]
            self.cache[b]=dict(peakSeconds=round(float(self.times[i]),6),distanceMs=round(float(self.times[i]-t)*1000,3),strength=round(float(self.onset[i]),4))
        return self.cache[b]
    def supported(self,b):
        return 0<=b<=self.last_beat and self.t(b)>=self.analysis['firstActiveSeconds']-.03 and self.ev(b)['strength']>=.20
    def sustain(self,a,b):
        ids=(self.times>=self.t(a))&(self.times<=self.t(b))
        energy=self.energy[:,ids].sum(axis=0)
        return len(energy)>0 and float(np.quantile(energy,.15))>float(np.median(self.energy.sum(axis=0)))*.12

def rhythm(m,diff):
    level=DIFFS.index(diff); events=[]; phrases=[]
    for si,(start,end,key,motion,label) in enumerate(FORM[m.spec['id']]):
        # 変速の半拍・小節途中の境界を丸めない。境界以降は新しいフレーズとして読む。
        for phrase_start in np.arange(start,end,16):
            stop=min(end,phrase_start+16); pattern=RHYTHMS[key][level]
            phrase=dict(startBeat=float(phrase_start),endBeat=float(stop),section=si,label=label,rhythm=key,motion=motion)
            phrases.append(phrase)
            for base in np.arange(phrase_start,stop,4):
                for q in pattern:
                    b=float(base+q)
                    if b>=stop or not m.supported(b): continue
                    events.append(dict(beat=b,phrase=len(phrases)-1,section=si,motion=motion,kind='single',key=key,label=label))
            # フィルは4小節目だけ。表拍骨格の同型反復はそのまま残す。
            if level==2 and key in ('drive','pulse','vocal','answer') and stop-phrase_start>=16 and m.bpm(phrase_start)<210:
                for q in (13.5,14.5):
                    b=float(phrase_start+q)
                    if m.supported(b) and m.ev(b)['strength']>=.50 and not any(e['beat']==b for e in events):
                        events.append(dict(beat=b,phrase=len(phrases)-1,section=si,motion=motion,kind='single',key=key,label=label))
    events.sort(key=lambda e:e['beat'])
    # 手順は拍の流れで決める。左右を小節頭ごとにランダムに切り替えない。
    hand=0
    for e in events:
        e['hand']=hand;hand=1-hand
        e['end']=e['beat']
    # 強いフレーズ頭の両手アクセント。直前直後の細分と衝突する場所には追加しない。
    last_pair=-999;pair_count=0
    for e in list(events):
        if e not in events:continue
        p=phrases[e['phrase']];b=e['beat'];t=m.t(b)
        if e['key'] in ('rest','sparse','outro','doubletime') or abs(b-p['startBeat'])>.001 or b-last_pair<(32 if level<2 else 16): continue
        if m.ev(b)['strength']<.45:continue
        gap=.45 if level==0 else .30
        # 句頭の着地を立てるときは直近の細分を休符にする（乱数的な間引きではない）。
        events=[n for n in events if n is e or abs(m.t(n['beat'])-t)>=gap]
        e['kind']='pair';e['gold']=pair_count%2==0;last_pair=b;pair_count+=1
    # 句末の長音は2回カットに限定。逆手の旋律を残して主旋律側に回復時間を設ける。
    last_long=-999
    for pi,p in enumerate(phrases):
        if p['endBeat']-p['startBeat']<8 or p['rhythm'] in ('rest','doubletime','finish'):continue
        if pi%2 and p['rhythm'] not in ('sparse','slow','outro'):continue
        eligible=[e for e in events if e['phrase']==pi and e['kind']=='single' and p['endBeat']-4<=e['beat']<=p['endBeat']-2]
        for e in reversed(eligible):
            b=e['beat']; length=1 if m.bpm(b)<=100 else 2
            end=b+length
            if b-last_long<16 or end>=m.last_beat or end>=p['endBeat'] or any(b<x[0]<=end for x in m.points):continue
            if not m.sustain(b,end) or m.t(end)-m.t(b)<.55:continue
            e['end']=end;e['kind']='long';last_long=b
            # 占有中と終端直後の同じ手だけを空ける。反対の手は維持する。
            events=[n for n in events if n is e or not (m.t(b)-.30<m.t(n['beat'])<m.t(end)+.36 and
                       (n['hand']==e['hand'] or n['kind']=='pair'))]
            break
    return events,phrases

def notes_for(m,events,phrases,diff):
    notes=[];phase=[0,0];level=DIFFS.index(diff);flicks=collections.Counter()
    for e in events:
        for hand in ((0,1) if e['kind']=='pair' else (e['hand'],)):
            parity=phase[hand]%2;sign=1 if hand==0 else -1
            # 前後の振りがつながる低→高の折返し。幅と斜めの軸はフレーズ内で固定。
            amp=.25 if level==0 else .34
            y=-amp if parity==0 else amp
            x=1.22
            if e['motion']=='diagonal':x+=(-.18 if parity==0 else .18)
            if e['motion']=='arc':x+=[-.18,.02,.18,.02][(phase[hand]//2)%4]
            if e['kind']=='pair':x=max(1.16,x)
            cut_beats=[e['beat']]
            if e['kind']=='long':
                x,y=1.48,-.42
                # 終点まで待つ仕様ではなく、音の余韻内で2回振れる長さ。
                cut_beats.append(e['end'])
            n=dict(beat=e['beat'],time=round(m.t(e['beat'])*1000,4),x=round(sign*x,3),y=y,
                   type='long' if e['kind']=='long' else 'tap',color='gold' if e.get('gold') else ('red' if hand==0 else 'blue'),
                   direction='none',count=len(cut_beats),lengthMs=round((m.t(e['end'])-m.t(e['beat']))*1000+90,4) if e['kind']=='long' else 0)
            p=phrases[e['phrase']];local=e['beat']-p['startBeat']
            accent=level>0 and e['kind']=='single' and e['key'] not in ('sparse','slow','outro','doubletime','rest') and local>=12 and e['beat']%1==0
            # Normal 1フレーズ2本、Hard 4本まで。各手も全体も追加検証で制限する。
            arrow=accent and flicks[e['phrase']]<(2 if level==1 else 4)
            if arrow:flicks[e['phrase']]+=1
            n.update(_hand=hand,_cutBeats=cut_beats,_arrow=arrow,_forced=None,_phrase=e['phrase'],_role=e['label'])
            notes.append(n);phase[hand]+=len(cut_beats)
    return sorted(notes,key=lambda n:(n['time'],n['_hand']))

def limit_flicks(notes,diff):
    """休符ではリセットしない。両手同時も両方数える。"""
    level=DIFFS.index(diff);global_run=0;hand_run=[0,0];recent=[];cap=(0,.12,.22)[level]
    for n in notes:
        h=n['_hand']
        if n['_arrow'] and (global_run>=4 or hand_run[h]>=3 or sum(recent[-15:])>=int(16*cap)):
            n['_arrow']=False
        value=int(n['_arrow']);global_run=global_run+1 if value else 0;hand_run[h]=hand_run[h]+1 if value else 0
        recent.append(value)

def build(m,diff,metadata):
    events,phrases=rhythm(m,diff);notes=apply_layout(notes_for(m,events,phrases,diff),m.spec['id'],diff)
    limit_flicks(notes,diff)
    repairs=[]
    # 音の削除で都合を合わせず、成立しない方向指定だけを自由タップへ戻す。
    for _ in range(len(notes)):
        plan,failure=path_for(notes,m)
        if failure is None:break
        i=failure[0];n=notes[i]
        if n['_arrow']:
            n['_arrow']=False;repairs.append(dict(beat=n['beat'],action='方向指定を解除'))
        else:
            raise AssertionError(('手順の設計を修正する必要あり',m.spec['id'],diff,failure,n))
    assert failure is None
    for i,n in enumerate(notes):
        if n['_arrow']:n['direction']=plan[i]['directions'][0];n['type']='direction'
    result=copy.deepcopy(metadata)
    result['spatialLayoutVersion']=1
    result['_comment']='Beat Saber参照・フレーズ単位の反復と応答。フリックは句末に限定し、全体/各手の連続数を検証。'
    result['notes']=[{k:v for k,v in n.items() if not k.startswith('_')} for n in notes]
    result['editorMarkers']=[dict(timeMs=round(m.t(a)*1000,4),name=label) for a,b,k,motion,label in FORM[m.spec['id']]]
    evidence=[dict(index=i,beat=n['beat'],hand='red' if n['_hand']==0 else 'blue',role=n['_role'],phrase=n['_phrase'],**m.ev(n['beat'])) for i,n in enumerate(notes)]
    summary=dict(notes=len(notes),cuts=sum(n['count'] for n in notes),types=dict(collections.Counter(n['type'] for n in notes)),
                 phrases=phrases,recoveryAdjustments=repairs,flowPassed=True)
    return result,dict(notes=plan),evidence,summary

def main():
    p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--features',type=Path);p.add_argument('--output',type=Path,required=True)
    args=p.parse_args();design=args.repo/'Tools/ChartDesign/FiveSongs20261014';summaries={}
    features=args.features or args.repo/'Library/FiveSongRechartAnalysis'
    for spec in json.loads((design/'songs.json').read_text(encoding='utf8')):
        folder=args.repo/'Assets/StreamingAssets/Songs'/spec['id']
        audio=next(x for x in folder.glob('audio.*') if x.suffix!='.meta')
        if not (features/spec['id']/'features.npz').is_file():analyze(spec,audio,features/spec['id'])
        m=Music(spec,features)
        assert hashlib.sha256(audio.read_bytes()).hexdigest()==m.analysis['sha256'], '音源解析の更新が必要'
        summaries[spec['id']]={}
        for diff in DIFFS:
            metadata=json.loads((folder/f'chart_{diff}.json').read_text(encoding='utf8'))
            chart,plan,evidence,summary=build(m,diff,metadata)
            target=args.output/'Assets/StreamingAssets/Songs'/spec['id']
            save(target/f'chart_{diff}.json',chart)
            if diff=='normal':save(target/'chart.json',chart)
            target=args.output/'Tools/ChartDesign/FiveSongs20261014'/spec['id']
            save(target/f'{diff}-cut-plan.json',plan);save(target/f'{diff}-evidence.json',evidence)
            summaries[spec['id']][diff]=summary
            print(spec['id'],diff,summary['notes'],summary['types'],'repairs',len(summary['recoveryAdjustments']),flush=True)
    save(args.output/'Tools/ChartDesign/FiveSongs20261014/audit.json',summaries)

if __name__=='__main__':main()
