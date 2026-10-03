"""保存済み人体模型の動作を、固定カメラの透過PNG 24コマとして書き出す。"""
import bpy
import math
import os
import sys
from mathutils import Vector

args = sys.argv[sys.argv.index('--') + 1:]
source, output = [os.path.abspath(path) for path in args]
bpy.ops.wm.open_mainfile(filepath=source)
scene = bpy.context.scene
os.makedirs(output, exist_ok=True)

def aim(obj, point):
    obj.rotation_euler = (Vector(point) - obj.location).to_track_quat('-Z', 'Y').to_euler()

for obj in list(scene.objects):
    if obj.type in {'CAMERA', 'LIGHT'}:
        bpy.data.objects.remove(obj, do_unlink=True)
bpy.ops.object.camera_add(location=(-3, 5, 2.7))
camera = bpy.context.object
camera.name = 'MannequinAnimationCamera'
aim(camera, (-.08, 0, 1.14))
camera.data.type = 'ORTHO'
camera.data.ortho_scale = 2.5
scene.camera = camera
for position, power, size in [((-3, 3, 5), 450, 4), ((3, 1, 3), 200, 3), ((0, -3, 3), 330, 3)]:
    bpy.ops.object.light_add(type='AREA', location=position)
    light = bpy.context.object
    light.data.energy = power
    light.data.shape = 'DISK'
    light.data.size = size
    aim(light, (0, 0, 1))
scene.world = bpy.data.worlds.new('MannequinStudio')
scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs[0].default_value = (.035, .06, .09, 1)
scene.world.node_tree.nodes['Background'].inputs[1].default_value = .5
scene.render.engine = 'CYCLES'
scene.cycles.samples = 32
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = 512
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
scene.render.image_settings.color_depth = '8'
scene.view_settings.view_transform = 'AgX'

# 元の動作の「腕を上げる」区間を均等にサンプリング。復路は同じコマを逆再生する。
for pose in range(24):
    frame = 1 + (.30 + 1.05 * pose / 23) * 30
    scene.frame_set(math.floor(frame), subframe=frame % 1)
    scene.render.filepath = os.path.join(output, f'pose-{pose:02}.png')
    bpy.ops.render.render(write_still=True)
    print(f'MANNEQUIN POSE {pose + 1}/24', flush=True)
print('MANNEQUIN RENDER PASS', flush=True)
