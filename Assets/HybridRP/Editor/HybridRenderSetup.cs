using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Installs the single HybridRP renderer feature on PC_Renderer, assigns its shader resources
    /// and the scene Volume profile. Idempotent: re-running repairs a missing piece without
    /// resetting tuned parameters.
    /// </summary>
    public static class HybridRenderSetup
    {
        public const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        public const string OutlineProfilePath = "Assets/Settings/HybridRP_Outline.asset";
        public const string Stage2ProfilePath = "Assets/Settings/HybridRP_Stage2.asset";
        public const string Stage2VolumeName = "HybridRP Stage 2";

        [MenuItem("Tools/HybridRP/Stage 2/Setup Render Feature")]
        static void SetupMenu() => Debug.Log(Setup());

        public static UniversalRendererData LoadRenderer()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
                throw new System.InvalidOperationException($"{RendererPath} not found.");
            return renderer;
        }

        /// <summary>The installed feature (created if missing), with resources assigned.</summary>
        public static HybridRenderFeature EnsureFeature()
        {
            var renderer = LoadRenderer();
            var features = renderer.rendererFeatures.OfType<HybridRenderFeature>().ToList();
            var feature = features.FirstOrDefault();
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<HybridRenderFeature>();
                AssetDatabase.AddObjectToAsset(feature, renderer);
                renderer.rendererFeatures.Add(feature);
            }
            feature.name = "HybridRP";

            var serialized = new SerializedObject(feature);
            var resources = serialized.FindProperty("_resources");
            AssignShader(resources, "outlineShader", "Hidden/HybridRP/Outline");
            AssignComputeAsset(resources, "depthPyramidCompute", "Assets/HybridRP/Shaders/Compute/HybridDepthPyramid.compute");
            AssignComputeAsset(resources, "gtaoCompute", "Assets/HybridRP/Shaders/Compute/HybridGTAO.compute");
            AssignComputeAsset(resources, "gtaoBlurUpsampleCompute", "Assets/HybridRP/Shaders/Compute/HybridGTAOBlurUpsample.compute");
            AssignComputeAsset(resources, "contactShadowsCompute", "Assets/HybridRP/Shaders/Compute/HybridContactShadows.compute");
            AssignShader(resources, "screenSpaceShadowsShader", "Hidden/HybridRP/ScreenSpaceShadows");
            AssignComputeAsset(resources, "volumetricFogCompute", "Assets/HybridRP/Shaders/Compute/HybridVolumetricFog.compute");
            AssignShader(resources, "fogCompositeShader", "Hidden/HybridRP/FogComposite");
            AssignComputeAsset(resources, "ssrCompute", "Assets/HybridRP/Shaders/Compute/HybridSSR.compute");
            AssignShader(resources, "ssrMaskShader", "Hidden/HybridRP/SSRMask");
            AssignShader(resources, "ssrCompositeShader", "Hidden/HybridRP/SSRComposite");
            serialized.ApplyModifiedPropertiesWithoutUndo();

            feature.SetActive(true);
            feature.Create();
            EditorUtility.SetDirty(feature);
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            return feature;
        }

        public static string Setup()
        {
            var feature = EnsureFeature();
            string outline = OutlineSetup.SetupVolumeAndLayers();
            string stage2 = EnsureStage2Volume();
            AssetDatabase.SaveAssets();
            return $"HybridRP feature on {RendererPath} ({LoadRenderer().rendererFeatures.Count} renderer features); {outline}; {stage2}";
        }

        /// <summary>
        /// One global Volume carrying the Stage 2 screen-space effects (GTAO now, contact
        /// shadows/fog/SSR later). A separate profile and GameObject keep each effect switchable
        /// for A/B captures without touching the final-image grading profile.
        /// </summary>
        public static string EnsureStage2Volume()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(Stage2ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, Stage2ProfilePath);
            }
            // VolumeProfile.Add only appends to the in-memory list; the profile inspector is what
            // normally makes the component a sub-asset. Without this the components are null after
            // the next domain reload.
            for (int i = profile.components.Count - 1; i >= 0; i--)
                if (profile.components[i] == null)
                    profile.components.RemoveAt(i);
            EnsureProfileComponent<HybridGTAO>(profile);
            EnsureProfileComponent<HybridContactShadows>(profile);
            EnsureProfileComponent<HybridVolumetricFog>(profile);
            EnsureProfileComponent<HybridSSR>(profile);

            var scene = SceneManager.GetActiveScene();
            var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(v => v.gameObject.name == Stage2VolumeName);
            bool created = false;
            if (volume == null)
            {
                var go = new GameObject(Stage2VolumeName);
                volume = go.AddComponent<Volume>();
                created = true;
            }
            volume.isGlobal = true;
            volume.priority = 10f;
            if (volume.sharedProfile != profile)
            {
                volume.sharedProfile = profile;
                created = true;
            }
            if (created)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            return created ? $"created {Stage2VolumeName}" : $"{Stage2VolumeName} up to date";
        }

        /// <summary>A volume component that survives a domain reload (added as a sub-asset).</summary>
        public static T EnsureProfileComponent<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet<T>(out var existing) && existing != null)
                return existing;
            var component = profile.Add<T>(true);
            component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(component, profile);
            EditorUtility.SetDirty(profile);
            return component;
        }

        static void AssignComputeAsset(SerializedProperty resources, string field, string assetPath)
        {
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(assetPath);
            if (compute == null)
                throw new System.InvalidOperationException($"Compute shader '{assetPath}' not found.");
            resources.FindPropertyRelative(field).objectReferenceValue = compute;
        }

        static void AssignShader(SerializedProperty resources, string field, string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
                throw new System.InvalidOperationException($"Shader '{shaderName}' not found.");
            resources.FindPropertyRelative(field).objectReferenceValue = shader;
        }

        public static void AssignCompute(HybridRenderFeature feature, string field, string assetPath)
        {
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(assetPath);
            if (compute == null)
                throw new System.InvalidOperationException($"Compute shader '{assetPath}' not found.");
            var serialized = new SerializedObject(feature);
            serialized.FindProperty("_resources").FindPropertyRelative(field).objectReferenceValue = compute;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
