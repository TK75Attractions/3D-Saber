"""本物の音源と新譜面から音合わせ用プレビューを作る。ゲーム本体の音源は変更しない。"""
import json
import subprocess
from pathlib import Path
import numpy as np

ROOT=Path(r'C:\Users\shike\dev\3D-Saber')
OUT=Path(__file__).resolve().parents[2]/'Outputs/YurikagoCharts'
FFMPEG=r'C:\pinokio\bin\miniconda\Library\bin\ffmpeg.exe'
SR=44100


def decode(path,channels):
    raw=subprocess.check_output([FFMPEG,'-v','error','-i',str(path),'-f','f32le','-ac',str(channels),'-ar',str(SR),'pipe:1'])
    return np.frombuffer(raw,dtype='<f4').reshape(-1,channels)


music=decode(ROOT/'Assets/StreamingAssets/Songs/揺籠/audio.mp3',2)
sounds={name:decode(ROOT/f'Assets/Resources/Audio/SFX/Saber_{name}.wav',1)[:,0] for name in ['NoteCut','FlickCut','LongTick','LongFinish','GoldCut']}
for diff in ['easy','normal','hard']:
    chart=json.loads((OUT/f'Charts/chart_{diff}.json').read_text(encoding='utf-8'))
    mixed=music.copy()*.80
    for note in chart['notes']:
        start=(note['time']+chart['offsetMs'])/1000
        pan=max(-.7,min(.7,note['x']/2.5))
        gains=np.array([np.sqrt((1-pan)/2),np.sqrt((1+pan)/2)])
        count=note['count']
        hits=np.linspace(start,start+note['lengthMs']/1000,count) if count>1 else [start]
        for cut_index,t in enumerate(hits):
            if cut_index<count-1:kind='LongTick'
            elif note['color']=='gold':kind='GoldCut'
            elif count>1:kind='LongFinish'
            elif note['direction']!='none':kind='FlickCut'
            else:kind='NoteCut'
            clip=sounds[kind]; at=round(t*SR);length=min(len(clip),len(mixed)-at)
            mixed[at:at+length]+=clip[:length,None]*gains*.32
    peak=float(abs(mixed).max())
    if peak>.95:mixed*=.95/peak
    args=[FFMPEG,'-y','-v','error','-f','f32le','-ar',str(SR),'-ac','2','-i','pipe:0','-c:a','aac','-b:a','192k',str(OUT/f'Yurikago_{diff}_timing_preview.m4a')]
    subprocess.run(args,input=mixed.astype('<f4').tobytes(),check=True)
    print(diff,'peak_before_limiter',round(peak,4),'seconds',len(mixed)/SR,flush=True)
    if diff=='hard':
        # 落ちる区間→フィル→最大ピークの差が一度に分かる40秒。
        clip=mixed[round(130*SR):round(170*SR)]
        args[-1]=str(OUT/'Yurikago_Hard_130-170s_preview.m4a')
        subprocess.run(args,input=clip.astype('<f4').tobytes(),check=True)
