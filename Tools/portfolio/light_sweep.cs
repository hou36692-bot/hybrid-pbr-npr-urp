// Baseline key-light sweep at the current (accepted) state: Portrait framing, sun 2.6 warm at elevation 35,
// from the character's left (-80), front (0) and right (+80). Writes Captures/baseline_12_v4/wuling_sweep_<side>.png.
UnityEditor.ShaderUtil.allowAsyncCompilation = false;
var rig = HybridRP.ShowcaseRig.Find();
var drv = HybridRP.HybridCharacterDriver.Find();
Light sun = null;
foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
  if (l.type == LightType.Directional && l.isActiveAndEnabled && (sun == null || l.intensity > sun.intensity)) sun = l;
var rs = rig.Capture();
var sb = new System.Text.StringBuilder();
try {
  rig.Apply("Portrait");
  rig.ApplyLighting("ThreeQuarter");
  Vector3 fwd = Vector3.ProjectOnPlane(drv.transform.forward, Vector3.up).normalized;
  foreach (var (side, yaw) in new[]{ ("left", -80f), ("front", 0f), ("right", 80f) }) {
    Vector3 toLight = Quaternion.AngleAxis(yaw, Vector3.up) * fwd;
    Vector3 axis = Vector3.Cross(Vector3.up, toLight);
    toLight = Quaternion.AngleAxis(-35f, axis) * toLight;
    sun.transform.rotation = Quaternion.LookRotation(-toLight, Vector3.up);
    var img = HybridRP.EditorTools.HybridCapture.Render(null, null, 1920, 1080, 4);
    sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, "baseline_12_v4/wuling_sweep_" + side));
    UnityEngine.Object.DestroyImmediate(img);
  }
} finally { rig.Restore(rs); }
return sb.ToString();
