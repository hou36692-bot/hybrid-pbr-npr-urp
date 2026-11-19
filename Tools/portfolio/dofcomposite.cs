// Composites the portfolio finals for the lens framings: in-focus pixels from the 2x supersampled render
// (final_ss2560_<n>.png), out-of-focus pixels from the 1080p depth-of-field render upscaled. URP's Bokeh
// blur radius is capped at 14 px of the render height, so at 2880 px the background was nearly sharp.
// The in-focus mask is where the 1080p renders with and without depth of field agree.
// Placeholders: __NAMES__ ("01_closeup","02_medium"), __DIR__ (capture folder under Captures/), __OUT__ (output folder).
string[] names = { __NAMES__ };
var sb = new System.Text.StringBuilder();
System.Func<string, Texture2D> Load = p => { var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false); t.LoadImage(System.IO.File.ReadAllBytes(p)); t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; return t; };
foreach (var n in names) {
  var dof = Load("Captures/__DIR__/dofpair_" + n + "_dof.png");
  var nod = Load("Captures/__DIR__/dofpair_" + n + "_nodof.png");
  var ss = Load("Captures/__DIR__/final_ss2560_" + n + ".png");
  var chr = Load("Captures/__DIR__/charmask_" + n + "_char.png");
  var emp = Load("Captures/__DIR__/charmask_" + n + "_empty.png");
  int w = dof.width, h = dof.height, W = ss.width, H = ss.height;
  var a = dof.GetPixels32(); var b = nod.GetPixels32(); var ca = chr.GetPixels32(); var cb = emp.GetPixels32();
  var m = new float[w * h]; var cm = new float[w * h];
  for (int i = 0; i < m.Length; i++) {
    int d = Mathf.Max(Mathf.Abs(a[i].r - b[i].r), Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
    m[i] = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 10f, d));
    int e = Mathf.Max(Mathf.Abs(ca[i].r - cb[i].r), Mathf.Max(Mathf.Abs(ca[i].g - cb[i].g), Mathf.Abs(ca[i].b - cb[i].b)));
    cm[i] = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f, 16f, e));
  }
  // Agreement mask: 5x5 box blur so single noisy pixels do not flicker between the two sources.
  // Coverage mask: 3x3, just enough to anti-alias the silhouette at 1080p.
  var m2 = new float[w * h];
  for (int y = 0; y < h; y++)
  for (int x = 0; x < w; x++) {
    float s = 0f, s3 = 0f; int c = 0, c3 = 0;
    for (int dy = -2; dy <= 2; dy++) { int yy = y + dy; if (yy < 0 || yy >= h) continue;
      for (int dx = -2; dx <= 2; dx++) { int xx = x + dx; if (xx < 0 || xx >= w) continue; s += m[yy * w + xx]; c++;
        if (dx >= -1 && dx <= 1 && dy >= -1 && dy <= 1) { s3 += cm[yy * w + xx]; c3++; } } }
    m2[y * w + x] = (s / c) * (s3 / c3);
  }
  var o = ss.GetPixels32();
  int inFocus = 0;
  for (int y = 0; y < H; y++)
  for (int x = 0; x < W; x++) {
    float u = (x + 0.5f) / W, v = (y + 0.5f) / H;
    float fx = Mathf.Clamp(u * w - 0.5f, 0f, w - 1.001f), fy = Mathf.Clamp(v * h - 0.5f, 0f, h - 1.001f);
    int x0 = (int)fx, y0 = (int)fy; float tx = fx - x0, ty = fy - y0;
    float mk = Mathf.Lerp(Mathf.Lerp(m2[y0 * w + x0], m2[y0 * w + x0 + 1], tx), Mathf.Lerp(m2[(y0 + 1) * w + x0], m2[(y0 + 1) * w + x0 + 1], tx), ty);
    if (mk > 0.5f) inFocus++;
    Color dc = dof.GetPixelBilinear(u, v);
    int i = y * W + x;
    Color sc = o[i];
    o[i] = Color.Lerp(dc, sc, mk);
  }
  var outTex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
  outTex.SetPixels32(o); outTex.Apply();
  System.IO.File.WriteAllBytes("__OUT__/" + n + ".png", outTex.EncodeToPNG());
  // mask preview for checking
  var mt = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
  var mp = new Color32[w * h]; for (int i = 0; i < mp.Length; i++) { byte g = (byte)(m2[i] * 255f); mp[i] = new Color32(g, g, g, 255); }
  mt.SetPixels32(mp); mt.Apply(); System.IO.File.WriteAllBytes("Captures/__DIR__/dofmask_" + n + ".png", mt.EncodeToPNG());
  sb.AppendLine(n + " in-focus " + (100f * inFocus / (W * H)).ToString("F1") + "%");
  foreach (var t in new[]{ dof, nod, ss, outTex, mt, chr, emp }) UnityEngine.Object.DestroyImmediate(t);
}
return sb.ToString();
