"""PMX -> Unity-ready FBX via blender_mmd_tools.

Usage:
  blender --background --factory-startup --python pmx2fbx.py -- <input.pmx> <output.fbx>

What it does:
  * imports the PMX with MMD Tools (scale 0.08 -> metres)
  * limits skin weights to 4 per vertex (Unity default) and normalises them
  * rebuilds every MMD shader material as Principled BSDF so the FBX carries
    base-colour textures and alpha (bakes MMD diffuse/alpha into textures when
    the material colour is not plain white)
  * exports FBX with Y-up / -Z-forward, textures embedded
"""
import os
import sys
import json

import bpy
import addon_utils
import numpy as np
from mathutils import Matrix

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(argv) < 2:
    raise SystemExit("need: <input.pmx> <output.fbx>")
PMX_PATH = os.path.abspath(argv[0])
FBX_PATH = os.path.abspath(argv[1])
OUT_DIR = os.path.dirname(FBX_PATH)
BAKE_DIR = os.path.join(OUT_DIR, "baked_textures")
os.makedirs(BAKE_DIR, exist_ok=True)

IMPORT_SCALE = 0.08          # MMD unit -> metre
MAX_INFLUENCES = 4           # Unity default Blend Weights
BAKE_SCALE = float(os.environ.get("PMX_BAKE_SCALE", "1"))  # extra geometry scale, usually 1
APPLY_UNIT = os.environ.get("PMX_APPLY_UNIT", "1") == "1"

print(f"[pmx2fbx] input : {PMX_PATH}")
print(f"[pmx2fbx] output: {FBX_PATH}")

bpy.ops.wm.read_factory_settings(use_empty=True)
addon_utils.enable("bl_ext.blender_org.mmd_tools", default_set=False, persistent=True)

res = bpy.ops.mmd_tools.import_model(
    filepath=PMX_PATH,
    scale=IMPORT_SCALE,
    clean_model=True,
    remove_doubles=False,
    rename_bones=False,
    types={'MESH', 'ARMATURE', 'MORPHS'},
    log_level='ERROR',
)
print("[pmx2fbx] import:", res)

armatures = [o for o in bpy.data.objects if o.type == 'ARMATURE']
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
if not armatures or not meshes:
    raise SystemExit("import produced no armature/mesh")
arm_obj = armatures[0]
mesh_obj = meshes[0]
me = mesh_obj.data

# consistent ASCII object names, taken from the output file name
base_name = os.path.splitext(os.path.basename(FBX_PATH))[0]
root_empty = arm_obj.parent or next(
    (o for o in bpy.data.objects if o.type == 'EMPTY' and o.parent is None), None)
if root_empty is not None:
    root_empty.name = base_name
arm_obj.name = base_name + "_Armature"
arm_obj.data.name = base_name + "_Armature"
mesh_obj.name = base_name + "_Mesh"
me.name = base_name + "_Mesh"
print(f"[pmx2fbx] renamed objects to {base_name} / {base_name}_Armature / {base_name}_Mesh")

# ---------------------------------------------------------------- non-bone groups
# mmd_tools stores outline/vertex-order data as vertex groups (mmd_edge_scale,
# mmd_vertex_order). They are not skin weights: drop them before limiting, or they
# eat one of the 4 influence slots on every vertex.
bone_names = {b.name for b in arm_obj.data.bones}
extra_groups = [vg.name for vg in mesh_obj.vertex_groups if vg.name not in bone_names]
for name in extra_groups:
    mesh_obj.vertex_groups.remove(mesh_obj.vertex_groups[name])
if extra_groups:
    print(f"[pmx2fbx] removed non-bone vertex groups: {extra_groups}")

