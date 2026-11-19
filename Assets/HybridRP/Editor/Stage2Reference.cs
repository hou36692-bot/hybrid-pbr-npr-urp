using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Stage 2 reference capture set: the before/after comparison point for every upgrade step.
    /// Writes under Captures/&lt;prefix&gt;/ so each step can be captured with the same call and
    /// diffed against the reference with <see cref="HybridCapture.Compare"/>.
    /// </summary>
    public static class Stage2Reference
    {
        const string Bookmarks = "Overview,FullBody,Face,Hair";
        const string Presets = "Front,Side,Back,LowAmbient";

        // Night view as in the Phase 2/3 night baselines: sun 0.03, sky ambient and
        // reflections at 6 %. Baked probe light stays at 1, as it did there: the lightmaps hold baked
        // daylight bounce that no runtime setting can dim, so dimming only the character's probes
        // would separate it from the environment. This is a "sun off" view, not a baked night.
        const float NightSunIntensity = 0.03f;
        const float NightAmbientScale = 0.06f;

        [MenuItem("Tools/HybridRP/Stage 2/Capture Reference Set")]
        static void CaptureMenu() => Debug.Log(Capture("s2_ref"));

        /// <summary>Captures the lighting sheet, 1600x900 close-ups, ContactShadow and night views.</summary>
        public static string Capture(string prefix, int warmupFrames = -1)
        {
            var written = new List<string>
            {
                HybridCapture.Sheet($"{prefix}/lighting", Bookmarks, Presets, 480, 270, 0, warmupFrames),
                HybridCapture.Shot($"{prefix}/contact_lowambient", "ContactShadow", "LowAmbient", 1600, 900, warmupFrames),
            };
            foreach (string bookmark in new[] { "Overview", "FullBody", "Face" })
            foreach (string preset in new[] { "Front", "Back" })
                written.Add(HybridCapture.Shot($"{prefix}/{bookmark.ToLowerInvariant()}_{preset.ToLowerInvariant()}",
                                               bookmark, preset, 1600, 900, warmupFrames));
            written.Add(Night($"{prefix}/night_overview", "Overview", warmupFrames));
            written.Add(Night($"{prefix}/night_fullbody", "FullBody", warmupFrames));
            return string.Join("\n", written);
        }

        /// <summary>Diffs every PNG in Captures/&lt;candidate&gt;/ against the same name in Captures/&lt;reference&gt;/.</summary>
        public static string CompareSets(string reference, string candidate, bool writeDiffs = false)
        {
            string referenceDir = Path.Combine(HybridCapture.CaptureDirectory, reference);
            var sb = new StringBuilder();
            foreach (string path in Directory.GetFiles(Path.Combine(HybridCapture.CaptureDirectory, candidate), "*.png"))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("diff_", StringComparison.Ordinal))
                    continue;
                string referencePath = Path.Combine(referenceDir, name);
                if (!File.Exists(referencePath))
                {
                    sb.AppendLine($"{name}: no reference");
                    continue;
                }
                string diff = writeDiffs ? $"{candidate}/diff_{name}" : null;
                sb.AppendLine($"{name}: {HybridCapture.Compare(referencePath, path, diff)}");
            }
            return sb.ToString();
        }

        static string Night(string fileName, string bookmark, int warmupFrames)
        {
            var rig = ShowcaseRig.Find();
            if (rig == null)
                throw new InvalidOperationException("No ShowcaseRig in the open scene.");
            var sun = rig.Sun;
            if (sun == null)
                throw new InvalidOperationException("The ShowcaseRig has no sun assigned.");
            var state = rig.Capture();
            try
            {
                // LowAmbient supplies a repeatable sun angle; the night values then override its
                // intensity and ambient the same way the preset does (relative to the base ambient).
                if (!rig.ApplyLighting("LowAmbient"))
                    throw new InvalidOperationException("The night view needs the LowAmbient preset.");
                rig.ApplyAmbientScale(NightAmbientScale, scaleProbes: false);
                sun.intensity = NightSunIntensity;
                var image = HybridCapture.Render(bookmark, null, 1600, 900, warmupFrames);
                try { return HybridCapture.Save(image, fileName); }
                finally { UnityEngine.Object.DestroyImmediate(image); }
            }
            finally { rig.Restore(state); }
        }
    }
}
