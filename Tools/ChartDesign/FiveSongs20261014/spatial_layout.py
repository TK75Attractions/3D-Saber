"""色と画面の位置を分離し、フレーズで横・縦・反転・斜めへ展開する。"""
import math,collections

SEQUENCES={
 'ManicSonic':(0,90,180,270,360,270,180,90),
 'escapade':(0,-90,-180,-90,0,90,180,270),
 'logic':(0,90,180,90,0,-90,-180,-270),
 'BenkyouRemaster':(0,-90,-180,-270,-360,-270,-180,-90),
 'Aobasou':(0,90,180,270,360,450,540,630),
}

def rotate(x,y,degrees,scale=1):
    a=math.radians(degrees);c,s=math.cos(a),math.sin(a)
    return round(scale*(x*c-y*s),4),round(scale*(x*s+y*c),4)

def angle_at(song,beat,difficulty):
    # 4小節は同じ軸で反復し、次の4小節で斜めを経由して受け渡す。
    # Easyも配置を固定せず、同じ音型のまとまりを長く読めるようにする。
    sequence=SEQUENCES[song];block=max(0,int(beat//32));q=beat%32
    a=sequence[block%len(sequence)];b=sequence[(block+1)%len(sequence)]
    delta=(b-a+180)%360-180
    u=max(0,(q-16)/16);u=u*u*(3-2*u)
    return a+delta*u

def apply_layout(notes,song,difficulty,scale=.80):
    groups=collections.defaultdict(list)
    for n in notes:
        n['x'],n['y']=rotate(n['x'],n['y'],angle_at(song,n['beat'],difficulty),scale)
        # 以前の画面座標に対する方向を固定せず、色ごとの経路で選び直す。
        n['_forced']=None
        groups[n['time']].append(n)
    for group in groups.values():
        if len(group)!=2:continue
        angle=angle_at(song,group[0]['beat'],difficulty)
        if abs(abs(angle)%180-90)<.001:
            # 縦軸を保持するフレーズでは、上下の同時打ちを同じ列にそろえる。
            x=round(sum(n['x'] for n in group)/2,4)
            for n in group:n['x']=x
    return notes
