// For each lens framing: a 1920x1080 render with the rig's depth of field and one without (lens volume
// weight 0), back to back. Writes Captures/__DIR__/dofpair_<name>_dof.png / _nodof.png.
// __FRAMINGS__ entries: ("name","Bookmark","Preset"),
UnityEditor.ShaderUtil.allowAsyncCompilation = false;
var rig = HybridRP.ShowcaseRig.Find();
UnityEngine.Rendering.Volume lens = null;
foreach (var v in UnityEngine.Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None))
  if (v.gameObject.name == "Lens (rig-driven depth of field)") lens = v;
var rs = rig.Capture();
var sb = new System.Text.StringBuilder();
try {
  foreach (var (n, b, p) in new[]{ __FRAMINGS__ }) {
    var dof = HybridRP.EditorTools.HybridCapture.Render(b, p, 1920, 1080, 4);
    sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(dof, "__DIR__/dofpair_" + n + "_dof") + " lensWeight=" + lens.weight);
    UnityEngine.Object.DestroyImmediate(dof);
    lens.weight = 0f;
    var sharp = HybridRP.EditorTools.HybridCapture.Render(null, null, 1920, 1080, 4);
    sb.AppendLine(HybridRP.EditorTools.HybridCapture.Save(sharp, "__DIR__/dofpair_" + n + "_nodof"));
    UnityEngine.Object.DestroyImmediate(sharp);
  }
} finally { rig.Restore(rs); }
return sb.ToString();
