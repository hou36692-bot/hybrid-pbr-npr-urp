using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HybridRP.EditorTools
{
    public static class FinalImageCapture
    {
        enum View { Legacy, None, Neutral, ACES, Ungraded, Final }

        [MenuItem("Tools/HybridRP/Phase 5/Capture Tone Mapping")]
        public static void ToneMenu() => Debug.Log(ToneSheet("p5_tone_mapping"));
        [MenuItem("Tools/HybridRP/Phase 5/Capture Grading On Off")]
        public static void GradingMenu() => Debug.Log(GradingSheet("p5_grading"));
        [MenuItem("Tools/HybridRP/Phase 5/Capture Legacy vs Final")]
        public static void ComparisonMenu() => Debug.Log(LegacySheet("p5_legacy_vs_final"));

        public static string ToneSheet(string name, string preset = "Front") =>
            Sheet(name, new[] { View.None, View.Neutral, View.ACES },
                  new[] { "Overview", "Face", "PBRCalibration" }, preset, 640, 360);

        public static string GradingSheet(string name, string preset = "Front") =>
            Sheet(name, new[] { View.Ungraded, View.Final },
                  new[] { "Overview", "Face", "Hair" }, preset, 800, 450);

        public static string LegacySheet(string name, string preset = "Front") =>
            Sheet(name, new[] { View.Legacy, View.Final },
                  new[] { "Overview", "FullBody", "Face", "Hair" }, preset, 800, 450);

        static string Sheet(string name, View[] views, string[] bookmarks, string preset, int width, int height)
        {
            var volume = FinalImageSetup.FindVolume();
            var rig = ShowcaseRig.Find();
            var driver = HybridCharacterDriver.Find();
            var finalProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(FinalImageSetup.ProfilePath);
            var urp = UniversalRenderPipeline.asset;
            if (volume == null || rig == null || driver == null || finalProfile == null || urp == null)
                throw new InvalidOperationException("Run Phase 5 Setup Final Image before capturing comparisons.");

            var previousInstance = volume.HasInstantiatedProfile() ? volume.profile : null;
            var rigState = rig.Capture();
            var driverState = driver.CaptureState();
            var gradingMode = urp.colorGradingMode;
            var qualityProfile = VolumeManager.instance.qualityDefaultProfile;
            var additional = rig.Camera.GetUniversalAdditionalCameraData();
            var updateMode = rig.Camera.GetVolumeFrameworkUpdateMode();
            bool enabled = volume.enabled;
            float weight = volume.weight;
            bool post = rig.Camera.GetUniversalAdditionalCameraData().renderPostProcessing;
            var sheet = new Texture2D(width * views.Length, height * bookmarks.Length, TextureFormat.RGB24, false);
            try
            {
                volume.enabled = true;
                volume.weight = 1f;
                additional.renderPostProcessing = true;
                rig.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
                driver.SetDebugView(0);
                for (int c = 0; c < views.Length; c++)
                {
                    bool legacy = views[c] == View.Legacy;
                    var source = legacy ? AssetDatabase.LoadAssetAtPath<VolumeProfile>(FinalImageSetup.LegacyProfilePath) : finalProfile;
                    var temporary = CloneProfile(source);
                    try
                    {
                        // Captures use a deep profile copy, never mutate serialized comparison assets.
                        volume.profile = temporary;
                        urp.colorGradingMode = legacy ? ColorGradingMode.LowDynamicRange : ColorGradingMode.HighDynamicRange;
                        var legacyDefaults = legacy
                            ? AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/SampleSceneProfile.asset") : null;
                        if (legacy && legacyDefaults == null)
                            throw new InvalidOperationException("Missing legacy quality profile: SampleSceneProfile.asset.");
                        VolumeManager.instance.SetQualityDefaultProfile(legacyDefaults);
                        if (!legacy && views[c] != View.Final)
                        {
                            NeutralizeGrading(temporary);
                            if (views[c] != View.Ungraded)
                            {
                                if (!temporary.TryGet<Tonemapping>(out var tone))
                                    tone = temporary.Add<Tonemapping>(true);
                                tone.active = true;
                                tone.mode.Override(views[c] == View.None ? TonemappingMode.None :
                                    views[c] == View.Neutral ? TonemappingMode.Neutral : TonemappingMode.ACES);
                            }
                        }
                        for (int r = 0; r < bookmarks.Length; r++)
                        {
                            var image = HybridCapture.Render(bookmarks[r], preset, width, height);
                            try { sheet.SetPixels(c * width, (bookmarks.Length - 1 - r) * height, width, height, image.GetPixels()); }
                            finally { UnityEngine.Object.DestroyImmediate(image); }
                        }
                    }
                    finally
                    {
                        volume.profile = previousInstance;
                        DestroyProfile(temporary);
                    }
                }
                sheet.Apply();
                string path = Path.Combine(HybridCapture.CaptureDirectory, name + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, sheet.EncodeToPNG());
                return path;
            }
            finally
            {
                volume.profile = previousInstance;
                volume.enabled = enabled;
                volume.weight = weight;
                urp.colorGradingMode = gradingMode;
                VolumeManager.instance.SetQualityDefaultProfile(qualityProfile);
                additional.renderPostProcessing = post;
                rig.Camera.SetVolumeFrameworkUpdateMode(updateMode);
                VolumeManager.instance.Update(rig.Camera.transform, additional.volumeLayerMask);
                driver.RestoreState(driverState);
                rig.Restore(rigState);
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        static void NeutralizeGrading(VolumeProfile profile)
        {
            foreach (var component in profile.components)
            {
                if (component is Tonemapping)
                    continue;
                if (component is ColorAdjustments color)
                {
                    // Exposure and tone stay identical: "off" isolates grading, not brightness.
                    color.contrast.Override(0f);
                    color.colorFilter.Override(Color.white);
                    color.hueShift.Override(0f);
                    color.saturation.Override(0f);
                }
                else
                    component.active = false;
            }
        }

        static VolumeProfile CloneProfile(VolumeProfile source)
        {
            if (source == null)
                throw new InvalidOperationException("Missing comparison profile.");
            var copy = ScriptableObject.CreateInstance<VolumeProfile>();
            copy.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                foreach (var component in source.components)
                {
                    var clone = UnityEngine.Object.Instantiate(component);
                    clone.hideFlags = HideFlags.HideAndDontSave;
                    copy.components.Add(clone);
                }
                return copy;
            }
            catch
            {
                DestroyProfile(copy);
                throw;
            }
        }

        static void DestroyProfile(VolumeProfile profile)
        {
            foreach (var component in profile.components)
                UnityEngine.Object.DestroyImmediate(component);
            UnityEngine.Object.DestroyImmediate(profile);
        }
    }
}
