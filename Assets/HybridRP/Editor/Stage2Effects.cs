using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Switches the Stage 2 screen-space effects and their URP counterparts for A/B captures.
    /// GTAO and URP SSAO write the same global slot, so exactly one of them may be on.
    /// </summary>
    public static class Stage2Effects
    {
        public static string SetSSAO(bool enabled) => SetRendererFeature("ScreenSpaceAmbientOcclusion", enabled);

        public static string SetGTAO(bool enabled) => SetVolumeEffect<HybridGTAO>(c => c.enable, enabled);

        public static string SetContactShadows(bool enabled) => SetVolumeEffect<HybridContactShadows>(c => c.enable, enabled);

        public static string SetVolumetricFog(bool enabled) => SetVolumeEffect<HybridVolumetricFog>(c => c.enable, enabled);

        public static string SetSSR(bool enabled) => SetVolumeEffect<HybridSSR>(c => c.enable, enabled);

        /// <summary>Tags the reflective renderers (currently the pond water) with rendering layer bit 2.</summary>
        public static string SetupReflectors()
        {
            // The pond water is transparent and does not write depth, so the SSR depth test cannot
            // see its surface; tag the smooth calibration spheres instead (opaque, mirror-like).
            int tagged = 0;
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(UnityEngine.FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                bool reflector = renderer.name.StartsWith("Sphere_M") &&
                                 (renderer.name.Contains("_S1.00") || renderer.name.Contains("_S0.75"));
                // The transparent pond water cannot receive post-process SSR (no depth); clear the
                // bit if an earlier setup run tagged it.
                uint mask = reflector
                    ? renderer.renderingLayerMask | (1u << 2)
                    : renderer.renderingLayerMask & ~(1u << 2);
                if (renderer.renderingLayerMask != mask &&
                    (reflector || renderer.name == "Water"))
                {
                    renderer.renderingLayerMask = mask;
                    tagged++;
                }
            }
            if (tagged > 0)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            }
            return $"tagged {tagged} reflector renderer(s)";
        }

        public static string Status()
        {
            var renderer = HybridRenderSetup.LoadRenderer();
            var features = string.Join(", ", renderer.rendererFeatures
                .Where(f => f != null).Select(f => f.name + (f.isActive ? "+" : "-")));
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(HybridRenderSetup.Stage2ProfilePath);
            string state = profile == null ? "no profile" : string.Join(", ", profile.components
                .Where(c => c != null).Select(c => c.GetType().Name + (c.active ? "+" : "-")));
            string ssao = FindFeature("ScreenSpaceAmbientOcclusion") is { } f && f.isActive ? "on" : "off";
            return $"SSAO feature {ssao}; features [{features}]; profile [{state}]";
        }

        static string SetRendererFeature(string typeName, bool enabled)
        {
            var renderer = HybridRenderSetup.LoadRenderer();
            var feature = FindFeature(typeName);
            if (feature == null)
                return $"{typeName} not found";
            feature.SetActive(enabled);
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();
            return $"{typeName} {(enabled ? "on" : "off")}";
        }

        static ScriptableRendererFeature FindFeature(string typeName) =>
            HybridRenderSetup.LoadRenderer().rendererFeatures.FirstOrDefault(f => f != null && f.GetType().Name == typeName);

        static string SetVolumeEffect<T>(Func<T, BoolParameter> selectEnabled, bool enabled) where T : VolumeComponent
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(HybridRenderSetup.Stage2ProfilePath);
            if (profile == null)
                return $"{HybridRenderSetup.Stage2ProfilePath} missing";
            var component = HybridRenderSetup.EnsureProfileComponent<T>(profile);
            component.active = true;
            var parameter = selectEnabled(component);
            parameter.overrideState = true;
            parameter.value = enabled;
            EditorUtility.SetDirty(component);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return $"{typeof(T).Name} {(enabled ? "on" : "off")}";
        }
    }
}
