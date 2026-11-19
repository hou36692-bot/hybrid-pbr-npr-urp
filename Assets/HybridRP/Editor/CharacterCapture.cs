using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Phase 3 comparison sheets for the character. Uses the same ShowcaseRig camera and
    /// lighting presets as HybridCapture, but changes the global Hybrid weights between
    /// cells. Like HybridCapture, every entry point restores the rig and the driver state.
    /// </summary>
    public static class CharacterCapture
    {
        static readonly (string bookmark, string preset)[] EffectColumns =
        {
            ("Face", "LowAmbient"),       // SDF / shadow colour / ramp are clearest without wash-out
            ("FullBody", "Side"),         // specular and rim
            ("Hair", "Back"),             // back-lit hair shows the directional highlight band
            ("ContactShadow", "LowAmbient"), // SSAO
        };

        [Serializable]
        public struct EffectState
        {
            public string name;
            public float ramp, shadowColor, specular, sdf, hairHighlight, rim, ssao;
            public int shadingMode;
        }

        static readonly EffectState[] EffectStates =
        {
            new EffectState { name = "Hybrid", ramp = 1, shadowColor = 1, specular = 1, sdf = 1, hairHighlight = 1, rim = 1, ssao = 1 },
            new EffectState { name = "RampOff", ramp = 0, shadowColor = 1, specular = 1, sdf = 1, hairHighlight = 1, rim = 1, ssao = 1 },
            new EffectState { name = "ShadowColorOff", ramp = 1, shadowColor = 0, specular = 1, sdf = 1, hairHighlight = 1, rim = 1, ssao = 1 },
            new EffectState { name = "SpecOff", ramp = 1, shadowColor = 1, specular = 0, sdf = 1, hairHighlight = 1, rim = 1, ssao = 1 },
            new EffectState { name = "SDFOff", ramp = 1, shadowColor = 1, specular = 1, sdf = 0, hairHighlight = 1, rim = 1, ssao = 1 },
            new EffectState { name = "HairHLOff", ramp = 1, shadowColor = 1, specular = 1, sdf = 1, hairHighlight = 0, rim = 1, ssao = 1 },
            new EffectState { name = "RimOff", ramp = 1, shadowColor = 1, specular = 1, sdf = 1, hairHighlight = 1, rim = 0, ssao = 1 },
            new EffectState { name = "SSAOOff", ramp = 1, shadowColor = 1, specular = 1, sdf = 1, hairHighlight = 1, rim = 1, ssao = 0 },
            new EffectState { name = "PBR", ramp = 1, shadowColor = 1, specular = 1, sdf = 1, hairHighlight = 1, rim = 1, ssao = 1, shadingMode = 1 },
        };

        [MenuItem("Tools/HybridRP/Phase 3/Capture Effect Sheet")]
        public static void CaptureEffectSheetMenu()
        {
            Debug.Log(EffectSheet("p3_effects"));
        }

        [MenuItem("Tools/HybridRP/Phase 3/Capture Debug Sheet")]
        public static void CaptureDebugSheetMenu()
        {
            Debug.Log(DebugSheet("p3_debug"));
        }

        [MenuItem("Tools/HybridRP/Phase 3/Capture Hybrid vs PBR")]
        public static void CaptureHybridPbrMenu()
        {
            Debug.Log(HybridPbrSheet("p3_hybrid_vs_pbr"));
        }

        /// <summary>Rows = effect states, columns = Face/Front, FullBody/Front, Hair/Back.</summary>
        public static string EffectSheet(string fileName, int cellWidth = 400, int cellHeight = 225)
        {
            var driver = RequireDriver();
            var cells = new List<(EffectState state, string bookmark, string preset)>();
            foreach (EffectState state in EffectStates)
                foreach (var column in EffectColumns)
                    cells.Add((state, column.bookmark, column.preset));

            return Preserving(driver, () =>
            {
                int rows = EffectStates.Length;
                int cols = EffectColumns.Length;
                var sheet = new Texture2D(cellWidth * cols, cellHeight * rows, TextureFormat.RGB24, false);
                try
                {
                    for (int r = 0; r < rows; r++)
                    {
                        EffectState state = EffectStates[r];
                        Apply(driver, state);
                        for (int c = 0; c < cols; c++)
                        {
                            var image = HybridCapture.Render(EffectColumns[c].bookmark, EffectColumns[c].preset, cellWidth, cellHeight);
                            try
                            {
                                sheet.SetPixels(c * cellWidth, (rows - 1 - r) * cellHeight, cellWidth, cellHeight, image.GetPixels());
                            }
                            finally { UnityEngine.Object.DestroyImmediate(image); }
                        }
                    }
                    sheet.Apply();
                    return Save(sheet, fileName);
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
            });
        }

        /// <summary>Debug views 0..11 on Face/Front, 4 columns x 3 rows.</summary>
        public static string DebugSheet(string fileName, int cellWidth = 480, int cellHeight = 270)
        {
            var driver = RequireDriver();
            return Preserving(driver, () =>
            {
                const int columns = 4;
                const int rows = 3;
                var sheet = new Texture2D(cellWidth * columns, cellHeight * rows, TextureFormat.RGB24, false);
                try
                {
                    for (int view = 0; view < 12; view++)
                    {
                        driver.SetDebugView(view);
                        var image = HybridCapture.Render("Face", "Front", cellWidth, cellHeight);
                        try
                        {
                            int r = view / columns, c = view % columns;
                            sheet.SetPixels(c * cellWidth, (rows - 1 - r) * cellHeight, cellWidth, cellHeight, image.GetPixels());
                        }
                        finally { UnityEngine.Object.DestroyImmediate(image); }
                    }
                    sheet.Apply();
                    return Save(sheet, fileName);
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
            });
        }

        /// <summary>Hybrid vs PBR, Face/Front and FullBody/Front.</summary>
        public static string HybridPbrSheet(string fileName, int cellWidth = 800, int cellHeight = 450)
        {
            var driver = RequireDriver();
            return Preserving(driver, () =>
            {
                string[] bookmarks = { "Face", "FullBody" };
                var sheet = new Texture2D(cellWidth * 2, cellHeight * 2, TextureFormat.RGB24, false);
                try
                {
                    for (int mode = 0; mode < 2; mode++)
                    {
                        driver.SetShadingMode(mode);
                        for (int b = 0; b < bookmarks.Length; b++)
                        {
                            var image = HybridCapture.Render(bookmarks[b], "Front", cellWidth, cellHeight);
                            try
                            {
                                sheet.SetPixels(b * cellWidth, (1 - mode) * cellHeight, cellWidth, cellHeight, image.GetPixels());
                            }
                            finally { UnityEngine.Object.DestroyImmediate(image); }
                        }
                    }
                    sheet.Apply();
                    return Save(sheet, fileName);
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
            });
        }

        static void Apply(HybridCharacterDriver driver, EffectState state)
        {
            driver.SetEffectWeights(state.ramp, state.shadowColor, state.specular, state.sdf,
                                    state.hairHighlight, state.rim, state.ssao);
            driver.SetShadingMode(state.shadingMode);
            driver.SetDebugView(0);
        }

        static T Preserving<T>(HybridCharacterDriver driver, Func<T> body)
        {
            var rig = ShowcaseRig.Find();
            var rigState = rig != null ? rig.Capture() : default;
            var driverState = driver.CaptureState();
            try { return body(); }
            finally
            {
                driver.RestoreState(driverState);
                if (rig != null)
                    rig.Restore(rigState);
            }
        }

        static HybridCharacterDriver RequireDriver()
        {
            var driver = HybridCharacterDriver.Find();
            if (driver == null)
                throw new InvalidOperationException("No HybridCharacterDriver in the open scene.");
            return driver;
        }

        static string Save(Texture2D image, string fileName)
        {
            string path = Path.Combine(HybridCapture.CaptureDirectory, fileName.EndsWith(".png") ? fileName : fileName + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, image.EncodeToPNG());
            return path;
        }
    }
}
