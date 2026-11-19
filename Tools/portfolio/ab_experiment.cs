// Parameter A/B harness (driven by ab_run.sh, which fills the placeholders). Renders every shot under every
// variant. Each variant starts from the scene's current state (character driver, Wuling grade, rig lenses),
// which is snapshotted first and restored at the end, so the first variant ("base", an empty action) is the
// reference.
//   __TAG__       output folder under Captures/ab/
//   __VARIANTS__  ("name", (System.Action)(() => { drv.SetShadowLift...; ca.postExposure.value = ...; setLens("Hero", 4f, eyes); })),
//   __SHOTS__     ("label", "Bookmark", "Preset", yaw, elevation),   yaw = 999 keeps the preset's sun
//   __W__ __H__   cell size
// Re-skin the character in a separate eval before running this (re-enable its SkinnedMeshRenderer and let
// the Editor tick): re-enabling it in the same eval renders the bind pose.
UnityEditor.ShaderUtil.allowAsyncCompilation = false;
var rig = HybridRP.ShowcaseRig.Find();
var drv = HybridRP.HybridCharacterDriver.Find();
Light sun = null;
foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
  if (l.type == LightType.Directional && l.isActiveAndEnabled && (sun == null || l.intensity > sun.intensity)) sun = l;
var grade = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.VolumeProfile>(HybridRP.EditorTools.WulingShowcaseSetup.GradeProfilePath);
UnityEngine.Rendering.Universal.ColorAdjustments ca;
grade.TryGet(out ca);
UnityEngine.Rendering.Universal.ShadowsMidtonesHighlights smh;
grade.TryGet(out smh);
float pe0 = ca.postExposure.value;
Vector4 smhShadows0 = smh.shadows.value;
string driver0 = UnityEditor.EditorJsonUtility.ToJson(drv);
var rigSo = new UnityEditor.SerializedObject(rig);
var bookmarkList = rigSo.FindProperty("_bookmarks");
var lens0 = new System.Collections.Generic.Dictionary<string, (float aperture, Vector3 focus)>();
for (int i = 0; i < bookmarkList.arraySize; i++) {
  var b = bookmarkList.GetArrayElementAtIndex(i);
  lens0[b.FindPropertyRelative("name").stringValue] = (b.FindPropertyRelative("aperture").floatValue, b.FindPropertyRelative("focusPoint").vector3Value);
}
System.Action<string, float, Vector3> setLens = (name, aperture, focus) => {
  for (int i = 0; i < bookmarkList.arraySize; i++) {
    var b = bookmarkList.GetArrayElementAtIndex(i);
    if (b.FindPropertyRelative("name").stringValue != name) continue;
    b.FindPropertyRelative("aperture").floatValue = aperture;
    b.FindPropertyRelative("focusPoint").vector3Value = focus;
  }
  rigSo.ApplyModifiedPropertiesWithoutUndo();
};
var eyes = new Vector3(0f, 1.58f, 0.08f);
System.Action reset = () => {
  // The driver publishes its globals in OnEnable, so a disable/enable re-publishes the restored fields.
  UnityEditor.EditorJsonUtility.FromJsonOverwrite(driver0, drv);
  drv.enabled = false;
  drv.enabled = true;
  ca.postExposure.value = pe0;
  smh.shadows.value = smhShadows0;
  foreach (var kv in lens0) setLens(kv.Key, kv.Value.aperture, kv.Value.focus);
};
var variants = new (string name, System.Action apply)[] { __VARIANTS__ };
var shots = new (string label, string bookmark, string preset, float yaw, float elev)[] { __SHOTS__ };
var rs = rig.Capture();
var sb = new System.Text.StringBuilder();
try {
  foreach (var s in shots)
  for (int vi = -1; vi < variants.Length; vi++) {
    // vi = -1 is a throwaway render: temporal history needs a frame after the camera and sun change,
    // otherwise the first cell of each row is not comparable with the others.
    var v = variants[System.Math.Max(vi, 0)];
    reset();
    v.apply();
    rig.ApplyLighting(s.preset);
    rig.Apply(s.bookmark);
    if (s.yaw < 900f) {
      Vector3 fwd = Vector3.ProjectOnPlane(drv.transform.forward, Vector3.up).normalized;
      Vector3 toLight = Quaternion.AngleAxis(s.yaw, Vector3.up) * fwd;
      Vector3 axis = Vector3.Cross(Vector3.up, toLight);
      toLight = Quaternion.AngleAxis(-s.elev, axis) * toLight;
      sun.transform.rotation = Quaternion.LookRotation(-toLight, Vector3.up);
    }
    var img = HybridRP.EditorTools.HybridCapture.Render(null, null, __W__, __H__, 6);
    if (vi >= 0) sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(img, $"ab/__TAG__/{s.label}__{v.name}"));
    UnityEngine.Object.DestroyImmediate(img);
  }
} finally { reset(); rig.Restore(rs); }
return sb.ToString();
