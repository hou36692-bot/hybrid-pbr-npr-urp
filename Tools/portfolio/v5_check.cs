// v5 verification set: the 12_v4 Wuling baseline frames (same bookmark/preset/size/warmup), the new Dappled
// frames and the key-light sweep, into Captures/v5/check/. Each frame is preceded by a throwaway render so
// temporal history settles after the camera/sun change.
UnityEditor.ShaderUtil.allowAsyncCompilation = false;
var rig = HybridRP.ShowcaseRig.Find();
var drv = HybridRP.HybridCharacterDriver.Find();
Light sun = null;
foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
  if (l.type == LightType.Directional && l.isActiveAndEnabled && (sun == null || l.intensity > sun.intensity)) sun = l;
var rs = rig.Capture();
var sb = new System.Text.StringBuilder();
System.Action<string, string, string> shot = (name, bookmark, preset) => {
  for (int i = 0; i < 2; i++) {
    var img = HybridRP.EditorTools.HybridCapture.Render(bookmark, preset, 1920, 1080, 4);
    if (i == 1) sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, "v5/check/" + name));
    UnityEngine.Object.DestroyImmediate(img);
  }
};
try {
  shot("wuling_closeup_threequarter", "CloseUp", "ThreeQuarter");
  shot("wuling_medium_threequarter", "Medium", "ThreeQuarter");
  shot("wuling_profile_golden", "Profile", "Golden");
  shot("wuling_fullbody_golden", "FullBody", "Golden");
  shot("wuling_hero_golden", "Hero", "Golden");
  shot("wuling_lowhero_golden", "LowHero", "Golden");
  shot("wuling_wide_golden", "Wide", "Golden");
  shot("wuling_medium_dappled", "Medium", "Dappled");
  shot("wuling_lowhero_dappled", "LowHero", "Dappled");
  // Key-light sweep, as Tools/portfolio/light_sweep.cs: Portrait, ThreeQuarter colour/ambient, sun 2.6 at
  // elevation 35 from her left (-80), front (0) and right (+80).
  rig.Apply("Portrait");
  rig.ApplyLighting("ThreeQuarter");
  Vector3 fwd = Vector3.ProjectOnPlane(drv.transform.forward, Vector3.up).normalized;
  foreach (var (side, yaw) in new[]{ ("left", -80f), ("front", 0f), ("right", 80f) }) {
    Vector3 toLight = Quaternion.AngleAxis(yaw, Vector3.up) * fwd;
    Vector3 axis = Vector3.Cross(Vector3.up, toLight);
    toLight = Quaternion.AngleAxis(-35f, axis) * toLight;
    sun.transform.rotation = Quaternion.LookRotation(-toLight, Vector3.up);
    for (int i = 0; i < 2; i++) {
      var img = HybridRP.EditorTools.HybridCapture.Render(null, null, 1920, 1080, 4);
      if (i == 1) sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, "v5/check/wuling_sweep_" + side));
      UnityEngine.Object.DestroyImmediate(img);
    }
  }
} finally { rig.Restore(rs); }
return sb.ToString();
