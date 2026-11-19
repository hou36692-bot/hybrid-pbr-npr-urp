using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Renders the ShowcaseRig camera to PNG files under &lt;project&gt;/Captures/ (outside Assets,
    /// so screenshots never get imported). Meant to be called from the Unity CLI, e.g.
    /// <c>HybridRP.EditorTools.HybridCapture.Sheet("p1_lighting", "FullBody,Face", "Front,Side,Back,LowAmbient")</c>.
    /// Every entry point restores the rig's camera and lighting afterwards, so a capture run can't
    /// leave a preset's sun/ambient values behind to be saved into the scene.
    /// </summary>
    public static class HybridCapture
    {
        public static string CaptureDirectory =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Captures"));

        /// <summary>
        /// Extra frames rendered into the same target before the one that is read back. Used by
        /// every entry point (and the other capture tools, which go through <see cref="Render"/>)
        /// unless a call passes its own count. Temporal effects keep per-camera history keyed on
        /// their own render counter, not on Time.frameCount (which does not advance during a
        /// synchronous Editor capture), so these frames let that history converge.
        /// </summary>
        public static int DefaultWarmupFrames { get; set; }

        /// <summary>Applies a bookmark and preset (either may be null) and writes one PNG. Returns its path.</summary>
        public static string Shot(string fileName, string bookmark = null, string preset = null,
                                  int width = 1600, int height = 900, int warmupFrames = -1)
        {
            return PreservingRig(() => ShotCore(fileName, bookmark, preset, width, height, warmupFrames));
        }

        /// <summary>
        /// Captures every bookmark × preset combination as "&lt;prefix&gt;_&lt;bookmark&gt;_&lt;preset&gt;.png".
        /// Lists are comma-separated; an empty list keeps the current camera / lighting.
        /// </summary>
        public static string Matrix(string prefix, string bookmarks, string presets, int width = 1600, int height = 900,
                                    int warmupFrames = -1)
        {
            return PreservingRig(() =>
            {
                var written = new List<string>();
                foreach (string preset in Split(presets))
                foreach (string bookmark in Split(bookmarks))
                    written.Add(ShotCore(JoinName(prefix, bookmark, preset), bookmark, preset, width, height, warmupFrames));
                return string.Join("\n", written);
            });
        }

        /// <summary>
        /// One contact sheet: rows = bookmarks, columns = presets. Easier to compare than loose files.
        /// With <paramref name="wrap"/> &gt; 0 the cells are laid out row-major, <paramref name="wrap"/> per row.
        /// </summary>
        public static string Sheet(string fileName, string bookmarks, string presets,
                                   int cellWidth = 800, int cellHeight = 450, int wrap = 0, int warmupFrames = -1)
        {
            return PreservingRig(() =>
            {
                var cells = (from bookmark in Split(bookmarks) from preset in Split(presets) select (bookmark, preset)).ToList();
                int perRow = wrap > 0 ? wrap : Split(presets).Count();
                int rowCount = (cells.Count + perRow - 1) / perRow;
                var sheet = new Texture2D(cellWidth * perRow, cellHeight * rowCount, TextureFormat.RGB24, false);
                try
                {
                    for (int i = 0; i < cells.Count; i++)
                    {
                        int r = i / perRow, c = i % perRow;
                        var cell = Render(cells[i].bookmark, cells[i].preset, cellWidth, cellHeight, warmupFrames);
                        try
                        {
                            // Texture rows start at the bottom; put the first cell at the top.
                            sheet.SetPixels(c * cellWidth, (rowCount - 1 - r) * cellHeight, cellWidth, cellHeight, cell.GetPixels());
                        }
                        finally { UnityEngine.Object.DestroyImmediate(cell); }
                    }
                    sheet.Apply();
                    return Save(sheet, fileName);
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
            });
        }

        /// <summary>
        /// Renders the rig camera into a new Texture2D (caller destroys it). Leaves the rig in the
        /// applied bookmark/preset state; the public Shot/Matrix/Sheet wrappers restore it.
        /// </summary>
        public static Texture2D Render(string bookmark, string preset, int width, int height, int warmupFrames = -1)
        {
            int warmup = Math.Max(0, warmupFrames < 0 ? DefaultWarmupFrames : warmupFrames);
            var rig = RequireRig();
            if (!string.IsNullOrEmpty(preset) && !rig.ApplyLighting(preset))
                throw new ArgumentException($"Unknown lighting preset '{preset}'.");
            if (!string.IsNullOrEmpty(bookmark) && !rig.Apply(bookmark))
                throw new ArgumentException($"Unknown camera bookmark '{bookmark}'.");

            Camera camera = rig.Camera;
            // URP derives its intermediate colour format from the external target. An 8-bit
            // target clips scene highlights before tonemapping even with camera HDR enabled.
            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBHalf, 24)
            {
                sRGB = false,
                msaaSamples = camera.allowMSAA && UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset != null
                    ? UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset.msaaSampleCount : 1,
            };
            var target = RenderTexture.GetTemporary(descriptor);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D image = null;
            Texture2D linearReadback = null;
            var lodGroups = UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var fadeModes = lodGroups.Select(group => group.fadeMode).ToArray();
            try
            {
                // Teleporting between bookmarks leaves transient cross-fade stipple in a
                // one-frame capture. Snap LOD selection for the image, not for the live scene.
                foreach (var group in lodGroups)
                    group.fadeMode = LODFadeMode.None;
                camera.targetTexture = target;
                var request = new RenderPipeline.StandardRequest { destination = target };
                bool useRequest = RenderPipeline.SupportsRenderRequest(camera, request);
                for (int frame = 0; frame <= warmup; frame++)
                {
                    if (useRequest)
                        RenderPipeline.SubmitRenderRequest(camera, request);
                    else
                        camera.Render();
                }

                RenderTexture.active = target;
                linearReadback = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
                linearReadback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                linearReadback.Apply();
                var pixels = linearReadback.GetPixels();
                // Float targets do not perform hardware sRGB encoding. Encode once, after the
                // complete post stack; PNG is display-referred SDR, not a raw HDR measurement.
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = pixels[i].gamma;
                image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.SetPixels(pixels);
                image.Apply();
                return image;
            }
            catch
            {
                if (image != null)
                    UnityEngine.Object.DestroyImmediate(image);
                throw;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                for (int i = 0; i < lodGroups.Length; i++)
                    if (lodGroups[i] != null)
                        lodGroups[i].fadeMode = fadeModes[i];
                if (linearReadback != null)
                    UnityEngine.Object.DestroyImmediate(linearReadback);
                RenderTexture.ReleaseTemporary(target);
            }
        }

        static string ShotCore(string fileName, string bookmark, string preset, int width, int height, int warmupFrames)
        {
            var image = Render(bookmark, preset, width, height, warmupFrames);
            try { return Save(image, fileName); }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }

        /// <summary>
        /// Compares two PNGs of equal size (paths relative to Captures/ or absolute) in 8-bit
        /// display space. Reports the mean absolute RGB difference (0..1, the same measure as the
        /// Phase 2 A/B numbers), the largest channel difference in 1/255 steps, and the share of
        /// pixels whose largest channel difference exceeds <paramref name="threshold255"/>.
        /// With <paramref name="diffFile"/> set, writes a ×<paramref name="diffGain"/> amplified
        /// difference image.
        /// </summary>
        public static string Compare(string fileA, string fileB, string diffFile = null, int threshold255 = 2, float diffGain = 8f)
        {
            var a = Load(fileA);
            var b = Load(fileB);
            Texture2D diff = null;
            try
            {
                if (a.width != b.width || a.height != b.height)
                    throw new ArgumentException($"Size mismatch: {a.width}x{a.height} vs {b.width}x{b.height}.");
                Color32[] pa = a.GetPixels32(), pb = b.GetPixels32();
                Color32[] pd = diffFile != null ? new Color32[pa.Length] : null;
                double sum = 0;
                int maxDelta = 0, changed = 0;
                for (int i = 0; i < pa.Length; i++)
                {
                    int dr = Math.Abs(pa[i].r - pb[i].r), dg = Math.Abs(pa[i].g - pb[i].g), db = Math.Abs(pa[i].b - pb[i].b);
                    int m = Math.Max(dr, Math.Max(dg, db));
                    sum += dr + dg + db;
                    maxDelta = Math.Max(maxDelta, m);
                    if (m > threshold255)
                        changed++;
                    if (pd != null)
                        pd[i] = new Color32(Amplify(dr, diffGain), Amplify(dg, diffGain), Amplify(db, diffGain), 255);
                }
                if (pd != null)
                {
                    diff = new Texture2D(a.width, a.height, TextureFormat.RGB24, false);
                    diff.SetPixels32(pd);
                    diff.Apply();
                    Save(diff, diffFile);
                }
                double mean = sum / (pa.Length * 3.0 * 255.0);
                return $"mean={mean:F5} max={maxDelta}/255 changed(>{threshold255})={100.0 * changed / pa.Length:F3}% " +
                       $"pixels={pa.Length}";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
                if (diff != null)
                    UnityEngine.Object.DestroyImmediate(diff);
            }
        }

        static byte Amplify(int delta, float gain) => (byte)Math.Min(255, Mathf.RoundToInt(delta * gain));

        static Texture2D Load(string file)
        {
            string path = Path.IsPathRooted(file) ? file : Path.Combine(CaptureDirectory, file.EndsWith(".png") ? file : file + ".png");
            if (!File.Exists(path))
                throw new FileNotFoundException("Capture not found.", path);
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidDataException($"Not a readable image: {path}");
            }
            return texture;
        }

        static T PreservingRig<T>(Func<T> body)
        {
            var rig = RequireRig();
            var state = rig.Capture();
            try { return body(); }
            finally { rig.Restore(state); }
        }

        static ShowcaseRig RequireRig()
        {
            var rig = ShowcaseRig.Find();
            if (rig == null)
                throw new InvalidOperationException("No ShowcaseRig in the open scene.");
            return rig;
        }

        public static string Save(Texture2D image, string fileName)
        {
            string path = Path.Combine(CaptureDirectory, fileName.EndsWith(".png") ? fileName : fileName + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, image.EncodeToPNG());
            return path;
        }

        static string JoinName(params string[] parts) =>
            string.Join("_", parts.Where(s => !string.IsNullOrEmpty(s)));

        static IEnumerable<string> Split(string list)
        {
            if (string.IsNullOrWhiteSpace(list))
            {
                yield return null;
                yield break;
            }
            foreach (string item in list.Split(','))
                if (!string.IsNullOrWhiteSpace(item))
                    yield return item.Trim();
        }
    }
}
