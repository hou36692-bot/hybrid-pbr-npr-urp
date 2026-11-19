using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Reproducible setup of the Wuling hero scene (Endfield-inspired portfolio pass): a copy of the
    /// Asian Dynasty URP demo map with the HybridRP character, showcase rig and volumes, an
    /// APV-only GI chain and a daylight look. Every step is idempotent.
    ///
    /// Why APV-only: the map has ~17k static renderers without lightmap UVs; a lightmap atlas for it
    /// would need UV generation on 184 vendor FBXs and several 4k atlases. APV with sky occlusion
    /// gives the soft directional bounce the look needs, and the environment's Shader Graph Lit
    /// materials sample it per pixel.
    /// </summary>
    public static class WulingShowcaseSetup
    {
        public const string ScenePath = "Assets/Scenes/Wuling/WulingShowcase.unity";
        const string SourceScenePath = "Assets/BefourStudios/AsianDynastyEnvironment/Scenes/DemoMap_URP.unity";
        const string MainScenePath = "Assets/Scenes/HybridRenderPipeline.unity";
        const string Folder = "Assets/Scenes/Wuling";
        const string SkyPath = Folder + "/M_WulingSky.mat";
        const string LightingSettingsPath = Folder + "/Wuling_Lighting.lighting";
        const string BakingSetPath = Folder + "/Wuling Baking Set.asset";
        const string RootName = "HybridRP Showcase";

        // Hero spot in the main temple courtyard, facing the demo's CameraAnimation01 framing.
        static readonly Vector3 CharacterPosition = new Vector3(13.05f, 11.51f, 13.08f);
        static readonly Vector3 CharacterFaces = new Vector3(4.39f, 11.51f, 15.54f);
        static readonly Vector3 HeroApvCenter = new Vector3(8f, 16f, 10f);
        static readonly Vector3 HeroApvSize = new Vector3(70f, 22f, 70f);

        // The character is exposed apart from the scene (HybridCharacterTone): at the courtyard's sun 2.6
        // she over-exposed (grey hair, white skin). Lit skin lands at the official renders' level (~sRGB
        // 235/218/204) when her total gain, her own exposure times the grade's post exposure, is ~1.054.
        // How that gain is split sets how bright the courtyard sits around her: a smaller character share
        // and a larger post exposure keep her on-screen level and lift the scene, which closes the
        // brightness gap that made her look pasted onto a darker background.
        const float CharacterScreenGain = 1.054f;
        const float CharacterExposure = 0.62f;
        static float GradePostExposure => Mathf.Log(CharacterScreenGain / CharacterExposure, 2f);

        [MenuItem("Tools/HybridRP/Wuling/Create Or Update Scene")]
        public static void SetupMenu() => Debug.Log(Setup());

        [MenuItem("Tools/HybridRP/Wuling/Bake Probes")]
        public static void BakeMenu() => Debug.Log(BakeProbes());

        /// <summary>Creates the scene from the vendor demo map if needed, then applies every step.</summary>
        public static string Setup()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                if (!AssetDatabase.IsValidFolder(Folder))
                    AssetDatabase.CreateFolder("Assets/Scenes", "Wuling");
                if (!AssetDatabase.CopyAsset(SourceScenePath, ScenePath))
                    throw new InvalidOperationException("Could not copy " + SourceScenePath);
            }
            var scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath);

            var report = new StringBuilder();
            report.AppendLine(DisableDemoObjects());
            var root = EnsureRoot();
            report.AppendLine(EnsureCharacter(scene));
            report.AppendLine(EnsureRigAndCamera(root));
            report.AppendLine(EnsureVolumes(root));
            report.AppendLine(EnsureLens(root));
            report.AppendLine(OutlineSetup.ApplyLineColour());
            report.AppendLine(ConfigureSkyAndSun());
            // The rig scales ambient relative to what it captured; re-capture after the sky changed.
            Object.FindFirstObjectByType<ShowcaseRig>().CaptureAmbient();
            report.AppendLine(ConfigureStaticsForApv());
            report.AppendLine(RetuneEnvironmentMaterials());
            report.AppendLine(ConfigureApv(scene, root));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            return report.ToString();
        }

        static string DisableDemoObjects()
        {
            // The demo's 30 cameras would each render if left enabled; its HDRP-style sky/fog
            // volume has no URP effect but would hide which profile is driving the image.
            int disabled = 0;
            foreach (var name in new[] { "Cameras", "Sky and Fog Global Volume" })
            {
                var go = FindRoot(name);
                if (go != null && go.activeSelf)
                {
                    go.SetActive(false);
                    disabled++;
                }
            }
            RenderSettings.fog = false; // HybridVolumetricFog replaces the built-in fog, as in the main scene
            return $"[demo] disabled {disabled} demo root(s); built-in fog off.";
        }

        static GameObject EnsureRoot()
        {
            var root = FindRoot(RootName);
            return root != null ? root : new GameObject(RootName);
        }

        static string EnsureCharacter(Scene scene)
        {
            var character = Object.FindFirstObjectByType<HybridCharacterDriver>();
            if (character == null)
            {
                // Copy the configured instance (36 Hybrid materials, driver, outline layer) from the
                // main scene instead of re-running the Phase 3 setup against a second instance.
                var main = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Additive);
                try
                {
                    var source = main.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<HybridCharacterDriver>() != null);
                    if (source == null)
                        throw new InvalidOperationException("No HybridCharacterDriver in " + MainScenePath);
                    var copy = Object.Instantiate(source);
                    copy.name = source.name;
                    SceneManager.MoveGameObjectToScene(copy, scene);
                    character = copy.GetComponent<HybridCharacterDriver>();
                }
                finally
                {
                    EditorSceneManager.CloseScene(main, true);
                }
            }
            var t = character.transform;
            t.position = CharacterPosition;
            Vector3 facing = CharacterFaces - CharacterPosition;
            facing.y = 0f;
            t.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
            foreach (var r in character.GetComponentsInChildren<Renderer>(true))
                r.renderingLayerMask |= HybridRenderFeature.CharacterRenderingLayer;

            // Stylised shadows on for this scene (the main scene keeps the Stage 2 defaults of 0): a light
            // shadow lift and a faint, surface-tinted screen rim (a full-strength white rim read as a halo
            // around the figure). The lift stays small: a larger one left her shadow side brighter than the
            // courtyard's shadows beside her, and in full-body frames she read as self-lit.
            character.SetStylisedShadows(1f, 0.06f, 0.25f, 2f);
            // Character light rig (see CharacterScreenGain). The toe contrast holds hair and black leather
            // near their on-screen values in the official renders. A blush as in the official portraits.
            character.SetCharacterExposure(CharacterExposure, 1.25f, 1.0f);
            character.SetOverlayWeights(0.4f, 0f);
            // Highlights follow the key light (probe reflections dim in shadow, rims take the light's hue
            // and shadow), and the environment rim drops to 0.15 so the rim sits where the sun is. The
            // probe-GI flattening and position lock keep their Stage 2 values.
            character.SetHighlightLightDrive(1f);
            character.SetIntegrationWeights(0.75f, 0.85f, 0.15f, 0.8f);
            EditorUtility.SetDirty(character);

            // The PMX ships in an A-pose with the arms ~45 degrees out, which reads as a T-pose
            // turntable in a hero frame. Arms down, a soft elbow bend and relaxed, slightly curled
            // fingers, stored as absolute local rotations so re-running the setup does not compound
            // them. The bones are scene-instance overrides only; the FBX and prefab keep the bind pose.
            int posed = 0;
            foreach (var bone in character.GetComponentsInChildren<Transform>(true))
            {
                foreach (var (name, rotation) in RelaxedPose)
                {
                    if (bone.name != name)
                        continue;
                    bone.localRotation = rotation;
                    posed++;
                }
            }
            // An Editor in the background does not tick the player loop, so skinning would keep the
            // old pose until the next tick; re-enabling the renderer re-skins it now.
            foreach (var skinned in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!skinned.enabled)
                    continue;
                skinned.enabled = false;
                skinned.enabled = true;
            }
            return $"[character] {character.name} at {t.position}, yaw {t.eulerAngles.y:F1}, {posed} pose bones.";
        }

        // Arms down with a soft elbow bend; each finger joint turned toward the palm by 0.9 x (index
        // 14/22/16, middle 20/28/18, ring 26/32/20, little 32/36/22, thumb 6/12/14 degrees, base to tip),
        // the little finger curling most, as a hand at rest does.
        static readonly (string, Quaternion)[] RelaxedPose =
        {
            ("右腕", new Quaternion(-0.6988469f, -0.10326916f, 0.6905223f, -0.15533f)),
            ("右ひじ", new Quaternion(0.00108101952f, -7.030748E-05f, 0.1543432f, 0.988016665f)),
            ("左腕", new Quaternion(0.690922141f, -0.154856548f, -0.6984877f, -0.103735432f)),
            ("左ひじ", new Quaternion(-0.00116283493f, 6.73182658E-05f, 0.154399186f, 0.988007843f)),
            ("右親指０", new Quaternion(0.05428819f, 0.3040691f, 0.3550771f, 0.882335f)),
            ("右親指１", new Quaternion(0.0265418086f, 0.3494847f, 0.07241526f, 0.9337623f)),
            ("右親指２", new Quaternion(0.0146273011f, -0.000320911262f, 0.105010353f, 0.9943635f)),
            ("右人指１", new Quaternion(0.07275476f, 0.005471171f, 0.05721888f, 0.995692134f)),
            ("右人指２", new Quaternion(0.1746383f, -0.000638433965f, 0.0376907364f, 0.983910859f)),
            ("右人指３", new Quaternion(0.120942339f, 0.000578538165f, 0.030152211f, 0.9922013f)),
            ("右中指１", new Quaternion(0.120136224f, -0.000157513772f, 0.02698355f, 0.9923907f)),
            ("右中指２", new Quaternion(0.211296871f, 6.2681E-05f, 0.0459503643f, 0.9763412f)),
            ("右中指３", new Quaternion(0.136146262f, 0.0009461034f, 0.03472174f, 0.9900797f)),
            ("右薬指１", new Quaternion(0.16001527f, -0.0122729084f, -0.01579046f, 0.9869119f)),
            ("右薬指２", new Quaternion(0.252423048f, 0.000837188039f, 0.0510552339f, 0.9662688f)),
            ("右薬指３", new Quaternion(0.154549152f, 0.00197479222f, 0.04010832f, 0.98716867f)),
            ("右小指１", new Quaternion(0.21496807f, -0.025576869f, -0.0472023748f, 0.9751444f)),
            ("右小指２", new Quaternion(0.2871827f, 0.003462705f, 0.0594567172f, 0.956022561f)),
            ("右小指３", new Quaternion(0.172704846f, 0.00284966966f, 0.04095544f, 0.9841176f)),
            ("左親指０", new Quaternion(-0.0539121069f, -0.30412066f, 0.356073439f, 0.881938756f)),
            ("左親指１", new Quaternion(-0.0265508052f, -0.349311084f, 0.07242069f, 0.933826566f)),
            ("左親指２", new Quaternion(-0.0201854687f, 0.00111839094f, 0.102238506f, 0.99455446f)),
            ("左人指１", new Quaternion(-0.07270607f, -0.00563468924f, 0.0583603f, 0.9956286f)),
            ("左人指２", new Quaternion(-0.174638778f, 0.000652540242f, 0.03772949f, 0.983909249f)),
            ("左人指３", new Quaternion(-0.1206958f, 0.0003383785f, 0.0238042269f, 0.9924041f)),
            ("左中指１", new Quaternion(-0.120099843f, -5.552117E-05f, 0.0281262062f, 0.992363334f)),
            ("左中指２", new Quaternion(-0.211296037f, -6.5739674E-05f, 0.04600009f, 0.976339042f)),
            ("左中指３", new Quaternion(-0.135807842f, -3.76127937E-05f, 0.029650528f, 0.9902915f)),
            ("左薬指１", new Quaternion(-0.160005674f, 0.0120014427f, -0.0146477856f, 0.9869344f)),
            ("左薬指２", new Quaternion(-0.252407342f, -0.000810826f, 0.05111197f, 0.9662699f)),
            ("左薬指３", new Quaternion(-0.154012918f, -0.0009779296f, 0.03586063f, 0.9874174f)),
            ("左小指１", new Quaternion(-0.214969084f, 0.02527296f, -0.04607557f, 0.975205958f)),
            ("左小指２", new Quaternion(-0.2871858f, -0.00343496259f, 0.0594990626f, 0.956019044f)),
            ("左小指３", new Quaternion(-0.171641484f, -0.00145754789f, 0.03718712f, 0.9844563f)),
        };

        static string EnsureRigAndCamera(GameObject root)
        {
            var rig = Object.FindFirstObjectByType<ShowcaseRig>();
            if (rig == null)
            {
                var go = new GameObject("HybridRP");
                go.transform.SetParent(root.transform, false);
                rig = go.AddComponent<ShowcaseRig>();
            }
            var cam = rig.Camera;
            if (cam == null)
            {
                var go = new GameObject("Showcase Camera");
                go.transform.SetParent(root.transform, false);
                cam = go.AddComponent<Camera>();
                go.AddComponent<UniversalAdditionalCameraData>();
            }
            cam.gameObject.tag = "MainCamera";
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 1000f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;

            var so = new SerializedObject(rig);
            so.FindProperty("_camera").objectReferenceValue = cam;
            so.FindProperty("_sun").objectReferenceValue = FindSun();
            so.FindProperty("_character").objectReferenceValue = Object.FindFirstObjectByType<HybridCharacterDriver>().transform;
            WritePresets(so.FindProperty("_presets"));
            WriteBookmarks(so.FindProperty("_bookmarks"));
            so.ApplyModifiedPropertiesWithoutUndo();
            return $"[rig] {rig.Bookmarks.Count} bookmarks, {rig.Presets.Count} presets, camera {cam.name}.";
        }

        // Sun directions are relative to the character's facing (0 = from the front, positive = her
        // right, as ShowcaseRig.LightingPreset documents; ThreeQuarter and Golden come from her left).
        // Roofs and the tree canopy around the hero spot only let full sun reach her from yaw -40..+20
        // (rendered sweep with the shadow-attenuation debug view; the canopy has no colliders, so a ray
        // scan is not enough). The presets stay inside that window except Dappled, which sits just
        // outside it on purpose: the canopy's leaf shadow crosses her body and the paving around her in
        // one pattern, the clearest evidence that she is lit by the scene's light. The Front/Side/Back/
        // LowAmbient validation set lives in the main Suntail scene. Intensities are for this map's
        // exposure (GradePostExposure), not the Suntail presets (sun 1.05).
        static readonly Color SunWarm = new Color(1.00f, 0.92f, 0.82f);
        static readonly Color SunGolden = new Color(1.00f, 0.80f, 0.60f);
        static readonly (string name, float yaw, float elevation, float intensity, Color color, float ambient)[] Presets =
        {
            ("Front",        -10f, 50f, 2.6f, SunWarm, 1f),
            ("ThreeQuarter", -40f, 32f, 2.6f, SunWarm, 1f),
            ("High",          20f, 60f, 2.6f, SunWarm, 1f),
            ("Golden",       -40f, 26f, 2.4f, SunGolden, 0.85f),
            ("LowAmbient",   -10f, 50f, 2.3f, SunWarm, 0.25f),
            ("Dappled",      -61f, 30f, 2.4f, SunGolden, 0.85f),
        };

        // Positions are in the character's yaw frame (+Z = where she faces, +X = her right).
        // The last three columns are the lens: f-stop (0 = no depth of field), focal length (mm) and the
        // focus point. The portfolio framings use a lens focused on the eyes, as the official close-ups
        // do; the comparison bookmarks (Overview, FullBody, Face, Hair, ContactShadow, Portrait, Wide) stay
        // sharp. The full-body framings (Hero, LowHero) use f/4: sharp, the courtyard's fine stone and
        // timber detail competed with the character's flat NPR shading and set her apart from the scene.
        static readonly Vector3 Eyes = new Vector3(0f, 1.58f, 0.08f);
        static readonly (string name, Vector3 position, Vector3 lookAt, float fov, float aperture, float focal, Vector3 focus)[] Bookmarks =
        {
            ("Overview", new Vector3(-2.6f, 2.2f, 9f),     new Vector3(0f, 1.2f, 0f),   50f, 0f, 85f, Vector3.zero),
            ("FullBody", new Vector3(0f, 1.0f, 3.8f),      new Vector3(0f, 0.95f, 0f),  32f, 0f, 85f, Vector3.zero),
            ("Face",     new Vector3(0f, 1.6f, 0.75f),     new Vector3(0f, 1.58f, 0f),  24f, 0f, 85f, Vector3.zero),
            ("Hair",     new Vector3(-0.85f, 1.8f, -0.7f), new Vector3(0f, 1.55f, 0f),  30f, 0f, 85f, Vector3.zero),
            ("ContactShadow", new Vector3(2.4f, 1.1f, 1.2f), new Vector3(0f, 0.3f, -0.4f), 40f, 0f, 85f, Vector3.zero),
            // Hero: low three-quarter view with the main hall's eaves and lanterns behind her.
            ("Hero",     new Vector3(1.3f, 0.55f, 3.3f),   new Vector3(0.15f, 1.12f, 0f), 34f, 4f, 85f, Eyes),
            // Portrait: bust, slight three-quarter, eye level.
            ("Portrait", new Vector3(0.55f, 1.55f, 1.25f), new Vector3(0f, 1.45f, 0f),  28f, 0f, 85f, Vector3.zero),
            // Wide: the courtyard as an environment establishing shot.
            ("Wide",     new Vector3(-4.5f, 1.7f, 13f),    new Vector3(1.5f, 2.6f, -6f), 52f, 0f, 85f, Vector3.zero),
            // Portfolio framings. CloseUp is on the key's side (she is front-lit, the red stalls behind
            // her), CloseUpRim on the far side (the key rims her cheek and hair).
            ("CloseUp",    new Vector3(-0.45f, 1.6f, 0.85f), new Vector3(0f, 1.56f, 0.05f), 22f, 2.8f, 85f, Eyes),
            ("CloseUpRim", new Vector3(0.45f, 1.6f, 0.85f),  new Vector3(0f, 1.56f, 0.05f), 22f, 2.8f, 85f, Eyes),
            ("Medium",     new Vector3(-0.9f, 1.45f, 1.9f),  new Vector3(0f, 1.3f, 0f),     26f, 2.8f, 85f, Eyes),
            ("Profile",    new Vector3(-1.2f, 1.55f, 0.2f),  new Vector3(0f, 1.5f, 0.1f),   24f, 2.8f, 85f, new Vector3(0f, 1.56f, 0.05f)),
            ("LowHero",    new Vector3(-1.4f, 0.4f, 2.8f),   new Vector3(0f, 1.1f, 0f),     38f, 4f, 85f, Eyes),
        };

        static void WritePresets(SerializedProperty list)
        {
            list.arraySize = Presets.Length;
            for (int i = 0; i < Presets.Length; i++)
            {
                var p = list.GetArrayElementAtIndex(i);
                p.FindPropertyRelative("name").stringValue = Presets[i].name;
                p.FindPropertyRelative("yaw").floatValue = Presets[i].yaw;
                p.FindPropertyRelative("elevation").floatValue = Presets[i].elevation;
                p.FindPropertyRelative("intensity").floatValue = Presets[i].intensity;
                p.FindPropertyRelative("color").colorValue = Presets[i].color;
                p.FindPropertyRelative("ambientScale").floatValue = Presets[i].ambient;
            }
        }

        static void WriteBookmarks(SerializedProperty list)
        {
            list.arraySize = Bookmarks.Length;
            for (int i = 0; i < Bookmarks.Length; i++)
            {
                var b = list.GetArrayElementAtIndex(i);
                b.FindPropertyRelative("name").stringValue = Bookmarks[i].name;
                b.FindPropertyRelative("anchor").objectReferenceValue = null;
                b.FindPropertyRelative("position").vector3Value = Bookmarks[i].position;
                b.FindPropertyRelative("lookAt").vector3Value = Bookmarks[i].lookAt;
                b.FindPropertyRelative("fieldOfView").floatValue = Bookmarks[i].fov;
                b.FindPropertyRelative("reveal").objectReferenceValue = null;
                b.FindPropertyRelative("aperture").floatValue = Bookmarks[i].aperture;
                b.FindPropertyRelative("focalLength").floatValue = Bookmarks[i].focal;
                b.FindPropertyRelative("focusPoint").vector3Value = Bookmarks[i].focus;
            }
        }

        static string EnsureVolumes(GameObject root)
        {
            var report = new StringBuilder("[volumes]");
            foreach (var (name, path, priority) in new[]
            {
                ("Global Volume", EnsureGradeProfile(), 0f),
                ("Character Outline Volume", HybridRenderSetup.OutlineProfilePath, 10f),
                (HybridRenderSetup.Stage2VolumeName, HybridRenderSetup.Stage2ProfilePath, 10f),
                ("Wuling Atmosphere", EnsureAtmosphereProfile(), 20f),
            })
            {
                var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
                var volume = root.GetComponentsInChildren<Volume>(true).FirstOrDefault(v => v.gameObject.name == name);
                if (volume == null)
                {
                    var go = new GameObject(name);
                    go.transform.SetParent(root.transform, false);
                    volume = go.AddComponent<Volume>();
                }
                volume.isGlobal = true;
                volume.priority = priority;
                volume.sharedProfile = profile;
                report.Append(' ').Append(name);
            }
            return report.ToString();
        }

        public const string GradeProfilePath = Folder + "/Wuling_FinalImage.asset";
        public const string LensProfilePath = Folder + "/Wuling_Lens.asset";
        const string LensVolumeName = "Lens (rig-driven depth of field)";

        /// <summary>
        /// URP Bokeh depth of field for the portfolio framings (built-in URP effect, configured here).
        /// The profile is owned by ShowcaseRig: bookmarks with an aperture focus it on the eyes and set the
        /// volume weight to 1, the comparison bookmarks set it to 0. Seven rounded blades, like a fast
        /// portrait lens; the official Endfield close-ups have the same soft, round background.
        /// </summary>
        static string EnsureLens(GameObject root)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(LensProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, LensProfilePath);
            }
            var dof = HybridRenderSetup.EnsureProfileComponent<DepthOfField>(profile);
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(1f);
            dof.aperture.Override(2.8f);
            dof.focalLength.Override(85f);
            dof.bladeCount.Override(7);
            dof.bladeCurvature.Override(1f);
            dof.bladeRotation.Override(0f);
            EditorUtility.SetDirty(dof);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var volume = root.GetComponentsInChildren<Volume>(true).FirstOrDefault(v => v.gameObject.name == LensVolumeName);
            if (volume == null)
            {
                var go = new GameObject(LensVolumeName);
                go.transform.SetParent(root.transform, false);
                volume = go.AddComponent<Volume>();
            }
            volume.isGlobal = true;
            volume.priority = 30f;
            volume.weight = 0f;
            volume.sharedProfile = profile;

            var rig = Object.FindFirstObjectByType<ShowcaseRig>();
            var so = new SerializedObject(rig);
            so.FindProperty("_lensVolume").objectReferenceValue = volume;
            so.ApplyModifiedPropertiesWithoutUndo();
            return $"[lens] {LensVolumeName} -> {LensProfilePath} (Bokeh, 7 blades), driven by the rig's bookmarks.";
        }

        /// <summary>
        /// The Wuling grade, separate from the Suntail HybridRP_FinalImage so the main scene's
        /// accepted look is untouched. Same chain (HDR -> tonemap -> grading in HDR LUT), still no
        /// Bloom. The direction is Endfield's daylight: lifted, slightly cool shadows, warm but not
        /// orange highlights, restrained saturation, a light vignette to hold the frame centre.
        /// </summary>
        static string EnsureGradeProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(GradeProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, GradeProfilePath);
            }
            var tonemap = HybridRenderSetup.EnsureProfileComponent<Tonemapping>(profile);
            // Neutral, not ACES: ACES pushed the white cloth and skin into a hue-shifted plateau and
            // oranged the timber; Neutral keeps whites clean and reads closer to the reference grade.
            tonemap.mode.Override(TonemappingMode.Neutral);

            var color = HybridRenderSetup.EnsureProfileComponent<ColorAdjustments>(profile);
            color.postExposure.Override(GradePostExposure);
            color.contrast.Override(8f);
            color.saturation.Override(-4f);

            var white = HybridRenderSetup.EnsureProfileComponent<WhiteBalance>(profile);
            white.temperature.Override(-4f);
            white.tint.Override(2f);

            var smh = HybridRenderSetup.EnsureProfileComponent<ShadowsMidtonesHighlights>(profile);
            smh.shadows.Override(new Vector4(0.96f, 0.99f, 1.06f, 0.04f));
            smh.midtones.Override(new Vector4(1.0f, 1.0f, 1.0f, 0f));
            smh.highlights.Override(new Vector4(1.03f, 1.0f, 0.96f, 0f));

            var vignette = HybridRenderSetup.EnsureProfileComponent<Vignette>(profile);
            vignette.intensity.Override(0.18f);
            vignette.smoothness.Override(0.45f);
            vignette.color.Override(new Color(0.06f, 0.05f, 0.07f));

            foreach (var c in profile.components)
                EditorUtility.SetDirty(c);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return GradeProfilePath;
        }

        public const string AtmosphereProfilePath = Folder + "/Wuling_Atmosphere.asset";

        /// <summary>
        /// Overrides only the fog parameters of the shared Stage 2 profile (GTAO, contact shadows and
        /// SSR settings come from it unchanged). The courtyard is 40-80 m deep instead of a 300 m
        /// village, so the aerial perspective that separates the eaves layers needs ~3x the density,
        /// a lower base and a warm sunlit tint; forward scattering gives the sun-side haze.
        /// </summary>
        static string EnsureAtmosphereProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AtmosphereProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, AtmosphereProfilePath);
            }
            var fog = HybridRenderSetup.EnsureProfileComponent<HybridVolumetricFog>(profile);
            fog.enable.Override(true);
            fog.color.Override(new Color(0.80f, 0.80f, 0.82f));
            fog.density.Override(0.014f);
            fog.baseHeight.Override(11.5f);
            fog.heightFalloff.Override(18f);
            fog.maxDistance.Override(250f);
            fog.sampleCount.Override(32);
            fog.anisotropy.Override(0.6f);
            fog.lightScattering.Override(1.4f);
            fog.ambientScattering.Override(0.12f);
            EditorUtility.SetDirty(fog);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return AtmosphereProfilePath;
        }

        static string ConfigureSkyAndSun()
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(sky, SkyPath);
            }
            // Endfield's daylight reads as a clean, slightly desaturated blue sky over warm sun:
            // thin atmosphere (less orange horizon), small soft sun disc.
            sky.SetFloat("_SunDisk", 2f);
            sky.SetFloat("_SunSize", 0.035f);
            sky.SetFloat("_SunSizeConvergence", 6f);
            sky.SetFloat("_AtmosphereThickness", 1.0f);
            sky.SetColor("_SkyTint", new Color(0.42f, 0.52f, 0.62f));
            sky.SetColor("_GroundColor", new Color(0.38f, 0.36f, 0.33f));
            sky.SetFloat("_Exposure", 1.3f);
            EditorUtility.SetDirty(sky);

            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            // The courtyard is enclosed by deep eaves: with the sky at 1.0 the APV sky term (sky
            // occlusion is on) left shaded walls and stairs nearly black at the sun level the
            // characters need. 2.6 lifts the sky fill, which APV still occludes per probe.
            RenderSettings.ambientIntensity = 2.6f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;

            var sun = FindSun();
            RenderSettings.sun = sun;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;
            // Mixed + Baked Indirect: the sun's bounce goes into APV, its direct light and shadow
            // stay realtime (CSM + our contact shadows).
            sun.lightmapBakeType = LightmapBakeType.Mixed;
            DynamicGI.UpdateEnvironment();

            // The demo's lanterns are authored for a night look (20-180 intensity), which in a
            // daylight frame floods the courtyard orange. Set absolute daylight values so the step
            // stays idempotent: they read as warm accents in shade and feed the probe bake.
            int lamps = 0;
            var lights = FindRoot("Lights");
            if (lights != null)
            {
                foreach (var l in lights.GetComponentsInChildren<Light>(true))
                {
                    l.intensity = l.type == LightType.Spot ? 4f : 1.6f;
                    l.lightmapBakeType = LightmapBakeType.Mixed;
                    lamps++;
                }
            }
            return $"[sky/sun] procedural sky {SkyPath}, sun {sun.name} Mixed/soft shadows; {lamps} lanterns set to daylight accents.";
        }

        static string ConfigureStaticsForApv()
        {
            // Static flags stay (APV bakes against static contributors); GI is received from probes.
            int changed = 0;
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r.GetComponentInParent<HybridCharacterDriver>() != null)
                    continue;
                if (r.receiveGI != ReceiveGI.LightProbes || r.lightProbeUsage != LightProbeUsage.BlendProbes)
                {
                    r.receiveGI = ReceiveGI.LightProbes;
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                    changed++;
                }
            }
            return $"[statics] {changed} renderer(s) switched to receive GI from APV.";
        }

        // Vendor values are tuned for a dark, metallic HDRP look (metallic intensity up to 2.25 on
        // stone and timber). A metal has no diffuse response, so probe GI cannot light it and the
        // courtyard reads as bronze in shadow. The Endfield look is matte painted surfaces with soft
        // bounce, so stone/wood go dielectric and rougher; the gilded statues and lanterns keep
        // their metal. Values are absolute, so the step is idempotent. Columns: metallic
        // intensity, smoothness intensity, AO intensity, saturation, albedo intensity.
        static readonly (string material, float metallic, float smoothness, float ao, float saturation, float albedo)[] MaterialRetune =
        {
            ("M_BuidlingSet",      0.10f, 0.45f, 0.45f, 0.75f, 1.15f),
            ("M_WallSet",          0.08f, 0.40f, 0.45f, 0.90f, 1.15f),
            ("M_StairSets",        0.05f, 0.45f, 0.55f, 0.85f, 1.20f),
            ("M_FloortTile",       0.05f, 0.50f, 0.55f, 0.90f, 1.25f),
            ("M_MiniPavilionBase", 0.10f, 0.45f, 0.50f, 0.95f, 1.10f),
            ("M_MiniPavilionRoof", 0.15f, 0.45f, 0.45f, 0.85f, 1.10f),
            ("M_ExteriorSet",      0.20f, 0.50f, 0.45f, 0.90f, 1.05f),
            ("M_BazaarProps",      0.15f, 0.50f, 0.50f, 1.00f, 1.05f),
            ("M_CarriageSet",      0.10f, 0.40f, 0.50f, 0.80f, 1.05f),
            ("M_StoneChairs",      0.05f, 0.40f, 0.50f, 1.00f, 1.10f),
            ("M_Stories",          0.05f, 0.40f, 0.50f, 1.00f, 1.10f),
            ("M_ShopStand1",       0.10f, 0.40f, 0.50f, 1.00f, 1.05f),
            ("M_StallRoof",        0.00f, 0.30f, 0.50f, 1.00f, 1.05f),
            ("M_Treebark",         0.00f, 0.15f, 0.40f, 1.00f, 1.00f),
            ("M_Buddhas",          0.90f, 0.85f, 0.50f, 1.00f, 1.00f),
            ("M_LightSet",         0.60f, 0.70f, 0.50f, 1.00f, 1.00f),
        };

        const string MaterialFolder = "Assets/BefourStudios/AsianDynastyEnvironment/Art/Materials/";

        static string RetuneEnvironmentMaterials()
        {
            int changed = 0;
            foreach (var (name, metallic, smoothness, ao, saturation, albedo) in MaterialRetune)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + name + ".mat");
                if (m == null)
                    continue;
                m.SetFloat("_Metallic_Intensity", metallic);
                m.SetFloat("_Smoothness_Intensity", smoothness);
                m.SetFloat("_AO_Intensity", ao);
                m.SetFloat("_Saturation", saturation);
                m.SetFloat("_Base_Intensity", albedo);
                EditorUtility.SetDirty(m);
                changed++;
            }
            AssetDatabase.SaveAssets();
            return $"[materials] retuned {changed} environment material(s) toward matte painted surfaces.";
        }

        static string ConfigureApv(Scene scene, GameObject root)
        {
            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingSettingsPath);
            if (settings == null)
            {
                settings = new LightingSettings { name = "Wuling_Lighting" };
                AssetDatabase.CreateAsset(settings, LightingSettingsPath);
            }
            var lightmapperProperty = typeof(LightingSettings).GetProperty("lightmapper");
            lightmapperProperty?.SetValue(settings, Enum.Parse(lightmapperProperty.PropertyType, "ProgressiveGPU"));
            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.mixedBakeMode = MixedLightingMode.IndirectOnly;
            settings.directSampleCount = 32;
            settings.indirectSampleCount = 256;
            settings.environmentSampleCount = 128;
            settings.maxBounces = 3;
            settings.lightProbeSampleCountMultiplier = 4f;
            settings.ao = false;
            EditorUtility.SetDirty(settings);
            Lightmapping.SetLightingSettingsForScene(scene, settings);

            // Fine 1 m probes around the hero courtyard, 3 m elsewhere (bricks only near geometry).
            var hero = EnsureProbeVolume(root, "APV - Hero", ProbeVolume.Mode.Local);
            hero.transform.position = HeroApvCenter;
            hero.size = HeroApvSize;
            var coarse = EnsureProbeVolume(root, "APV - Scene", ProbeVolume.Mode.Scene);
            coarse.overridesSubdivLevels = true;
            coarse.lowestSubdivLevelOverride = 1;
            coarse.highestSubdivLevelOverride = 1;

            string sceneGuid = AssetDatabase.AssetPathToGUID(scene.path);
            var set = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(BakingSetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                set.name = "Wuling Baking Set";
                Invoke(set, "SetDefaults");
                typeof(ProbeVolumeBakingSet).GetField("singleSceneMode", AnyInstance)?.SetValue(set, true);
                set.TryAddScene(sceneGuid);
                AssetDatabase.CreateAsset(set, BakingSetPath);
            }
            set.simplificationLevels = 3;
            set.minDistanceBetweenProbes = 1f;
            set.skyOcclusion = true;
            set.skyOcclusionBakingSamples = 2048;
            set.skyOcclusionBakingBounces = 2;
            typeof(ProbeVolumeBakingSet).GetField("bakedSimplificationLevels", AnyInstance)?.SetValue(set, set.simplificationLevels);
            typeof(ProbeVolumeBakingSet).GetField("bakedMinDistanceBetweenProbes", AnyInstance)?.SetValue(set, set.minDistanceBetweenProbes);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            InvokeStatic(typeof(ProbeVolumeBakingSet), "SyncBakingSets");
            return $"[APV] hero volume {HeroApvSize} m (1 m), scene volume 3 m, sky occlusion, set {BakingSetPath}.";
        }

        /// <summary>Starts the lightmapper (probes + reflection probes; no lightmapped renderers) and then APV.</summary>
        public static string BakeProbes()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
                return "Open " + ScenePath + " first.";
            if (Lightmapping.isRunning)
                return "A bake is already running.";
            Lightmapping.BakeAsync();
            EditorApplication.update -= BakeApvAfterLightmapper;
            EditorApplication.update += BakeApvAfterLightmapper;
            return "Wuling bake started (lightmapper, then APV).";
        }

        static void BakeApvAfterLightmapper()
        {
            if (Lightmapping.isRunning)
                return;
            EditorApplication.update -= BakeApvAfterLightmapper;
            var type = Type.GetType("UnityEngine.Rendering.AdaptiveProbeVolumes, Unity.RenderPipelines.Core.Editor");
            var bake = type?.GetMethod("BakeAsync", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            bool started = bake != null && (bool)bake.Invoke(null, null);
            Debug.Log($"[Wuling] APV bake {(started ? "started" : "not started")}.");
        }

        // ------------------------------------------------------------------ helpers

        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static ProbeVolume EnsureProbeVolume(GameObject root, string name, ProbeVolume.Mode mode)
        {
            var volume = root.GetComponentsInChildren<ProbeVolume>(true).FirstOrDefault(v => v.gameObject.name == name);
            if (volume == null)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                volume = go.AddComponent<ProbeVolume>();
            }
            volume.mode = mode;
            EditorUtility.SetDirty(volume);
            return volume;
        }

        static Light FindSun()
        {
            var sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(l => l.type == LightType.Directional);
            if (sun == null)
                throw new InvalidOperationException("The scene has no directional light.");
            return sun;
        }

        static GameObject FindRoot(string name) =>
            SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == name);

        static void Invoke(object target, string method)
        {
            target.GetType().GetMethods(AnyInstance).FirstOrDefault(m => m.Name == method && m.GetParameters().Length == 0)?.Invoke(target, null);
        }

        static void InvokeStatic(Type type, string method)
        {
            type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == 0)?.Invoke(null, null);
        }
    }
}