# ---------------------------------------------------------------- weights
bpy.context.view_layer.objects.active = mesh_obj
mesh_obj.select_set(True)
bpy.ops.object.mode_set(mode='OBJECT')
before = sorted({len([g for g in v.groups if g.weight > 1e-4]) for v in me.vertices})
bpy.ops.object.vertex_group_limit_total(limit=MAX_INFLUENCES)
bpy.ops.object.vertex_group_normalize_all(lock_active=False)
after = sorted({len([g for g in v.groups if g.weight > 1e-4]) for v in me.vertices})
print(f"[pmx2fbx] influences/vertex before={before} after={after}")

# ---------------------------------------------------------------- extra UVs
while len(me.uv_layers) > 1:
    print(f"[pmx2fbx] removing unused UV layer {me.uv_layers[-1].name!r}")
    me.uv_layers.remove(me.uv_layers[-1])

# ---------------------------------------------------------------- materials
def src_image_of(mat):
    for n in mat.node_tree.nodes:
        if n.type == 'TEX_IMAGE' and n.name == 'mmd_base_tex':
            return n.image
    return None


def baked_image(src, diffuse, alpha, tag):
    """Multiply an sRGB byte image by the MMD diffuse colour / alpha and save it."""
    w, h = src.size
    if w == 0 or h == 0:
        return None
    buf = np.empty(w * h * 4, dtype=np.float32)
    src.pixels.foreach_get(buf)
    px = buf.reshape(-1, 4)
    px[:, 0] *= diffuse[0]
    px[:, 1] *= diffuse[1]
    px[:, 2] *= diffuse[2]
    px[:, 3] *= alpha
    safe = "".join(c if (c.isalnum() or c in "-_") else "_" for c in tag)[:60]
    path = os.path.join(BAKE_DIR, f"{safe}.png")
    img = bpy.data.images.new(f"{safe}_baked", w, h, alpha=True)
    img.colorspace_settings.name = src.colorspace_settings.name
    img.pixels.foreach_set(px.ravel())
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    print(f"[pmx2fbx] baked {src.name!r} -> {path} (diffuse={diffuse}, alpha={alpha})")
    return img


baked = 0
setup_entries = []
for mat in bpy.data.materials:
    mm = getattr(mat, "mmd_material", None)
    if mm is None:
        continue
    img = src_image_of(mat)
    diffuse = tuple(mm.diffuse_color)
    alpha = float(mm.alpha)
    mmd_alpha = alpha
    white = all(abs(c - 1.0) < 1e-3 for c in diffuse)

    if img is not None and (not white or alpha < 1.0):
        new = baked_image(img, diffuse, alpha, mat.name)
        if new is not None:
            img = new
            baked += 1
            diffuse, alpha = (1.0, 1.0, 1.0), 1.0

    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    out.location = (400, 0)
    bsdf = nt.nodes.new('ShaderNodeBsdfPrincipled')
    bsdf.location = (0, 0)
    nt.links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])
    bsdf.inputs['Roughness'].default_value = 0.65
    bsdf.inputs['Metallic'].default_value = 0.0

    if img is not None:
        tex = nt.nodes.new('ShaderNodeTexImage')
        tex.location = (-350, 100)
        tex.image = img
        tex.interpolation = 'Linear'
        nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
        nt.links.new(tex.outputs['Alpha'], bsdf.inputs['Alpha'])
    else:
        bsdf.inputs['Base Color'].default_value = (*diffuse, 1.0)
        bsdf.inputs['Alpha'].default_value = alpha

    if alpha < 1.0:
        bsdf.inputs['Alpha'].default_value = alpha

    mat.use_backface_culling = not bool(mm.is_double_sided)
    mat.blend_method = 'HASHED' if alpha < 1.0 or img is not None else 'OPAQUE'
    mat.show_transparent_back = False

    mode = "opaque"
    if mmd_alpha < 1.0:
        mode = "transparent"
    elif img is not None:
        mode = "cutout"
    setup_entries.append({
        "name": mat.name,
        "mode": mode,
        "alpha": round(mmd_alpha, 4),
        "double_sided": bool(mm.is_double_sided),
        "texture": os.path.basename(img.filepath) if img is not None and img.filepath else None,
    })
