using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Phase 3 character material setup.
    ///
    /// Creates the 36 M_ZFY_* materials from material_setup.json (transparency and
    /// double-sided flags), assigns the Hybrid character shaders and textures, remaps them
    /// onto the FBX with ModelImporter.AddRemap, retires MMDSetupMaterials and adds the
    /// HybridCharacterDriver that publishes the head basis / effect weights.
    ///
    /// Run Tools/HybridRP/Phase 3/Setup Character after changing a shader default; use
    /// Rebuild Character Materials to throw the material assets away first.
    /// </summary>
    public static class CharacterShaderSetup
    {
        const string CharacterRoot = "Assets/Characters/ZhuangFangyi";
        const string TextureFolder = CharacterRoot + "/Textures/";
        const string MaterialFolder = CharacterRoot + "/Materials";
        const string FbxPath = CharacterRoot + "/Model/ZhuangFangyi.fbx";
        const string SetupJsonPath = CharacterRoot + "/Model/material_setup.json";

        const string LitShader = "HybridRP/Character/Lit";
        const string FaceShader = "HybridRP/Character/Face";
        const string HairShader = "HybridRP/Character/Hair";
        const string EyeShader = "HybridRP/Character/Eye";
        const string OverlayShader = "HybridRP/Character/Overlay";

        enum Role
        {
            Skin,
            Cloth,
            FaceSkin,
            FaceFeature,
            FaceInner,
            Hair,
            EyeSclera,
            EyeIris,
            EyeHighlight,
            OverlayEyeShadow,
            OverlayHairShadow,
            OverlayBlush,
            OverlayBlushRed,
            OverlayTear,
        }

        sealed class Entry
        {
            public string source;
            public string file;
            public Role role;
        }

        // ASCII names keep the material folder readable; the source names are the PMX
        // material names that the FBX importer exposes to AddRemap.
        static readonly Entry[] Entries =
        {
            new Entry { source = "M_actor_zhuangfy_body_01", file = "Skin_Body", role = Role.Skin },
            new Entry { source = "足", file = "Skin_Foot", role = Role.Skin },

            new Entry { source = "M_actor_zhuangfy_cloth_01", file = "Cloth_Main", role = Role.Cloth },
            new Entry { source = "M_actor_zhuangfy_cloth_02", file = "Cloth_Secondary", role = Role.Cloth },
            new Entry { source = "护腰", file = "Cloth_WaistGuard", role = Role.Cloth },
            new Entry { source = "宋裤", file = "Cloth_Trousers", role = Role.Cloth },
            new Entry { source = "肩甲", file = "Cloth_ShoulderArmor", role = Role.Cloth },
            new Entry { source = "头饰", file = "Cloth_Headwear", role = Role.Cloth },
            new Entry { source = "裙甲", file = "Cloth_SkirtArmor", role = Role.Cloth },
            new Entry { source = "金属", file = "Cloth_Metal", role = Role.Cloth },
            new Entry { source = "玉", file = "Cloth_Jade", role = Role.Cloth },
            new Entry { source = "皮革", file = "Cloth_Leather", role = Role.Cloth },
            new Entry { source = "靴", file = "Cloth_Boots", role = Role.Cloth },
            new Entry { source = "靴metal", file = "Cloth_BootMetal", role = Role.Cloth },
            new Entry { source = "角", file = "Cloth_Horn", role = Role.Cloth },
            new Entry { source = "尾巴", file = "Cloth_Tail", role = Role.Cloth },
            new Entry { source = "尾巴+", file = "Cloth_TailExtra", role = Role.Cloth },

            new Entry { source = "M_actor_zhuangfy_face_01", file = "Face_Skin", role = Role.FaceSkin },
            new Entry { source = "M_actor_zhuangfy_brow_01", file = "Face_Brow", role = Role.FaceFeature },
            new Entry { source = "まつげ", file = "Face_Lashes", role = Role.FaceFeature },
            new Entry { source = "口・鼻線", file = "Face_MouthLine", role = Role.FaceFeature },
            new Entry { source = "唇", file = "Face_Lip", role = Role.FaceFeature },
            new Entry { source = "歯", file = "Face_Teeth", role = Role.FaceInner },
            new Entry { source = "舌", file = "Face_Tongue", role = Role.FaceInner },
            new Entry { source = "口内", file = "Face_MouthInner", role = Role.FaceInner },

            new Entry { source = "M_actor_zhuangfy_hair_01", file = "Hair", role = Role.Hair },

            new Entry { source = "白目", file = "Eye_Sclera", role = Role.EyeSclera },
            new Entry { source = "M_actor_zhuangfy_iris_01", file = "Eye_Iris", role = Role.EyeIris },
            new Entry { source = "目HL", file = "Eye_Highlight", role = Role.EyeHighlight },

            new Entry { source = "M_eyeshadow_common_05", file = "Overlay_EyeShadow", role = Role.OverlayEyeShadow },
            new Entry { source = "M_hairshadow_common_01", file = "Overlay_HairShadow", role = Role.OverlayHairShadow },
            new Entry { source = "照れ", file = "Overlay_Blush", role = Role.OverlayBlush },
            new Entry { source = "赤面", file = "Overlay_BlushRed", role = Role.OverlayBlushRed },
            new Entry { source = "涙", file = "Overlay_Tear1", role = Role.OverlayTear },
            new Entry { source = "涙2", file = "Overlay_Tear2", role = Role.OverlayTear },
            new Entry { source = "涙3", file = "Overlay_Tear3", role = Role.OverlayTear },
        };

        [Serializable]
        sealed class MatEntry
        {
            public string name;
            public string mode;
            public float alpha;
            public bool double_sided;
            public string texture;
        }

        [Serializable]
        sealed class MatSetup
        {
            public List<MatEntry> materials = new List<MatEntry>();
        }

        [MenuItem("Tools/HybridRP/Phase 3/Setup Character")]
        public static void Setup()
        {
            string report = SetupInternal(false);
            Debug.Log(report);
        }

        [MenuItem("Tools/HybridRP/Phase 3/Rebuild Character Materials")]
        public static void Rebuild()
        {
            string report = SetupInternal(true);
            Debug.Log(report);
        }

        [MenuItem("Tools/HybridRP/Phase 3/Report Character")]
        public static void Report()
        {
            Debug.Log(ReportInternal());
        }

        public static string SetupInternal(bool rebuild)
        {
            EnsureFolder(MaterialFolder);

            var setup = LoadSetup();
            int created = 0, updated = 0;
            foreach (Entry entry in Entries)
            {
                MatEntry flags = setup.materials.FirstOrDefault(m => m.name == entry.source);
                if (flags == null)
                {
                    Debug.LogWarning($"[Hybrid] material_setup.json has no entry for '{entry.source}'.");
                    flags = new MatEntry { name = entry.source, mode = "cutout", double_sided = true, alpha = 1f };
                }

                string shaderName = ShaderFor(entry.role);
                Shader targetShader = Shader.Find(shaderName);
                if (targetShader == null)
                {
                    Debug.LogError($"[Hybrid] Shader '{shaderName}' is missing; '{entry.source}' was skipped.");
                    continue;
                }

                string path = MaterialPath(entry);
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material != null && rebuild)
                {
                    AssetDatabase.DeleteAsset(path);
                    material = null;
                }

                if (material == null)
                {
                    material = new Material(targetShader);
                    material.name = "M_ZFY_" + entry.file;
                    AssetDatabase.CreateAsset(material, path);
                    created++;
                }
                else
                {
                    material.shader = targetShader;
                    updated++;
                }

                ConfigureMaterial(material, entry, flags);
                EditorUtility.SetDirty(material);
            }

            AssetDatabase.SaveAssets();
            int remapped = RemapFbx();
            string sceneReport = ConfigureScene();

            return $"[Hybrid] Phase 3 setup: {created} materials created, {updated} updated, {remapped} remapped, {sceneReport}.";
        }

        static string ReportInternal()
        {
            var smr = FindCharacterRenderer();
            if (smr == null)
                return "[Hybrid] Character renderer not found.";

            int hybrid = smr.sharedMaterials.Count(m => m != null && m.shader != null && m.shader.name.StartsWith("HybridRP/Character"));
            string driver = smr.transform.root.GetComponent<HybridCharacterDriver>() != null ? "driver present" : "driver missing";
            return $"[Hybrid] {smr.sharedMaterials.Length} slots, {hybrid} on Hybrid shaders, {driver}, probes {smr.lightProbeUsage}, shadows {smr.shadowCastingMode}/{smr.receiveShadows}.";
        }

        static string ConfigureScene()
        {
            var smr = FindCharacterRenderer();
            if (smr == null)
                return "scene character not found";

            smr.lightProbeUsage = LightProbeUsage.BlendProbes;
            smr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.receiveShadows = true;

            // AddRemap updates the imported model; if the scene instance still holds the
            // old embedded materials (e.g. a stale override), copy the imported order.
            // Copy the prefab's array rather than our Entries order: the SMR slot order is
            // the PMX material order, which differs from the order of Entries (cloth_02,
            // face and eye slots are interleaved).
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var prefabSmr = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            if (prefabSmr != null && prefabSmr.sharedMaterials.Length == smr.sharedMaterials.Length)
            {
                bool same = true;
                for (int i = 0; i < prefabSmr.sharedMaterials.Length; i++)
                    if (smr.sharedMaterials[i] != prefabSmr.sharedMaterials[i]) { same = false; break; }
                if (!same)
                {
                    smr.sharedMaterials = prefabSmr.sharedMaterials;
                    Debug.LogWarning("[Hybrid] The FBX remap did not reach the scene instance; material overrides were written to the character.");
                }
            }
            else if (prefabSmr != null)
            {
                Debug.LogError($"[Hybrid] FBX has {prefabSmr.sharedMaterials.Length} material slots but the scene renderer has {smr.sharedMaterials.Length}; remap skipped.");
            }

            GameObject root = smr.transform.root.gameObject;
            var driver = root.GetComponent<HybridCharacterDriver>();
            if (driver == null)
                driver = Undo.AddComponent<HybridCharacterDriver>(root);

            Transform head = null;
            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate.name == "頭")
                {
                    head = candidate;
                    break;
                }
            }

            var serialized = new SerializedObject(driver);
            SerializedProperty headProperty = serialized.FindProperty("_headBone");
            if (headProperty != null && headProperty.objectReferenceValue == null && head != null)
                headProperty.objectReferenceValue = head;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return driver != null ? $"driver on {root.name}, head {(head != null ? head.name : "missing")}" : "driver could not be added";
        }

        static SkinnedMeshRenderer FindCharacterRenderer()
        {
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.sharedMesh != null && smr.sharedMesh.name.IndexOf("ZhuangFangyi", StringComparison.OrdinalIgnoreCase) >= 0)
                        return smr;
                }
            }
            return null;
        }

        static int RemapFbx()
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError("[Hybrid] ModelImporter not found at " + FbxPath);
                return 0;
            }

            int count = 0;
            foreach (Entry entry in Entries)
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath(entry));
                if (material == null)
                    continue;

                var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), entry.source);
                try
                {
                    importer.RemoveRemap(id);
                }
                catch
                {
                    // RemoveRemap throws when nothing is mapped yet; harmless.
                }
                importer.AddRemap(id, material);
                count++;
            }

            importer.SaveAndReimport();
            return count;
        }

        static void ConfigureMaterial(Material material, Entry entry, MatEntry flags)
        {
            material.shaderKeywords = Array.Empty<string>();
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = -1;

            bool transparent = IsOverlay(entry.role);
            material.SetFloat("_Cull", flags.double_sided ? (float)CullMode.Off : (float)CullMode.Back);
            material.SetFloat("_Cutoff", 0.5f);

            SetTexture(material, "_BaseMap", BaseTexture(entry.role));
            SetColor(material, "_BaseColor", Color.white);

            switch (entry.role)
            {
                // v4 pass (2026-11-18): values calibrated against the official Endfield renders of this
                // character with colour probes over matching crops. Base colours are white because
                // the textures already carry the authored skin hue; the shadow colours counter the blue sky
                // fill instead of adding to it.
                case Role.Skin:
                    SetTexture(material, "_BumpMap", "T_actor_zhuangfy_body_01_N.png");
                    SetTexture(material, "_RampMap", "T_actor_common_body_01_RD.png");
                    Enable(material, "_RAMP_ON", "_SHADOWCOLOR_ON", "_SPEC_ON", "_RIM_ON");
                    SetLit(material, usePMap: 0f, metallic: 0f, smoothness: 0.32f, smoothnessScale: 1f,
                           occlusion: 1f, shadowColor: new Color(0.95f, 0.74f, 0.70f), shadowStrength: 0.7f,
                           specIntensity: 0.35f, rimIntensity: 0.25f, ssao: 0.06f);
                    SetFloat(material, "_RampWeight", 0.7f);
                    SetFloat(material, "_DirectLightingIntensity", 0.9f);
                    // The chin must still shadow the neck; 12 cm of smoothing offset skipped it.
                    SetFloat(material, "_SelfShadowOffset", 0.03f);
                    break;

                case Role.Cloth:
                    SetTexture(material, "_BumpMap", "T_actor_zhuangfy_cloth_01_N.png");
                    SetTexture(material, "_PMap", "T_actor_zhuangfy_cloth_01_P.png");
                    SetTexture(material, "_RampMap", "T_actor_common_cloth_04_RD.png");
                    Enable(material, "_RAMP_ON", "_SHADOWCOLOR_ON", "_SPEC_ON", "_RIM_ON");
                    SetLit(material, usePMap: 1f, metallic: 1f, smoothness: 0.45f, smoothnessScale: 1f,
                           occlusion: 1f, shadowColor: new Color(0.90f, 0.86f, 0.84f), shadowStrength: 0.8f,
                           specIntensity: 1f, rimIntensity: 0.35f, ssao: 0.5f);
                    SetFloat(material, "_RampWeight", 0.9f);
                    // Metal keeps a third of its lit diffuse so it shades with the sun; with none, the
                    // baked probe reflection was its whole look and stayed put when the light moved.
                    SetFloat(material, "_MetalDiffuse", 0.35f);
                    break;

                case Role.FaceSkin:
                case Role.FaceFeature:
                case Role.FaceInner:
                {
                    SetTexture(material, "_RampMap", "T_actor_common_face_01_RD.png");
                    SetTexture(material, "_SDFMap", "T_actor_common_female_face_02_SDF.png");
                    SetTexture(material, "_FaceMaskMap", "T_actor_common_female_face_01_cm_M.png");
                    // face_03 paints the nose-tip highlight as well as the lip one (face_01 is lip only).
                    SetTexture(material, "_LipMaskMap", "T_actor_common_face_03_hl_M.png");
                    Enable(material, "_RAMP_ON", "_SHADOWCOLOR_ON", "_SPEC_ON", "_RIM_ON");
                    bool sdf = entry.role != Role.FaceInner;
                    bool feature = entry.role == Role.FaceFeature && entry.file != "Face_Lip";
                    if (sdf)
                        Enable(material, "_SDF_ON");
                    SetFloat(material, "_SDFWeight", sdf ? 1f : 0f);
                    SetFloat(material, "_SDFSoftness", 0.2f);
                    SetFloat(material, "_SDFThresholdOffset", 0f);
                    SetColor(material, "_SideTintColor", new Color(1f, 0.92f, 0.88f));
                    SetFloat(material, "_SideTintStrength", 0.12f);
                    SetColor(material, "_LipColor", new Color(1f, 0.82f, 0.78f));
                    SetFloat(material, "_LipIntensity", entry.role == Role.FaceSkin || entry.file == "Face_Lip" ? 0.25f : 0f);
                    SetColor(material, "_ShadowColor", new Color(1f, 0.80f, 0.76f));
                    SetFloat(material, "_ShadowColorStrength", 0.8f);
                    SetFloat(material, "_SpecIntensity", feature ? 0f : 0.05f);
                    SetFloat(material, "_Smoothness", 0.45f);
                    SetColor(material, "_RimColor", new Color(1f, 0.86f, 0.82f));
                    SetFloat(material, "_RimPower", 3f);
                    SetFloat(material, "_RimIntensity", entry.role == Role.FaceInner || feature ? 0f : 0.3f);
                    SetFloat(material, "_FaceRimStrength", 1f);
                    SetFloat(material, "_SSAOStrength", 0.05f);
                    // Brows, lashes and the mouth line are drawn lines: lit like the face they read as
                    // grey-brown smears; the references keep them near-black in any light, the upper lash
                    // line darkest of all.
                    bool lashes = entry.file == "Face_Lashes";
                    SetFloat(material, "_DirectLightingIntensity", lashes ? 0.32f : feature ? 0.45f : 0.9f);
                    SetFloat(material, "_GIIntensity", lashes ? 0.32f : feature ? 0.45f : 1f);
                    break;
                }

                case Role.Hair:
                    SetTexture(material, "_PMap", "T_actor_zhuangfy_hair_01_P.png");
                    SetTexture(material, "_RampMap", "T_actor_common_hair_01_RD.png");
                    SetTexture(material, "_HairNormalMap", "T_actor_zhuangfy_hair_01_HN.png");
                    SetTexture(material, "_HairShiftMap", "T_actor_common_hairst_01_ST.png");
                    Enable(material, "_RAMP_ON", "_SHADOWCOLOR_ON", "_SPEC_ON", "_RIM_ON", "_HAIRHL_ON");
                    SetLit(material, usePMap: 0f, metallic: 0f, smoothness: 0.42f, smoothnessScale: 1f,
                           occlusion: 1f, shadowColor: new Color(0.86f, 0.90f, 0.80f), shadowStrength: 0.7f,
                           specIntensity: 0.15f, rimIntensity: 0.1f, ssao: 0.35f);
                    // The texture is a mid grey-green; on screen the hair is a near-black grey-green. It is
                    // darkened (and pulled from teal toward neutral against the blue sky fill) but keeps most
                    // of its direct light, so its light and shadow masses follow the sun: lit mostly by the
                    // flattened probe GI it looks the same under every lighting preset.
                    SetColor(material, "_BaseColor", new Color(0.70f, 0.72f, 0.62f));
                    SetFloat(material, "_DirectLightingIntensity", 0.8f);
                    SetFloat(material, "_GIIntensity", 1f);
                    SetFloat(material, "_OcclusionStrength", 1f);
                    SetColor(material, "_HairHLColor", new Color(1f, 0.98f, 0.95f));
                    // Thin primary band, broad hair-coloured sheen: the references' bangs show soft grey-
                    // green bands across the strands rather than bright streaks.
                    SetFloat(material, "_HairHLIntensity", 0.25f);
                    SetFloat(material, "_HairHLPower", 48f);
                    SetFloat(material, "_HairHLWidth", 0.5f);
                    SetFloat(material, "_HairHLShift", 0f);
                    SetFloat(material, "_HairHLViewPower", 2f);
                    SetFloat(material, "_HairHLMaskStrength", 1f);
                    SetFloat(material, "_HairHLAlbedoTint", 0.6f);
                    SetFloat(material, "_HairSheenIntensity", 1.6f);
                    SetFloat(material, "_HairSheenPower", 6f);
                    // The teal underside and red tips (the character's signature) stay coloured.
                    SetFloat(material, "_HairAccentSaturation", 1.8f);
                    SetFloat(material, "_BumpScale", 1f);
                    break;

                case Role.EyeSclera:
                case Role.EyeIris:
                case Role.EyeHighlight:
                    SetFloat(material, "_Cull", 0f);
                    SetFloat(material, "_Cutoff", 0.5f);
                    SetFloat(material, "_Smoothness", entry.role == Role.EyeIris ? 0.65f : 0.5f);
                    SetFloat(material, "_SpecIntensity", entry.role == Role.EyeIris ? 0.35f : 0.1f);
                    SetFloat(material, "_AmbientStrength", entry.role == Role.EyeHighlight ? 0f : 0.85f);
                    SetFloat(material, "_SSAOStrength", entry.role == Role.EyeHighlight ? 0f : 0.15f);
                    SetColor(material, "_EmissionColor", new Color(1f, 0.95f, 0.85f));
                    SetFloat(material, "_EmissionStrength", entry.role == Role.EyeHighlight ? 1.6f : 0f);
                    // The iris texture has teal and orange rings; the references' iris is a muted olive-gold
                    // with a dark band under the upper lid. Desaturate, tint gold, shade the top third.
                    bool iris = entry.role == Role.EyeIris;
                    if (iris)
                        SetColor(material, "_BaseColor", new Color(1f, 0.88f, 0.62f));
                    SetFloat(material, "_Saturation", iris ? 0.35f : 1f);
                    SetColor(material, "_IrisShadeColor", iris ? new Color(0.42f, 0.33f, 0.28f) : Color.white);
                    SetFloat(material, "_IrisShadeStart", 0.4f);
                    SetFloat(material, "_IrisShadeEnd", 0.75f);
                    break;

                case Role.OverlayEyeShadow:
                case Role.OverlayHairShadow:
                    // Multiply: the eyelid shadow on the eyeball and the bangs' shadow on the forehead
                    // tint the lit colour under them (cool grey on the eye, warm on skin).
                    SetFloat(material, "_Cull", 0f);
                    SetFloat(material, "_OverlayMode", 0f);
                    SetFloat(material, "_OverlayGroup", 0f);
                    SetFloat(material, "_OverlayAlpha", 1f);
                    SetColor(material, "_OverlayColor", entry.role == Role.OverlayEyeShadow
                        ? new Color(0.62f, 0.58f, 0.68f) : new Color(0.68f, 0.50f, 0.48f));
                    SetOverlayBlend(material, multiply: true);
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.renderQueue = (int)RenderQueue.Transparent;
                    break;

                case Role.OverlayBlush:
                case Role.OverlayBlushRed:
                case Role.OverlayTear:
                    SetFloat(material, "_OverlayMode", 1f);
                    SetFloat(material, "_OverlayGroup", entry.role == Role.OverlayTear ? 2f : 1f);
                    SetFloat(material, "_OverlayAlpha", 1f);
                    SetColor(material, "_OverlayColor", Color.white);
                    // Blush multiplies (it follows the face's light); tears stay an alpha layer.
                    SetOverlayBlend(material, multiply: entry.role != Role.OverlayTear);
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.renderQueue = (int)RenderQueue.Transparent;
                    break;
            }

            // Keep the variant set minimal: only face-skin/features compile the SDF variant,
            // only hair compiles the highlight-band variant. Set the [Toggle] floats too:
            // Unity re-derives the keyword from the toggle property on reimport.
            bool useSdf = entry.role == Role.FaceSkin || entry.role == Role.FaceFeature;
            SetFloat(material, "_SDFOn", useSdf ? 1f : 0f);
            SetFloat(material, "_HairHLOn", entry.role == Role.Hair ? 1f : 0f);
            if (!useSdf)
                material.DisableKeyword("_SDF_ON");
            if (entry.role != Role.Hair)
                material.DisableKeyword("_HAIRHL_ON");
            SetFloat(material, "_RampOn", 1f);
            SetFloat(material, "_ShadowColorOn", 1f);
            SetFloat(material, "_SpecOn", 1f);
            SetFloat(material, "_RimOn", 1f);

            if (transparent)
            {
                SetFloat(material, "_Cull", flags.double_sided ? 0f : 2f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)RenderQueue.Transparent;
            }
            else if (!string.IsNullOrEmpty(flags.mode) && flags.mode == "cutout")
            {
                material.renderQueue = 2450;
                material.SetOverrideTag("RenderType", "TransparentCutout");
            }
        }

        static void SetLit(Material m, float usePMap, float metallic, float smoothness, float smoothnessScale,
                           float occlusion, Color shadowColor, float shadowStrength, float specIntensity,
                           float rimIntensity, float ssao)
        {
            SetFloat(m, "_UsePMap", usePMap);
            SetFloat(m, "_Metallic", metallic);
            SetFloat(m, "_Smoothness", smoothness);
            SetFloat(m, "_SmoothnessScale", smoothnessScale);
            SetFloat(m, "_OcclusionStrength", occlusion);
            SetFloat(m, "_BumpScale", 1f);
            SetFloat(m, "_RampWeight", 1f);
            SetFloat(m, "_ShadowColorStrength", shadowStrength);
            SetColor(m, "_ShadowColor", shadowColor);
            SetColor(m, "_SpecColor", Color.white);
            SetFloat(m, "_SpecIntensity", specIntensity);
            SetColor(m, "_RimColor", Color.white);
            SetFloat(m, "_RimPower", 4f);
            SetFloat(m, "_RimIntensity", rimIntensity);
            SetFloat(m, "_SSAOStrength", ssao);
            SetFloat(m, "_DirectLightingIntensity", 1f);
            SetFloat(m, "_GIIntensity", 1f);
            SetFloat(m, "_SelfShadowOffset", 0.12f);
        }

        static void SetOverlayBlend(Material m, bool multiply)
        {
            SetFloat(m, "_OverlayBlend", multiply ? 1f : 0f);
            SetFloat(m, "_SrcBlend", (float)(multiply ? BlendMode.DstColor : BlendMode.SrcAlpha));
            SetFloat(m, "_DstBlend", (float)(multiply ? BlendMode.Zero : BlendMode.OneMinusSrcAlpha));
        }

        static void Enable(Material m, params string[] keywords)
        {
            foreach (string keyword in keywords)
                m.EnableKeyword(keyword);
        }

        static void SetFloat(Material m, string name, float value)
        {
            if (m.HasProperty(name))
                m.SetFloat(name, value);
        }

        static void SetColor(Material m, string name, Color value)
        {
            if (m.HasProperty(name))
                m.SetColor(name, value);
        }

        static void SetTexture(Material m, string name, string file)
        {
            if (string.IsNullOrEmpty(file) || !m.HasProperty(name))
                return;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TextureFolder + file);
            if (texture == null)
            {
                Debug.LogWarning("[Hybrid] Missing texture " + TextureFolder + file);
                return;
            }
            m.SetTexture(name, texture);
        }

        static string BaseTexture(Role role)
        {
            switch (role)
            {
                case Role.Skin: return "T_actor_zhuangfy_body_01_D.png";
                case Role.Cloth: return "T_actor_zhuangfy_cloth_01_D.png";
                case Role.FaceSkin:
                case Role.FaceFeature:
                case Role.FaceInner:
                case Role.EyeSclera: return "T_actor_zhuangfy_face_01_D.png";
                case Role.Hair: return "T_actor_zhuangfy_hair_01_D.png";
                case Role.EyeIris:
                case Role.EyeHighlight: return "T_actor_zhuangfy_iris_01_D.png";
                case Role.OverlayEyeShadow: return "T_actor_common_eyeshadow_01_M.png";
                case Role.OverlayHairShadow: return "T_actor_common_hairshadow_01_M.png";
                case Role.OverlayBlush: return "T_ZFY_BlushTere.png";
                case Role.OverlayBlushRed: return "T_ZFY_BlushSekimen.png";
                case Role.OverlayTear: return "T_ZFY_Tear.png";
                default: return null;
            }
        }

        static bool IsOverlay(Role role) =>
            role == Role.OverlayEyeShadow || role == Role.OverlayHairShadow ||
            role == Role.OverlayBlush || role == Role.OverlayBlushRed || role == Role.OverlayTear;

        static string ShaderFor(Role role)
        {
            switch (role)
            {
                case Role.Skin:
                case Role.Cloth: return LitShader;
                case Role.FaceSkin:
                case Role.FaceFeature:
                case Role.FaceInner: return FaceShader;
                case Role.Hair: return HairShader;
                case Role.EyeSclera:
                case Role.EyeIris:
                case Role.EyeHighlight: return EyeShader;
                default: return OverlayShader;
            }
        }

        static string MaterialPath(Entry entry) => $"{MaterialFolder}/M_ZFY_{entry.file}.mat";

        static MatSetup LoadSetup()
        {
            if (!File.Exists(SetupJsonPath))
                return new MatSetup();
            string json = File.ReadAllText(SetupJsonPath);
            var setup = JsonUtility.FromJson<MatSetup>(json);
            return setup ?? new MatSetup();
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent))
                AssetDatabase.CreateFolder(parent, name);
        }
    }
}
