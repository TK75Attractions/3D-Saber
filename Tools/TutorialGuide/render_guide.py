"""選曲案内と同じ模型を両手持ちで撮影。固定胴体の左右半分を独立再生する。"""
import bpy
import math
import os
import sys
from mathutils import Vector, Matrix

source, output = sys.argv[sys.argv.index('--') + 1:][:2]
output = os.path.abspath(output)
bpy.ops.wm.open_mainfile(filepath=os.path.abspath(source))
scene = bpy.context.scene
scene.frame_set(1)
rig = bpy.data.objects['GuideSkeleton']
rig.animation_data_clear()
for o in list(scene.objects):
    if o.type in {'CAMERA', 'LIGHT'}:
        bpy.data.objects.remove(o, do_unlink=True)
for b in rig.pose.bones:
    for c in list(b.constraints):
        b.constraints.remove(c)
rest = {b.name: b.matrix.copy() for b in rig.pose.bones}
bases = {b.name: b.matrix_basis.copy() for b in rig.pose.bones}
mirror = Matrix.Diagonal((-1, 1, 1, 1))

def material_copy(name, color):
    m = bpy.data.materials[name].copy()
    m.name = name + '_Blue'
    m.diffuse_color = color
    if m.use_nodes:
        for n in m.node_tree.nodes:
            if n.type == 'BSDF_PRINCIPLED':
                n.inputs['Base Color'].default_value = color
                n.inputs['Emission Color'].default_value = color
    return m

blue = material_copy('Guide_Coral', (.025, .27, 1, 1))
blue_blade = material_copy('Guide_Blade', (.02, .25, 1, 1))
blue_core = material_copy('Guide_Core', (.55, .8, 1, 1))
body = bpy.data.objects['GuideHumanBody']
body.data.materials.append(blue)
blue_index = len(body.data.materials) - 1
left_groups = {g.index for g in body.vertex_groups if g.name.endswith('_l') and
               any(k in g.name for k in ['arm', 'hand', 'index', 'middle', 'pinky', 'ring', 'thumb'])}
right_groups = {g.index for g in body.vertex_groups if g.name.endswith('_r') and
                any(k in g.name for k in ['arm', 'hand', 'index', 'middle', 'pinky', 'ring', 'thumb'])}
coral_index = next(i for i, m in enumerate(body.data.materials) if m.name == 'Guide_Coral')
for p in body.data.polygons:
    weight = sum(sum(g.weight for g in body.data.vertices[v].groups if g.group in left_groups) for v in p.vertices) / len(p.vertices)
    if weight > .5:
        p.material_index = blue_index
    elif sum(sum(g.weight for g in body.data.vertices[v].groups if g.group in right_groups) for v in p.vertices) / len(p.vertices) > .5:
        p.material_index = coral_index

right_grip = bpy.data.objects['RightHandGrip']
right_grip.parent = None
left_grip = bpy.data.objects.new('LeftHandGrip', None)
scene.collection.objects.link(left_grip)
for o in list(right_grip.children):
    clone = o.copy()
    clone.data = o.data.copy()
    scene.collection.objects.link(clone)
    clone.parent = left_grip
    for slot in clone.material_slots:
        if slot.material.name == 'Guide_Blade': slot.material = blue_blade
        elif slot.material.name == 'Guide_Core': slot.material = blue_core
        elif slot.material.name == 'Guide_Coral': slot.material = blue

def aim(o, point):
    o.rotation_euler = (Vector(point) - o.location).to_track_quat('-Z', 'Y').to_euler()

bpy.ops.object.camera_add(location=(0, 6, 2.35))
camera = bpy.context.object
aim(camera, (0, 0, 1.2))
camera.data.type = 'ORTHO'
camera.data.ortho_scale = 3.25
scene.camera = camera
for pos, power in [((-3, 3, 5), 420), ((3, 3, 4), 420), ((0, -3, 4), 240)]:
    bpy.ops.object.light_add(type='AREA', location=pos)
    lamp = bpy.context.object
    lamp.data.energy = power
    lamp.data.size = 4
    aim(lamp, (0, 0, 1))
scene.world = bpy.data.worlds.new('TutorialStudio')
scene.world.use_nodes = True
next(n for n in scene.world.node_tree.nodes if n.type == 'BACKGROUND').inputs[1].default_value = .5
scene.render.engine = 'CYCLES'
scene.cycles.samples = 16
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = 512
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
scene.view_settings.view_transform = 'AgX'
os.makedirs(output, exist_ok=True)

def point_bone(name, head, direction):
    original = rest[name]
    old_dir = original.to_3x3() @ Vector((0, 1, 0))
    rotate = old_dir.rotation_difference(Vector(direction).normalized()).to_matrix().to_4x4()
    matrix = rotate @ original
    matrix.translation = head
    rig.pose.bones[name].matrix = matrix
    bpy.context.view_layer.update()

poses = [0, 8, 15] if '--preview' in sys.argv else range(16)
for index in poses:
    t = index / 15
    for name, matrix in bases.items(): rig.pose.bones[name].matrix_basis = matrix
    bpy.context.view_layer.update()
    point_bone('upperarm_r', rest['upperarm_r'].translation, (-.48, -.26, -1 + 1.65 * t))
    point_bone('lowerarm_r', rig.pose.bones['upperarm_r'].tail, (-.2, -.5, -1 + 2.3 * t))
    wrist = rig.pose.bones['lowerarm_r'].tail.copy()
    point_bone('hand_r', wrist, (0, -.9, -.4 + .8 * t))
    # 左右の骨の固有軸を保って動かす。指は親の手に追従し、胴体の継ぎ目は全コマ固定。
    point_bone('upperarm_l', rest['upperarm_l'].translation, (.48, -.26, -1 + 1.65 * t))
    point_bone('lowerarm_l', rig.pose.bones['upperarm_l'].tail, (.2, -.5, -1 + 2.3 * t))
    left_wrist = rig.pose.bones['lowerarm_l'].tail.copy()
    point_bone('hand_l', left_wrist, (0, -.9, -.4 + .8 * t))
    blade_dir = Vector((-.08, -.45, -1 + 2 * t)).normalized()
    right_grip.matrix_world = Matrix.Translation(wrist + Vector((0, -.025, 0))) @ blade_dir.to_track_quat('Z', 'Y').to_matrix().to_4x4()
    left_grip.matrix_world = mirror @ right_grip.matrix_world @ mirror
    left_grip.location = left_wrist + Vector((0, -.025, 0))
    scene.render.filepath = os.path.join(output, f'pose-{index:02}.png')
    bpy.ops.render.render(write_still=True)
    print(f'TUTORIAL POSE {index + 1}/16', flush=True)
