using System;
using System.Collections.Generic;
using UnityEngine;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Stage 2 S2-2 comparisons: each character/environment integration term on its own and all
    /// together, against the Phase 3-5 shading (all weights 0). Rows are framings, columns states.
    /// </summary>
    public static class IntegrationCapture
    {
        public struct Weights
        {
            public string name;
            public float flatten, positionLock, envRim, backRim;

            public Weights(string name, float flatten, float positionLock, float envRim, float backRim)
            {
                this.name = name;
                this.flatten = flatten;
                this.positionLock = positionLock;
                this.envRim = envRim;
                this.backRim = backRim;
            }
        }

        public static readonly Weights Off = new Weights("Off", 0f, 0f, 0f, 0f);

        /// <summary>Accepted defaults: GI normal flatten, GI position lock, environment rim, back rim.</summary>
        public static Weights Default = new Weights("All", 0.75f, 0.85f, 0.35f, 0.8f);

        /// <summary>Renders the sheet; <paramref name="framings"/> is "Bookmark/Preset,...".</summary>
        public static string Sheet(string fileName, string framings, IList<Weights> states,
                                   int cellWidth = 480, int cellHeight = 270, int warmupFrames = -1)
        {
            var driver = HybridCharacterDriver.Find();
            var rig = ShowcaseRig.Find();
            if (driver == null || rig == null)
                throw new InvalidOperationException("Needs the showcase character and rig.");
            var cells = new List<(string bookmark, string preset)>();
            foreach (string item in framings.Split(','))
            {
                string[] parts = item.Trim().Split('/');
                cells.Add((parts[0], parts.Length > 1 ? parts[1] : null));
            }

            var driverState = driver.CaptureState();
            var rigState = rig.Capture();
            var sheet = new Texture2D(cellWidth * states.Count, cellHeight * cells.Count, TextureFormat.RGB24, false);
            try
            {
                for (int r = 0; r < cells.Count; r++)
                for (int c = 0; c < states.Count; c++)
                {
                    var w = states[c];
                    driver.SetIntegrationWeights(w.flatten, w.positionLock, w.envRim, w.backRim);
                    var image = HybridCapture.Render(cells[r].bookmark, cells[r].preset, cellWidth, cellHeight, warmupFrames);
                    try { sheet.SetPixels(c * cellWidth, (cells.Count - 1 - r) * cellHeight, cellWidth, cellHeight, image.GetPixels()); }
                    finally { UnityEngine.Object.DestroyImmediate(image); }
                }
                sheet.Apply();
                return HybridCapture.Save(sheet, fileName);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sheet);
                driver.RestoreState(driverState);
                rig.Restore(rigState);
            }
        }

        /// <summary>One image per state for close-ups, named "&lt;prefix&gt;_&lt;state&gt;".</summary>
        public static string Shots(string prefix, string bookmark, string preset, IList<Weights> states,
                                   int width = 1600, int height = 900)
        {
            var driver = HybridCharacterDriver.Find();
            var driverState = driver.CaptureState();
            var written = new List<string>();
            try
            {
                foreach (var w in states)
                {
                    driver.SetIntegrationWeights(w.flatten, w.positionLock, w.envRim, w.backRim);
                    written.Add(HybridCapture.Shot($"{prefix}_{w.name}", bookmark, preset, width, height));
                }
            }
            finally { driver.RestoreState(driverState); }
            return string.Join("\n", written);
        }

        public static Weights[] Isolated(Weights all) => new[]
        {
            Off,
            new Weights("GINormal", all.flatten, 0f, 0f, 0f),
            new Weights("GIPosition", 0f, all.positionLock, 0f, 0f),
            new Weights("EnvRim", 0f, 0f, all.envRim, 0f),
            new Weights("BackRim", 0f, 0f, 0f, all.backRim),
            all,
        };
    }
}
