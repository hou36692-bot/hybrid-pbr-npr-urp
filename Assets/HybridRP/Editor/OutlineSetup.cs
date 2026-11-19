using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Phase 4 outline setup: the dedicated Volume and the character's rendering-layer mark. The
    /// renderer feature itself is installed by <see cref="HybridRenderSetup"/>.
    /// </summary>
    public static class OutlineSetup
    {
        [MenuItem("Tools/HybridRP/Phase 4/Setup Outline")]
        public static void SetupMenu() => Debug.Log(HybridRenderSetup.Setup());

        public static string Setup() => HybridRenderSetup.Setup();

        /// <summary>
        /// v4 pass: the line takes a darker, more saturated shade of the surface under it (warm brown on
        /// skin, near-black on hair, grey on white cloth), as Endfield's per-material outline colours do,
        /// instead of one near-black colour for the whole figure. Idempotent; usable without a scene.
        /// </summary>
        public static void ApplyLineColour(HybridOutline outline)
        {
            outline.surfaceTint.Override(1f);
            outline.surfaceDarkness.Override(0.3f);
            outline.surfaceSaturation.Override(1.5f);
        }

        public static string ApplyLineColour()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(HybridRenderSetup.OutlineProfilePath);
            if (profile == null || !profile.TryGet<HybridOutline>(out var outline))
                return "No outline profile; run Tools/HybridRP/Phase 4/Setup Outline first.";
            ApplyLineColour(outline);
            EditorUtility.SetDirty(outline);
            AssetDatabase.SaveAssets();
            return "Outline colour taken from the surface (tint 1, darkness 0.3, saturation 1.5).";
        }

        internal static string SetupVolumeAndLayers()
        {
            var driver = HybridCharacterDriver.Find();
            if (driver == null)
                throw new System.InvalidOperationException("Outline setup needs the showcase character.");

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(HybridRenderSetup.OutlineProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, HybridRenderSetup.OutlineProfilePath);
            }
            if (!profile.TryGet<HybridOutline>(out var outline))
            {
                outline = profile.Add<HybridOutline>(true);
                outline.enabled.Override(true);
                AssetDatabase.AddObjectToAsset(outline, profile);
            }
            outline.name = nameof(HybridOutline);
            outline.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
            ApplyLineColour(outline);
            EditorUtility.SetDirty(outline);
            EditorUtility.SetDirty(profile);

            var volume = Resources.FindObjectsOfTypeAll<Volume>().FirstOrDefault(v =>
                v.gameObject.scene.IsValid() && v.sharedProfile == profile);
            bool sceneChanged = false;
            if (volume == null)
            {
                var go = new GameObject("Character Outline Volume");
                go.transform.SetParent(driver.transform.parent, false);
                volume = go.AddComponent<Volume>();
                sceneChanged = true;
            }
            if (!volume.isGlobal || volume.priority != 10f || volume.sharedProfile != profile)
            {
                volume.isGlobal = true;
                volume.priority = 10f;
                volume.sharedProfile = profile;
                sceneChanged = true;
            }
            foreach (var characterRenderer in driver.GetComponentsInChildren<Renderer>(true))
            {
                if ((characterRenderer.renderingLayerMask & HybridRenderFeature.CharacterRenderingLayer) != 0)
                    continue;
                characterRenderer.renderingLayerMask |= HybridRenderFeature.CharacterRenderingLayer;
                EditorUtility.SetDirty(characterRenderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(characterRenderer);
                sceneChanged = true;
            }
            if (sceneChanged)
            {
                EditorSceneManager.MarkSceneDirty(driver.gameObject.scene);
                EditorSceneManager.SaveScene(driver.gameObject.scene);
            }
            return $"outline volume {HybridRenderSetup.OutlineProfilePath}, character layer 0x{HybridRenderFeature.CharacterRenderingLayer:X}" +
                   (sceneChanged ? " (scene updated)" : "");
        }
    }
}
