using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Builds the PBR calibration board: a 5×5 grid of URP Lit spheres (metallic down, smoothness across)
    /// in front of a neutral backdrop, plus a normal-mapped slab beside an identical flat one. It is
    /// the reference for judging the environment's metallic / roughness / normal / reflection response.
    /// The board is hidden by default and shown by the ShowcaseRig "PBRCalibration" bookmarks.
    /// </summary>
    public static class PbrCalibrationBoard
    {
        const string RootName = "PBR Calibration";
        const string MaterialFolder = "Assets/HybridRP/Calibration";
        const string SlabSource = "Assets/Suntail Village/Materials/Building Modules/Stone_Wall.mat";

        const int Grid = 5;
        const float Spacing = 0.75f;
        const float Radius = 0.3f;
        const float NormalSlabBumpScale = 3f;
        static readonly Color Albedo = new Color(0.78f, 0.78f, 0.78f);

        [MenuItem("Tools/HybridRP/Build PBR Calibration Board")]
        public static void BuildFromMenu() => Debug.Log(Build());

        /// <summary>Rebuilds the board at the given world position, facing the given yaw. Returns a summary.</summary>
        public static string Build(Vector3? position = null, float yaw = 0f, Transform parent = null)
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            // AssetDatabase.CreateAsset needs the folder to be known to the database, not just on disk.
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder("Assets/HybridRP", "Calibration");

            // GameObject.Find can't see inactive objects (the board is hidden by default) and a broken
            // build may have left duplicates, so collect every board in the open scene and start over.
            var existing = new List<GameObject>();
            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                if (candidate.name == RootName && candidate.scene.IsValid())
                    existing.Add(candidate);

            // Remember which ShowcaseRig bookmarks pointed at a board before the old ones are destroyed,
            // so re-running the tool cannot leave the rig pointing at a deleted object.
            var rig = ShowcaseRig.Find();
            var rigSerialized = rig != null ? new SerializedObject(rig) : null;
            var bookmarkList = rigSerialized?.FindProperty("_bookmarks");
            var boundBookmarks = new List<int>();
            if (bookmarkList != null)
                for (int i = 0; i < bookmarkList.arraySize; i++)
                {
                    var element = bookmarkList.GetArrayElementAtIndex(i);
                    if (IsOneOf(element.FindPropertyRelative("anchor").objectReferenceValue, existing) ||
                        IsOneOf(element.FindPropertyRelative("reveal").objectReferenceValue, existing))
                        boundBookmarks.Add(i);
                }

            // Reuse the placement/parent of an existing board; prefer a parented one (the scene's).
            GameObject reference = null;
            foreach (var candidate in existing)
                if (candidate.transform.parent != null)
                {
                    reference = candidate;
                    break;
                }
            if (reference == null && existing.Count > 0)
                reference = existing[0];

            Vector3 at = position ?? (reference != null ? reference.transform.position : Vector3.zero);
            float facing = position.HasValue ? yaw : (reference != null ? reference.transform.eulerAngles.y : yaw);
            Transform parentTransform = parent != null ? parent : (reference != null ? reference.transform.parent : null);
            foreach (var stale in existing)
                Object.DestroyImmediate(stale);

            var root = new GameObject(RootName);
            root.transform.SetParent(parentTransform, true);
            root.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, facing, 0f));

            // Backdrop: mid-grey, rough, so the spheres' reflections are the only bright thing behind them.
            float width = (Grid - 1) * Spacing + 1.4f;
            float height = (Grid - 1) * Spacing + 1.4f;
            var backdrop = GameObject.CreatePrimitive(PrimitiveType.Cube);
            backdrop.name = "Backdrop";
            backdrop.transform.SetParent(root.transform, false);
            backdrop.transform.localPosition = new Vector3(0f, height * 0.5f + 0.2f, -0.6f);
            backdrop.transform.localScale = new Vector3(width, height, 0.1f);
            backdrop.GetComponent<Renderer>().sharedMaterial =
                MakeMaterial("M_Calib_Backdrop", lit, new Color(0.35f, 0.35f, 0.35f), 0f, 0.1f);

            // Rows: metallic 0 → 1 from top to bottom. Columns: smoothness 0 → 1 from left to right.
            for (int row = 0; row < Grid; row++)
            for (int col = 0; col < Grid; col++)
            {
                float metallic = row / (float)(Grid - 1);
                float smoothness = col / (float)(Grid - 1);
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"Sphere_M{metallic:0.00}_S{smoothness:0.00}";
                sphere.transform.SetParent(root.transform, false);
                // Local +X is camera-left for a viewer in front of the board, so negate it to run smoothness left to right.
                float x = -(col - (Grid - 1) * 0.5f) * Spacing;
                float y = height - 0.7f - row * Spacing + 0.2f;
                sphere.transform.localPosition = new Vector3(x, y, 0f);
                sphere.transform.localScale = Vector3.one * (Radius * 2f);
                Object.DestroyImmediate(sphere.GetComponent<Collider>());
                sphere.GetComponent<Renderer>().sharedMaterial =
                    MakeMaterial($"M_Calib_M{(int)(metallic * 100):D3}_S{(int)(smoothness * 100):D3}", lit, Albedo, metallic, smoothness);
            }

            // Normal-map pair: the pack's stone with its normal map, and the same material without it.
            var slabMaterial = AssetDatabase.LoadAssetAtPath<Material>(SlabSource);
            if (slabMaterial != null)
            {
                // The pack's normal is authored for GI-lit walls; against this scene's ambient-heavy
                // lighting the bevel shading is too subtle to read in a capture, so the calibration
                // copy amplifies the scale. The environment's Stone_Wall keeps its authored value.
                Material normal = MakeSlabCopy(slabMaterial, "M_Calib_SlabNormal", NormalSlabBumpScale);
                Material flat = MakeSlabCopy(slabMaterial, "M_Calib_SlabFlat", 0f);
                // Beside the sphere grid, so both bookmarks frame them without anything overlapping.
                MakeSlab(root.transform, "Slab_NormalMap", normal, new Vector3(-3.2f, 1.6f, 0f));
                MakeSlab(root.transform, "Slab_Flat", flat, new Vector3(-4.7f, 1.6f, 0f));
            }

            root.SetActive(false);
            Undo.RegisterCreatedObjectUndo(root, "Build PBR Calibration Board");

            foreach (int index in boundBookmarks)
            {
                var element = bookmarkList.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("anchor").objectReferenceValue = root.transform;
                element.FindPropertyRelative("reveal").objectReferenceValue = root;
            }
            if (boundBookmarks.Count > 0)
                rigSerialized.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.SaveAssets();

            return $"Built '{RootName}' at {at} (parent={(parentTransform != null ? parentTransform.name : "<scene root>")}), " +
                   $"replaced {existing.Count} board(s) with {Grid * Grid} spheres" +
                   (slabMaterial != null ? " and 2 slabs" : ", no slab source found") +
                   $"; rebound {boundBookmarks.Count} bookmark(s).";
        }

        static bool IsOneOf(UnityEngine.Object reference, List<GameObject> objects)
        {
            var go = reference as GameObject;
            return go != null && objects.Contains(go);
        }

        static void MakeSlab(Transform parent, string name, Material material, Vector3 localPosition)
        {
            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = name;
            slab.transform.SetParent(parent, false);
            slab.transform.localPosition = localPosition;
            slab.transform.localScale = new Vector3(1.4f, 1.2f, 0.12f);
            Object.DestroyImmediate(slab.GetComponent<Collider>());
            slab.GetComponent<Renderer>().sharedMaterial = material;
        }

        static Material MakeMaterial(string name, Shader lit, Color color, float metallic, float smoothness)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(lit) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = lit;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>Copy of the source slab material with the normal map removed (scale 0) or scaled up.</summary>
        static Material MakeSlabCopy(Material source, string name, float bumpScale)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var copy = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (copy == null)
            {
                copy = new Material(source.shader) { name = name };
                AssetDatabase.CreateAsset(copy, path);
            }
            else
            {
                copy.shader = source.shader;
            }
            copy.CopyPropertiesFromMaterial(source);
            bool hasNormal = source.GetTexture("_BumpMap") != null && bumpScale > 0f;
            if (!hasNormal)
                copy.SetTexture("_BumpMap", null);
            copy.SetFloat("_BumpScale", bumpScale);
            if (hasNormal)
                copy.EnableKeyword("_NORMALMAP");
            else
                copy.DisableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(copy);
            return copy;
        }
    }
}