print(f"[pmx2fbx] rebuilt {len(bpy.data.materials)} materials, {baked} baked")

# ---------------------------------------------------------------- material setup json
setup_path = os.path.join(OUT_DIR, "material_setup.json")
with open(setup_path, "w", encoding="utf-8") as fh:
    json.dump({"materials": setup_entries}, fh, ensure_ascii=False, indent=2)
print(f"[pmx2fbx] wrote {setup_path}")

# ---------------------------------------------------------------- save blend (metres)
scene = bpy.context.scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1.0
blend_path = os.path.join(OUT_DIR, os.path.splitext(os.path.basename(FBX_PATH))[0] + ".blend")
bpy.ops.wm.save_as_mainfile(filepath=blend_path)

# Unity reads a Blender FBX as centimetres (UnitScaleFactor 100) and multiplies the
# vertex data by 0.01, so bake a x100 scale into the geometry itself. The FBX then
# imports at real-world size with an identity root transform.
dims_m = tuple(mesh_obj.dimensions)
if BAKE_SCALE != 1.0:
    s = BAKE_SCALE
    me.transform(Matrix.Scale(s, 4))
    if me.shape_keys:
        buf = np.empty(len(me.vertices) * 3, dtype=np.float32)
        for kb in me.shape_keys.key_blocks:
            kb.data.foreach_get('co', buf)
            buf *= s
            kb.data.foreach_set('co', buf)
    arm_obj.data.transform(Matrix.Scale(s, 4))
    print(f"[pmx2fbx] baked x{s} into mesh+armature data, dims now "
          f"{tuple(round(v, 1) for v in mesh_obj.dimensions)} (cm)")

bpy.ops.object.select_all(action='DESELECT')

bpy.ops.export_scene.fbx(
    filepath=FBX_PATH,
    use_selection=False,
    use_visible=False,
    use_active_collection=False,
    global_scale=1.0,
    apply_unit_scale=APPLY_UNIT,
    apply_scale_options='FBX_SCALE_NONE',
    use_space_transform=True,
    bake_space_transform=False,
    object_types={'ARMATURE', 'MESH', 'EMPTY'},
    use_mesh_modifiers=False,
    mesh_smooth_type='OFF',
    colors_type='NONE',
    use_tspace=False,
    use_custom_props=False,
    add_leaf_bones=False,
    primary_bone_axis='Y',
    secondary_bone_axis='X',
    use_armature_deform_only=False,
    bake_anim=False,
    path_mode='COPY',
    embed_textures=False,
    batch_mode='OFF',
    use_metadata=False,
    axis_forward='-Z',
    axis_up='Y',
)
print("[pmx2fbx] exported:", FBX_PATH, os.path.getsize(FBX_PATH), "bytes")

manifest = {
    "source_pmx": PMX_PATH,
    "fbx": FBX_PATH,
    "blend": blend_path,
    "import_scale": IMPORT_SCALE,
    "vertices": len(me.vertices),
    "polygons": len(me.polygons),
    "materials": len(me.materials),
    "shape_keys": len(me.shape_keys.key_blocks) if me.shape_keys else 0,
    "bones_total": len(arm_obj.data.bones),
    "bones_deform": sum(1 for b in arm_obj.data.bones if b.use_deform),
    "vertex_groups": len(mesh_obj.vertex_groups),
    "max_influences": MAX_INFLUENCES,
    "dimensions_m": [round(v, 4) for v in dims_m],
    "baked_textures": baked,
}
with open(os.path.join(OUT_DIR, "import_manifest.json"), "w", encoding="utf-8") as fh:
    json.dump(manifest, fh, ensure_ascii=False, indent=2)
print("[pmx2fbx] manifest:", json.dumps(manifest, ensure_ascii=False))
print("[pmx2fbx] DONE")
