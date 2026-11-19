using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HybridRP.EditorTools
{
    public static class FinalImageSetup
    {
        public const string ProfilePath = "Assets/Settings/HybridRP_FinalImage.asset";
        public const string LegacyProfilePath = "Assets/Suntail Village/Demo/Suntail Village/Suntail Outdoor Volume.asset";

        [MenuItem("Tools/HybridRP/Phase 5/Setup Final Image")]
        public static void SetupMenu() => Debug.Log(Setup());

        public static Volume FindVolume()
        {
            return Resources.FindObjectsOfTypeAll<Volume>().FirstOrDefault(v =>
                v.gameObject.scene.IsValid() && v.gameObject.scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene() &&
                v.isGlobal && ((v.sharedProfile != null &&
                (AssetDatabase.GetAssetPath(v.sharedProfile) == ProfilePath ||
                 AssetDatabase.GetAssetPath(v.sharedProfile) == LegacyProfilePath)) ||
                 (v.sharedProfile == null && v.gameObject.name == "Global Volume")));
        }

        public static string Setup()
        {
            var rig = ShowcaseRig.Find();
            var volume = FindVolume();
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (rig == null || volume == null || urp == null)
                throw new InvalidOperationException("Final image setup needs the showcase rig, Global Volume and URP asset.");

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }
            if (!profile.TryGet<Tonemapping>(out _))
                Add<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);
            if (!profile.TryGet<ColorAdjustments>(out _))
            {
                var color = Add<ColorAdjustments>(profile);
                color.postExposure.Override(0.35f);
                color.contrast.Override(-5f);
                color.saturation.Override(-6f);
            }
            if (!profile.TryGet<WhiteBalance>(out _))
                Add<WhiteBalance>(profile);
            foreach (var component in profile.components)
                EditorUtility.SetDirty(component);
            EditorUtility.SetDirty(profile);
            // Do not reset accepted grading on subsequent setup runs. Remove a stale
            // instantiated profile, which would otherwise hide the shared asset assignment.
            var instance = volume.HasInstantiatedProfile() ? volume.profile : null;
            volume.profile = null;
            if (instance != null && !EditorUtility.IsPersistent(instance))
            {
                foreach (var component in instance.components)
                    if (!EditorUtility.IsPersistent(component))
                        UnityEngine.Object.DestroyImmediate(component);
                UnityEngine.Object.DestroyImmediate(instance);
            }
            volume.sharedProfile = profile;
            volume.enabled = true;
            volume.weight = 1f;
            rig.Camera.allowHDR = true;
            rig.Camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            urp.supportsHDR = true;
            urp.colorGradingMode = ColorGradingMode.HighDynamicRange;
            urp.colorGradingLutSize = 32;
            // SampleSceneProfile was also a quality default, independently adding Bloom and
            // vignette even after replacing the scene Volume. Fall back to global neutral defaults.
            urp.volumeProfile = null;
            VolumeManager.instance.SetQualityDefaultProfile(null);
            EditorUtility.SetDirty(urp);
            EditorUtility.SetDirty(volume);
            EditorUtility.SetDirty(rig.Camera);
            EditorSceneManager.MarkSceneDirty(volume.gameObject.scene);
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(volume.gameObject.scene);
            return $"Final image ready: HDR grading, 32-slice internal LUT, {ProfilePath}";
        }

        static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var component = profile.Add<T>(true);
            component.name = typeof(T).Name;
            component.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
            AssetDatabase.AddObjectToAsset(component, profile);
            EditorUtility.SetDirty(component);
            return component;
        }
    }
}
