using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HybridRP.EditorTools
{
    public static class OutlineCapture
    {
        [MenuItem("Tools/HybridRP/Phase 4/Capture Outline On Off")]
        public static void OnOffMenu() => Debug.Log(OnOffSheet("p4_on_off"));

        [MenuItem("Tools/HybridRP/Phase 4/Capture Outline Debug")]
        public static void DebugMenu() => Debug.Log(DebugSheet("p4_debug"));

        public static string OnOffSheet(string name, string preset = "Side", int width = 800, int height = 450)
        {
            return Preserving(() =>
            {
                var driver = HybridCharacterDriver.Find();
                var sheet = new Texture2D(width * 2, height * 3, TextureFormat.RGB24, false);
                try
                {
                    string[] bookmarks = { "FullBody", "Face", "Hair" };
                    driver.SetDebugView(0);
                    for (int r = 0; r < bookmarks.Length; r++)
                    for (int c = 0; c < 2; c++)
                    {
                        driver.SetOutlineWeight(c);
                        var image = HybridCapture.Render(bookmarks[r], preset, width, height);
                        try { sheet.SetPixels(c * width, (2 - r) * height, width, height, image.GetPixels()); }
                        finally { UnityEngine.Object.DestroyImmediate(image); }
                    }
                    sheet.Apply();
                    return Save(sheet, name);
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
            });
        }

        public static string DebugSheet(string name, int width = 640, int height = 360)
        {
            return Preserving(() =>
            {
                var camera = ShowcaseRig.Find().Camera;
                var additional = camera.GetUniversalAdditionalCameraData();
                bool post = additional.renderPostProcessing;
                var driver = HybridCharacterDriver.Find();
                try
                {
                    // Bloom/tone mapping in the pack's existing Volume would change binary mask
                    // values. Debug captures bypass them; ordinary comparisons keep the full chain.
                    additional.renderPostProcessing = false;
                    driver.SetDebugView(11);
                    driver.SetOutlineWeight(1f);
                    return HybridCapture.Sheet(name, "FullBody,Face,Hair", "Front,Side,Back", width, height);
                }
                finally { additional.renderPostProcessing = post; }
            });
        }

        public static string ResolutionTests(string prefix)
        {
            return Preserving(() =>
            {
                var urp = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
                var camera = ShowcaseRig.Find().Camera;
                var additional = camera.GetUniversalAdditionalCameraData();
                float scale = urp.renderScale;
                int msaa = urp.msaaSampleCount;
                var aa = additional.antialiasing;
                try
                {
                    HybridCharacterDriver.Find().SetDebugView(0);
                    HybridCharacterDriver.Find().SetOutlineWeight(1f);
                    foreach (int h in new[] { 720, 1080, 1440 })
                        HybridCapture.Shot($"{prefix}_{h}p", "Face", "Side", h * 16 / 9, h);
                    foreach (float testScale in new[] { 0.75f, 1f, 1.25f })
                    {
                        urp.renderScale = testScale;
                        HybridCapture.Shot($"{prefix}_scale_{testScale:F2}", "Face", "Side", 1920, 1080);
                    }
                    urp.renderScale = 1f;
                    foreach (int testMsaa in new[] { 1, 4 })
                    {
                        urp.msaaSampleCount = testMsaa;
                        foreach (var testAa in new[] { AntialiasingMode.None, AntialiasingMode.SubpixelMorphologicalAntiAliasing })
                        {
                            additional.antialiasing = testAa;
                            HybridCapture.Shot($"{prefix}_msaa_{testMsaa}_{testAa}", "Face", "Side", 1920, 1080);
                        }
                    }
                    return HybridCapture.CaptureDirectory;
                }
                finally
                {
                    urp.renderScale = scale;
                    urp.msaaSampleCount = msaa;
                    additional.antialiasing = aa;
                }
            });
        }

        public static string OcclusionTest(string prefix)
        {
            return Preserving(() =>
            {
                var driver = HybridCharacterDriver.Find();
                var rig = ShowcaseRig.Find();
                rig.Apply("Face");
                var camera = rig.Camera;
                var occluder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                try
                {
                    occluder.name = "Temporary Outline Occluder";
                    // Camera-local square straddles the character and background. It must neither
                    // receive an outline nor reveal the masked character behind it.
                    occluder.transform.position = camera.transform.position + camera.transform.forward * 0.4f;
                    occluder.transform.rotation = camera.transform.rotation;
                    occluder.transform.localScale = new Vector3(0.07f, 0.07f, 0.04f);
                    material.SetColor("_BaseColor", new Color(0.45f, 0.6f, 0.7f));
                    occluder.GetComponent<Renderer>().sharedMaterial = material;
                    driver.SetDebugView(0);
                    driver.SetOutlineWeight(0f);
                    HybridCapture.Shot(prefix + "_off", null, "Side");
                    driver.SetOutlineWeight(1f);
                    HybridCapture.Shot(prefix + "_on", null, "Side");
                    var additional = camera.GetUniversalAdditionalCameraData();
                    bool post = additional.renderPostProcessing;
                    try
                    {
                        additional.renderPostProcessing = false;
                        driver.SetDebugView(11);
                        HybridCapture.Shot(prefix + "_mask", null, "Side");
                    }
                    finally { additional.renderPostProcessing = post; }
                    return HybridCapture.CaptureDirectory;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(occluder);
                    UnityEngine.Object.DestroyImmediate(material);
                }
            });
        }

        static T Preserving<T>(Func<T> action)
        {
            var rig = ShowcaseRig.Find();
            var driver = HybridCharacterDriver.Find();
            if (rig == null || driver == null)
                throw new InvalidOperationException("Outline capture needs the showcase rig and driver.");
            var rigState = rig.Capture();
            var driverState = driver.CaptureState();
            try { return action(); }
            finally
            {
                driver.RestoreState(driverState);
                rig.Restore(rigState);
            }
        }

        static string Save(Texture2D image, string name)
        {
            string path = Path.Combine(HybridCapture.CaptureDirectory, name + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, image.EncodeToPNG());
            return path;
        }
    }
}
