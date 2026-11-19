# Tools

Scripts that drive the open Unity Editor and produce the comparison sheets, final renders and GIFs. They run
from the Unity project root (the folder that contains `Assets/`), with this `Tools` folder copied into it.

## Driving the Editor

`ue.sh` wraps the [Unity CLI](https://docs.unity.com/) and needs the project's `com.unity.pipeline` package and
Node on `PATH`:

```bash
Tools/ue.sh eval 'return HybridRP.EditorTools.HybridCapture.Shot("check_face", "Face", "Front");'
Tools/ue.sh recompile        # recompile scripts and report errors
Tools/ue.sh errors           # console warnings and errors, deduplicated
```

The project path defaults to the parent of this folder; set `UNITY_PROJECT` to override it.
The `.cs` files below are not compiled into the project: they are C# bodies that `ue.sh eval` runs in the Editor,
for example `Tools/ue.sh eval "$(cat Tools/portfolio/v5_check.cs)" 1500`. Files with `__PLACEHOLDERS__` are
filled in with `sed` first (see the comment at the top of each file).

## Images

| File | What it does |
|---|---|
| `compose.ps1` | Labelled comparison sheet from several captures (System.Drawing, Windows) |
| `portfolio/fitsheet.ps1` | Contact sheet with letterboxed cells and optional per-image crops |
| `portfolio/downsample.ps1` | 2x box-filter downsample for supersampled finals |
| `portfolio/dofpair.cs`, `charmask.cs`, `dofcomposite.cs` | Lens finals: URP's Bokeh blur radius is capped at 14 px of the render height, so a 2x supersampled render loses most of the blur. The out-of-focus pixels come from a 1080p render with the lens, the in-focus character from the supersampled render, masked by the character silhouette |
| `portfolio/v5_check.cs`, `light_sweep.cs` | Baseline frames and the key-light sweep at fixed bookmarks and presets |
| `portfolio/ab_run.sh` + `ab_experiment.cs` | Parameter A/B: every variant starts from the current scene state, which is restored afterwards |
| `portfolio/fx_ab.cs` | Each screen-space effect off vs on, through a temporary high-priority Volume (no asset is modified) |
| `portfolio/gif_frames.cs` | Frame sequences for the GIFs (key-light sweep, sunset, terms toggle) |
| `portfolio/make_gif.js`, `web_images.js` | Frame sequence to looping GIF, and renders to web JPGs and labelled panels (Node; needs `gifenc`, `pngjs`, `jpeg-js` on `NODE_PATH`) |

Captures are deterministic: `fx_ab.cs` and `gif_frames.cs` freeze the vendor foliage's wind with a
MaterialPropertyBlock for the duration of the capture, so two captures of the same state are pixel-identical.

## Asset conversion

`pmx2fbx.py` converts a PMX (MMD) model to a Unity-ready FBX in Blender with
[mmd_tools](https://github.com/MMD-Blender/blender_mmd_tools):

```bash
blender --background --factory-startup --python Tools/pmx2fbx.py -- input.pmx output.fbx
```

It imports at metre scale, limits skin weights to 4 per vertex, rebuilds the MMD materials as Principled BSDF
(baking diffuse colour and alpha into textures where needed) and exports Y-up / −Z-forward with embedded textures.
