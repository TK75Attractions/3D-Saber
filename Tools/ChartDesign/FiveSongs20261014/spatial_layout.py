"""横配置を土台にする。縦・反転は選んだ短い素材フレーズで使う。"""
import math

def rotate(x,y,degrees,scale=1):
    a=math.radians(degrees);c,s=math.cos(a),math.sin(a)
    return round(scale*(x*c-y*s),4),round(scale*(x*s+y*c),4)

def angle_at(song,beat,difficulty):
    # 曲全体の周期回転はしない。特殊配置は素材の明示的な座標で設計する。
    return 0

def apply_layout(notes,song,difficulty,scale=.80):
    for n in notes:
        n['x'],n['y']=rotate(n['x'],n['y'],0,scale)
        n['_forced']=None
    return notes
