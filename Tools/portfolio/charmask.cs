// Character coverage masks for the lens framings: the character alone (albedo debug view, on an unused
// layer that the camera alone renders) minus an empty frame with the same settings, at 1920x1080.
// Writes Captures/__DIR__/charmask_<n>_char.png and _empty.png. Restores layers, culling, lens and debug view.
// Placeholders: __FRAMINGS__ entries ("name","Bookmark","Preset"), __DIR__ (capture folder under Captures/).
UnityEditor.ShaderUtil.allowAsyncCompilation = false;
var rig = HybridRP.ShowcaseRig.Find();
var drv = HybridRP.HybridCharacterDriver.Find();
UnityEngine.Rendering.Volume lens = null;
foreach (var v in UnityEngine.Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None))
  if (v.gameObject.name == "Lens (rig-driven depth of field)") lens = v;
const int MaskLayer = 31;
var gos = drv.GetComponentsInChildren<Transform>(true);
var oldLayers = new int[gos.Length];
for (int i = 0; i < gos.Length; i++) oldLayers[i] = gos[i].gameObject.layer;
var cam = rig.Camera;
int oldMask = cam.cullingMask;
var rs = rig.Capture();
var ds = drv.CaptureState();
var sb = new System.Text.StringBuilder();
try {
  foreach (var (n, b, p) in new[]{ __FRAMINGS__ }) {
    rig.Apply(b); rig.ApplyLighting(p); lens.weight = 0f;
    drv.SetDebugView(1);
    foreach (var t in gos) t.gameObject.layer = MaskLayer;
    cam.cullingMask = 1 << MaskLayer;
    var img = HybridRP.EditorTools.HybridCapture.Render(null, null, 1920, 1080, 1);
    sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, "__DIR__/charmask_" + n + "_char")); UnityEngine.Object.DestroyImmediate(img);
    cam.cullingMask = 0;
    img = HybridRP.EditorTools.HybridCapture.Render(null, null, 1920, 1080, 1);
    sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, "__DIR__/charmask_" + n + "_empty")); UnityEngine.Object.DestroyImmediate(img);
    cam.cullingMask = oldMask;
    for (int i = 0; i < gos.Length; i++) gos[i].gameObject.layer = oldLayers[i];
  }
} finally {
  cam.cullingMask = oldMask;
  for (int i = 0; i < gos.Length; i++) gos[i].gameObject.layer = oldLayers[i];
  drv.RestoreState(ds); drv.SetDebugView(0); rig.Restore(rs);
}
return sb.ToString();
