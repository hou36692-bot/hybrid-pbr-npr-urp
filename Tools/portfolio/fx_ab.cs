// Screen-space effect A/B for the README: each HybridRP effect off vs on in one framing, plus the outline mask.
// Switches the effects through a temporary global Volume (priority 1000) that overrides only the component under
// test, so no profile asset is modified; the volume is destroyed afterwards. Foliage wind is frozen (property block
// on the animated branch material) so the pair differs only in the effect. Writes Captures/ab_fx/<effect>_<state>.png.
UnityEditor.ShaderUtil.allowAsyncCompilation = false;
var rig = HybridRP.ShowcaseRig.Find();
var drv = HybridRP.HybridCharacterDriver.Find();
var frozen = new System.Collections.Generic.List<Renderer>();
foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
  foreach (var m in r.sharedMaterials) if (m != null && m.HasProperty("_Wind") && m.GetFloat("_Wind") > 0f) { frozen.Add(r); break; }
GameObject go = null;
UnityEngine.Rendering.VolumeProfile profile = null;
var rs = rig.Capture();
var ds = drv.CaptureState();
var sb = new System.Text.StringBuilder();
System.Action<string> shot = name => {
  for (int i = 0; i < 2; i++) {
    var img = HybridRP.EditorTools.HybridCapture.Render(null, null, 1600, 900, 4);
    if (i == 1) sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, "ab_fx/" + name));
    UnityEngine.Object.DestroyImmediate(img);
  }
};
try {
  var block = new MaterialPropertyBlock(); block.SetFloat("_Wind", 0f);
  foreach (var r in frozen) r.SetPropertyBlock(block);
  go = new GameObject("__ab_fx_override");
  go.hideFlags = HideFlags.DontSave;
  var vol = go.AddComponent<UnityEngine.Rendering.Volume>();
  vol.isGlobal = true; vol.priority = 1000f; vol.weight = 1f;
  profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
  vol.sharedProfile = profile;
  // GTAO and contact shadows: ContactShadow framing, low ambient makes occlusion readable.
  rig.Apply("ContactShadow"); rig.ApplyLighting("ThreeQuarter");
  shot("gtao_on");
  var gtao = profile.Add<HybridRP.HybridGTAO>(); gtao.enable.Override(false);
  shot("gtao_off");
  profile.Remove<HybridRP.HybridGTAO>();
  var cs = profile.Add<HybridRP.HybridContactShadows>(); cs.enable.Override(false);
  shot("contact_off");
  profile.Remove<HybridRP.HybridContactShadows>();
  // Volumetric fog: the wide courtyard, where the depth layers read.
  rig.Apply("Wide"); rig.ApplyLighting("Golden");
  shot("fog_on");
  var fog = profile.Add<HybridRP.HybridVolumetricFog>(); fog.enable.Override(false);
  shot("fog_off");
  profile.Remove<HybridRP.HybridVolumetricFog>();
  // Outline: Medium framing without the lens, so the line itself is sharp; plus the debug mask.
  rig.Apply("Portrait"); rig.ApplyLighting("ThreeQuarter");
  shot("outline_on");
  drv.SetOutlineWeight(0f);
  shot("outline_off");
  drv.SetOutlineWeight(1f);
  drv.SetDebugView(11);
  shot("outline_mask");
  drv.SetDebugView(0);
} finally {
  if (go != null) UnityEngine.Object.DestroyImmediate(go);
  if (profile != null) UnityEngine.Object.DestroyImmediate(profile);
  foreach (var r in frozen) r.SetPropertyBlock(null);
  drv.RestoreState(ds); rig.Restore(rs);
}
return sb.ToString();
