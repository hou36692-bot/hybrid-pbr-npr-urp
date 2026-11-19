// Frame sequences for the portfolio GIFs, rendered with the foliage wind frozen (a property block on the
// renderers of the one animated branch material, removed afterwards) so frames differ only in what is being
// demonstrated. Writes Captures/gif/<sequence>/f###.png. Restores the rig, sun, driver and blocks.
//   sweep:  Portrait, key light orbiting her from left (-80) through front to right (+80), elevation 35.
//   golden: Medium (lens), sun elevation falling from 40 to 18 degrees with the Golden preset's colour.
//   terms:  CloseUp; v4/v5 character terms off <-> on, as a two-frame toggle.
// Placeholders: __SEQ__ (sweep | golden | terms), __FRAMES__ (frame count), __W__ __H__ (frame size).
UnityEditor.ShaderUtil.allowAsyncCompilation = false;
var rig = HybridRP.ShowcaseRig.Find();
var drv = HybridRP.HybridCharacterDriver.Find();
Light sun = null;
foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
  if (l.type == LightType.Directional && l.isActiveAndEnabled && (sun == null || l.intensity > sun.intensity)) sun = l;
var frozen = new System.Collections.Generic.List<Renderer>();
foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
  foreach (var m in r.sharedMaterials) if (m != null && m.HasProperty("_Wind") && m.GetFloat("_Wind") > 0f) { frozen.Add(r); break; }
var rs = rig.Capture();
var ds = drv.CaptureState();
var sb = new System.Text.StringBuilder();
sb.AppendLine("frozen renderers=" + frozen.Count);
System.Action<float, float> aimSun = (yaw, elevation) => {
  Vector3 fwd = Vector3.ProjectOnPlane(drv.transform.forward, Vector3.up).normalized;
  Vector3 toLight = Quaternion.AngleAxis(yaw, Vector3.up) * fwd;
  Vector3 axis = Vector3.Cross(Vector3.up, toLight);
  toLight = Quaternion.AngleAxis(-elevation, axis) * toLight;
  sun.transform.rotation = Quaternion.LookRotation(-toLight, Vector3.up);
};
System.Action<string, int> save = (seq, i) => {
  var img = HybridRP.EditorTools.HybridCapture.Render(null, null, __W__, __H__, 3);
  sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, "gif/" + seq + "/f" + i.ToString("000")));
  UnityEngine.Object.DestroyImmediate(img);
};
try {
  var block = new MaterialPropertyBlock();
  block.SetFloat("_Wind", 0f);
  foreach (var r in frozen) r.SetPropertyBlock(block);
  if ("__SEQ__" == "sweep") {
    rig.Apply("Portrait"); rig.ApplyLighting("ThreeQuarter");
    int n = __FRAMES__;
    aimSun(-80f, 35f); var warm = HybridRP.EditorTools.HybridCapture.Render(null, null, 64, 36, 2); UnityEngine.Object.DestroyImmediate(warm);
    for (int i = 0; i < n; i++) { aimSun(Mathf.Lerp(-80f, 80f, i / (float)(n - 1)), 35f); save("sweep", i); }
  } else if ("__SEQ__" == "golden") {
    rig.Apply("Medium"); rig.ApplyLighting("Golden");
    int n = __FRAMES__;
    aimSun(-40f, 40f); var warm = HybridRP.EditorTools.HybridCapture.Render(null, null, 64, 36, 2); UnityEngine.Object.DestroyImmediate(warm);
    for (int i = 0; i < n; i++) { aimSun(-40f, Mathf.Lerp(40f, 18f, i / (float)(n - 1))); save("golden", i); }
  } else if ("__SEQ__" == "terms") {
    rig.Apply("CloseUp"); rig.ApplyLighting("ThreeQuarter");
    var warm = HybridRP.EditorTools.HybridCapture.Render(null, null, 64, 36, 2); UnityEngine.Object.DestroyImmediate(warm);
    drv.SetCharacterExposure(1f, 1f, 1f); drv.SetHighlightLightDrive(0f); drv.SetStylisedShadows(0f, 0f, 0f, 2f);
    save("terms", 0);
    drv.RestoreState(ds);
    save("terms", 1);
  }
} finally {
  foreach (var r in frozen) r.SetPropertyBlock(null);
  drv.RestoreState(ds); rig.Restore(rs);
}
return sb.ToString();
