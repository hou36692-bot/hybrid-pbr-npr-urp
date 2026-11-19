using System;
using System.Collections.Generic;
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
    /// Reproducible setup of the environment lighting chain:
    /// baked GI on the architecture and terrain, APV for everything dynamic or small,
    /// baked lantern light, box-projected reflection probes, CSM/SSAO tuning.
    /// Every step is idempotent, so the menu entries can be re-run after a scene change.
    /// The bake itself is a separate step (Unity is single-threaded while baking).
    /// </summary>
    public static class EnvironmentLightingSetup
    {
        const string RpAssetPath = "Assets/Settings/PC_RPAsset.asset";
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const string LightingSettingsPath = "Assets/Settings/HybridRP_Lighting.lighting";

        const string ApvVolumeName = "APV - Showcase";
        const string ApvSceneVolumeName = "APV - Scene";
        const string ApvFolder = "Assets/Scenes/HybridRenderPipeline";
        const string ApvBakingSetPath = ApvFolder + "/HybridRP Baking Set.asset";
        static readonly Vector3 ApvCenter = new Vector3(145f, 12f, 155f);
        static readonly Vector3 ApvSize = new Vector3(90f, 20f, 90f);

        // Renderers at least this big (largest bounds axis) become lightmapped architecture/props;
        // everything smaller uses APV. 2 m keeps interior clutter out of the atlas.
        const float LightmapMinSize = 2f;

        // Realtime lanterns kept for the style test (the rest become baked).
        const int RealtimePointLights = 3;

        [MenuItem("Tools/HybridRP/Phase 2/Setup Lighting Chain")]
        public static void SetupFromMenu() => Debug.Log(Setup());

        [MenuItem("Tools/HybridRP/Phase 2/Prepare Lightmap UVs")]
        public static void PrepareLightmapUVsFromMenu() => Debug.Log(PrepareLightmapUVs());

        [MenuItem("Tools/HybridRP/Phase 2/Bake Lighting")]
        public static void BakeFromMenu() => Debug.Log(Bake());

        /// <summary>Applies every non-bake step. Returns a report for the console/CLI.</summary>
        public static string Setup()
        {
            var report = new StringBuilder();
            report.AppendLine(ConfigureLightingSettings());
            report.AppendLine(ConfigureRenderPipeline());
            report.AppendLine(ConfigureAmbient());
            report.AppendLine(ConfigureStatics());
            report.AppendLine(ConfigureLights());
            report.AppendLine(ConfigureReflectionProbes());
            report.AppendLine(ConfigureApv());
            report.AppendLine(ConfigureProbeVolumeOptions());
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            return report.ToString();
        }

        // ---------------------------------------------------------------- lighting settings

        static string ConfigureLightingSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingSettingsPath);
            if (settings == null)
            {
                settings = new LightingSettings { name = "HybridRP_Lighting" };
                AssetDatabase.CreateAsset(settings, LightingSettingsPath);
            }

            // Progressive GPU: the scene is ~300x300 m and has >400 lightmapped renderers.
            var lightmapperProperty = typeof(LightingSettings).GetProperty("lightmapper");
            if (lightmapperProperty != null)
                lightmapperProperty.SetValue(settings, Enum.Parse(lightmapperProperty.PropertyType, "ProgressiveGPU"));

            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.mixedBakeMode = MixedLightingMode.IndirectOnly; // Baked Indirect: direct stays realtime
            settings.lightmapResolution = 8f;                        // texels per metre
            settings.lightmapMaxSize = 2048;
            settings.lightmapPadding = 2;
            settings.lightmapCompression = LightmapCompression.NormalQuality;
            settings.sampling = LightingSettings.Sampling.Fixed;
            settings.directSampleCount = 16;
            settings.indirectSampleCount = 128;
            settings.environmentSampleCount = 64;
            settings.maxBounces = 3;
            settings.filteringMode = LightingSettings.FilterMode.Auto;
            settings.ao = false; // SSAO is the AO in this project; baked AO would confound its comparison
            settings.lightProbeSampleCountMultiplier = 4f;
            settings.prioritizeView = false;

            EditorUtility.SetDirty(settings);
            Lightmapping.lightingSettings = settings;
            AssetDatabase.SaveAssets();
            return $"[lighting settings] {LightingSettingsPath}: GPU progressive, Baked Indirect, 8 texels/m, 2048 atlas, 16/128/64 samples, 3 bounces.";
        }

        static string ConfigureRenderPipeline()
        {
            var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(RpAssetPath);
            if (rp == null)
                return "[pipeline] PC_RPAsset.asset not found.";

            var so = new SerializedObject(rp);
            so.FindProperty("m_LightProbeSystem").enumValueIndex = 1;   // Adaptive Probe Volumes
            so.FindProperty("m_ProbeVolumeSHBands").enumValueIndex = 1; // L2
            so.FindProperty("m_ShadowDistance").floatValue = 70f;
            so.FindProperty("m_MainLightShadowmapResolution").enumValueIndex = 4; // 4096
            so.FindProperty("m_SoftShadowQuality").enumValueIndex = 3;             // High
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rp);

            int ssaoChanges = ConfigureSsao();
            AssetDatabase.SaveAssets();
            return $"[pipeline] APV (L2), shadows 70 m / 4096 / high softness; SSAO tuned ({ssaoChanges} settings).";
        }

        static int ConfigureSsao()
        {
            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rendererData == null)
                return 0;

            var rendererSo = new SerializedObject(rendererData);
            var features = rendererSo.FindProperty("m_RendererFeatures");
            for (int i = 0; i < features.arraySize; i++)
            {
                var feature = features.GetArrayElementAtIndex(i).objectReferenceValue;
                if (feature == null || feature.GetType().Name != "ScreenSpaceAmbientOcclusion")
                    continue;

                var featureSo = new SerializedObject(feature);
                var s = featureSo.FindProperty("m_Settings");
                s.FindPropertyRelative("Source").enumValueIndex = 1;      // Depth Normals
                s.FindPropertyRelative("AfterOpaque").boolValue = false;
                s.FindPropertyRelative("AOMethod").enumValueIndex = 0;    // Blue Noise
                s.FindPropertyRelative("Radius").floatValue = 1.2f;
                s.FindPropertyRelative("Intensity").floatValue = 0.8f;
                s.FindPropertyRelative("DirectLightingStrength").floatValue = 0.25f;
                s.FindPropertyRelative("Falloff").floatValue = 100f;
                s.FindPropertyRelative("Samples").enumValueIndex = 0;     // High
                s.FindPropertyRelative("NormalSamples").enumValueIndex = 2; // High
                s.FindPropertyRelative("BlurQuality").enumValueIndex = 0; // High
                s.FindPropertyRelative("SampleCount").intValue = -1;
                s.FindPropertyRelative("Downsample").boolValue = false;
                featureSo.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(feature);
                return 11;
            }
            return 0;
        }

        static string ConfigureAmbient()
        {
            RenderSettings.ambientMode = AmbientMode.Skybox;
            return "[ambient] Skybox.";
        }

        // ---------------------------------------------------------------- static classification

        static string ConfigureStatics()
        {
            var candidates = CollectLightmapCandidates();
            var candidateSet = new HashSet<GameObject>();
            foreach (var renderer in candidates)
                candidateSet.Add(renderer.gameObject);

            var nature = GameObject.Find("Nature");
            var mountains = nature != null ? nature.transform.Find("Mountains") : null;
            var seenObjects = new HashSet<GameObject>();

            int cleared = 0, probed = 0;
            foreach (string rootName in new[] { "Buildings", "Environment", "Nature" })
            {
                var root = GameObject.Find(rootName);
                if (root == null)
                    continue;

                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var go = renderer.gameObject;
                    if (!seenObjects.Add(go))
                        continue;

                    bool isCandidate = candidateSet.Contains(go);
                    bool isMountain = mountains != null && go.transform.IsChildOf(mountains);
                    var flags = GameObjectUtility.GetStaticEditorFlags(go);
                    var newFlags = flags;

                    if (isCandidate)
                    {
                        newFlags |= StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic;
                    }
                    else
                    {
                        if ((newFlags & StaticEditorFlags.ContributeGI) != 0)
                        {
                            newFlags &= ~StaticEditorFlags.ContributeGI;
                            cleared++;
                        }
                        if (!isMountain)
                            newFlags |= StaticEditorFlags.ReflectionProbeStatic;
                    }
                    if (newFlags != flags)
                        GameObjectUtility.SetStaticEditorFlags(go, newFlags);

                    if (!isCandidate)
                    {
                        // Everything non-lightmapped samples the APV, including the mountains (S2-4):
                        // the coarse scene volume gives them sky/terrain bounce instead of a flat
                        // ambient probe; outside the volume they fall back to the sky.
                        renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                        renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                        probed++;
                    }
                }
            }

            var terrain = Object.FindFirstObjectByType<Terrain>();
            if (terrain != null)
            {
                var flags = GameObjectUtility.GetStaticEditorFlags(terrain.gameObject);
                var newFlags = flags | StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic;
                if (newFlags != flags)
                    GameObjectUtility.SetStaticEditorFlags(terrain.gameObject, newFlags);
            }

            int missingUv2 = 0;
            foreach (var renderer in candidates)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || !HasLightmapUVs(filter.sharedMesh))
                    missingUv2++;
            }

            return $"[statics] lightmapped {candidates.Count} renderers (LOD0, >= {LightmapMinSize} m) + terrain; " +
                   $"cleared ContributeGI on {cleared}, moved {probed} renderers to light probes/APV." +
                   (missingUv2 > 0 ? $" WARNING: {missingUv2} lightmapped renderers have no UV2 - run Prepare Lightmap UVs first." : "");
        }

        /// <summary>LOD0 (or ungrouped) renderers in the architecture roots that are large enough to lightmap.</summary>
        static List<MeshRenderer> CollectLightmapCandidates()
        {
            var result = new List<MeshRenderer>();
            var seen = new HashSet<Renderer>();
            foreach (string rootName in new[] { "Buildings", "Environment" })
            {
                var root = GameObject.Find(rootName);
                if (root == null)
                    continue;

                foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    var lods = group.GetLODs();
                    if (lods.Length == 0)
                        continue;
                    foreach (var renderer in lods[0].renderers)
                        AddCandidate(renderer, seen, result);
                }
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                    if (renderer.GetComponentInParent<LODGroup>(true) == null)
                        AddCandidate(renderer, seen, result);
            }
            return result;
        }

        static void AddCandidate(Renderer renderer, HashSet<Renderer> seen, List<MeshRenderer> result)
        {
            if (renderer is not MeshRenderer meshRenderer || !seen.Add(renderer))
                return;
            if (!meshRenderer.enabled || !meshRenderer.gameObject.activeInHierarchy)
                return;
            var size = meshRenderer.bounds.size;
            if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < LightmapMinSize)
                return;
            result.Add(meshRenderer);
        }

        // ---------------------------------------------------------------- lightmap UVs

        /// <summary>
        /// The pack's meshes were imported without lightmap UVs. Mesh assets are unwrapped in place;
        /// FBX importers get Generate Lightmap UVs and are reimported. Safe to re-run (already has UV2 = skip).
        /// </summary>
        public static string PrepareLightmapUVs()
        {
            var meshes = new HashSet<Mesh>();
            foreach (var renderer in CollectLightmapCandidates())
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                    meshes.Add(filter.sharedMesh);
            }

            int unwrapped = 0, importerSet = 0;
            var skipped = new List<string>();
            foreach (var mesh in meshes)
            {
                if (HasLightmapUVs(mesh))
                    continue;

                string path = AssetDatabase.GetAssetPath(mesh);
                if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    if (!mesh.isReadable)
                    {
                        skipped.Add(path + " (not readable)");
                        continue;
                    }
                    Unwrapping.GenerateSecondaryUVSet(mesh);
                    EditorUtility.SetDirty(mesh);
                    unwrapped++;
                }
                else
                {
                    if (AssetImporter.GetAtPath(path) is ModelImporter importer)
                    {
                        if (!importer.generateSecondaryUV)
                        {
                            importer.generateSecondaryUV = true;
                            importer.SaveAndReimport();
                            importerSet++;
                        }
                    }
                    else
                    {
                        skipped.Add(path + " (not a model)");
                    }
                }
            }

            AssetDatabase.SaveAssets();
            var report = new StringBuilder();
            report.AppendLine($"[lightmap UVs] {meshes.Count} candidate meshes: unwrapped {unwrapped} mesh assets, " +
                              $"enabled Generate Lightmap UVs on {importerSet} models.");
            foreach (string note in skipped)
                report.AppendLine("  skipped: " + note);
            return report.ToString();
        }

        static bool HasLightmapUVs(Mesh mesh) =>
            mesh != null && mesh.uv2 != null && mesh.uv2.Length == mesh.vertexCount && mesh.vertexCount > 0;

        // ---------------------------------------------------------------- lights

        static string ConfigureLights()
        {
            var character = GameObject.Find("ZhuangFangyi");
            Vector3 center = character != null ? character.transform.position : Vector3.zero;

            var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var points = new List<Light>();
            int mixed = 0;
            foreach (var light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    light.lightmapBakeType = LightmapBakeType.Mixed;
                    light.shadows = LightShadows.Soft;
                    mixed++;
                }
                else if (light.type == LightType.Point)
                {
                    points.Add(light);
                }
            }

            points.Sort((a, b) => Vector3.Distance(a.transform.position, center)
                .CompareTo(Vector3.Distance(b.transform.position, center)));

            int realtime = 0, baked = 0;
            for (int i = 0; i < points.Count; i++)
            {
                bool keepRealtime = i < RealtimePointLights;
                points[i].lightmapBakeType = keepRealtime ? LightmapBakeType.Realtime : LightmapBakeType.Baked;
                if (keepRealtime)
                {
                    points[i].shadows = LightShadows.None; // the lanterns never had shadows; keep them cheap
                    realtime++;
                }
                else
                {
                    baked++;
                }
            }

            return $"[lights] directional {mixed} Mixed (Baked Indirect); {baked} point lights Baked, " +
                   $"{realtime} nearest kept Realtime for the style test.";
        }

        // ---------------------------------------------------------------- reflection probes

        static string ConfigureReflectionProbes()
        {
            int removed = 0;
            foreach (var probe in Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
            {
                // The demo's single 300 m realtime probe re-renders the whole scene every frame.
                if (probe.mode == ReflectionProbeMode.Realtime || probe.name == "Reflection Probe")
                {
                    Object.DestroyImmediate(probe.gameObject);
                    removed++;
                }
            }

            var lighting = GameObject.Find("Lighting");
            Transform parent = lighting != null ? lighting.transform : null;
            CreateProbe("Reflection Probe - Courtyard", parent, new Vector3(144f, 12f, 158f), new Vector3(36f, 18f, 36f));
            CreateProbe("Reflection Probe - Pond", parent, new Vector3(110f, 12f, 158f), new Vector3(44f, 14f, 44f));
            CreateProbe("Reflection Probe - Street", parent, new Vector3(140f, 12f, 120f), new Vector3(32f, 16f, 32f));
            return $"[reflection probes] removed {removed} realtime probe(s); baked box-projected probes: Courtyard, Pond, Street.";
        }

        static void CreateProbe(string name, Transform parent, Vector3 position, Vector3 size)
        {
            var existing = GameObject.Find(name);
            var go = existing != null ? existing : new GameObject(name);
            if (go.transform.parent == null && parent != null)
                go.transform.SetParent(parent, false);
            go.transform.position = position;

            var probe = go.GetComponent<ReflectionProbe>();
            if (probe == null)
                probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked;
            probe.size = size;
            probe.boxProjection = true;
            probe.resolution = 256;
            probe.nearClipPlane = 0.1f;
            probe.farClipPlane = 150f;
            probe.importance = 1;
        }

        // ---------------------------------------------------------------- APV

        static string ConfigureApv()
        {
            var volumeObject = GameObject.Find(ApvVolumeName);
            if (volumeObject == null)
            {
                volumeObject = new GameObject(ApvVolumeName);
                var lighting = GameObject.Find("Lighting");
                if (lighting != null)
                    volumeObject.transform.SetParent(lighting.transform, false);
            }
            volumeObject.transform.position = ApvCenter;

            var volume = volumeObject.GetComponent<ProbeVolume>();
            if (volume == null)
                volume = volumeObject.AddComponent<ProbeVolume>();
            volume.mode = ProbeVolume.Mode.Local;
            volume.size = ApvSize;

            var scene = SceneManager.GetActiveScene();
            string sceneGuid = AssetDatabase.AssetPathToGUID(scene.path);
            var bakingSet = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(ApvBakingSetPath);
            if (bakingSet == null)
                bakingSet = CreateBakingSet(sceneGuid);
            else if (!ContainsScene(bakingSet, sceneGuid))
                bakingSet.TryAddScene(sceneGuid);

            bakingSet.simplificationLevels = 3;
            bakingSet.minDistanceBetweenProbes = 1f;
            // A freshly created set has baked values -1 until it round-trips through serialization;
            // mirror the display values so bounds/pathing are valid before the first bake.
            var setType = typeof(ProbeVolumeBakingSet);
            const BindingFlags anyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            setType.GetField("bakedSimplificationLevels", anyInstance)?.SetValue(bakingSet, bakingSet.simplificationLevels);
            setType.GetField("bakedMinDistanceBetweenProbes", anyInstance)?.SetValue(bakingSet, bakingSet.minDistanceBetweenProbes);
            EditorUtility.SetDirty(bakingSet);
            AssetDatabase.SaveAssets();

            // Refresh the editor's scene->set lookup, then let the APV system create its hidden
            // per-scene data object for this scene. A null path uses the display density (not the
            // baked one), which is what the pre-bake bounds need.
            InvokeStatic(setType, "SyncBakingSets");
            InvokeStatic(setType, "OnSceneSaving", scene, null);

            EnsureSceneApvVolume();

            return $"[APV] L2, local volume {ApvSize} m at {ApvCenter} (1 m probes) + scene volume (9 m probes), " +
                   $"baking set {ApvBakingSetPath} (simplification 3).";
        }

        /// <summary>
        /// The coarse second coverage level (S2-4): a scene-wide probe volume at subdivision level 2
        /// (9 m probes) under the 1 m showcase volume. Together they mirror the near/mid/far light
        /// volumes of the reference: fine probes where the character walks, coarse probes for the
        /// rest of the village and the mountains. Bricks exist only around geometry, so the coarse
        /// volume is far cheaper than its bounding box suggests.
        /// </summary>
        static void EnsureSceneApvVolume()
        {
            var volumeObject = GameObject.Find(ApvSceneVolumeName);
            if (volumeObject == null)
            {
                volumeObject = new GameObject(ApvSceneVolumeName);
                var lighting = GameObject.Find("Lighting");
                if (lighting != null)
                    volumeObject.transform.SetParent(lighting.transform, false);
            }
            var volume = volumeObject.GetComponent<ProbeVolume>();
            if (volume == null)
                volume = volumeObject.AddComponent<ProbeVolume>();
            volume.mode = ProbeVolume.Mode.Scene;
            volume.overridesSubdivLevels = true;
            // 1 m at level 0, 3 m at level 1, 9 m at level 2.
            volume.lowestSubdivLevelOverride = 2;
            volume.highestSubdivLevelOverride = 2;
            EditorUtility.SetDirty(volume);
        }

        /// <summary>Bakes APV only (the lightmaps and reflection probes are unchanged).</summary>
        public static string BakeProbes()
        {
            var type = Type.GetType("UnityEngine.Rendering.AdaptiveProbeVolumes, Unity.RenderPipelines.Core.Editor");
            var bake = type?.GetMethod("BakeAsync", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (bake == null)
                return "AdaptiveProbeVolumes.BakeAsync not found; APV was not baked.";
            bool started = (bool)bake.Invoke(null, null);
            return $"[APV] bake {(started ? "started" : "not started (nothing to bake or already running)")}.";
        }

        /// <summary>
        /// APV sampling options live in the default volume profile. Leak reduction moves from
        /// Performance (one sample, UV warping only) to Quality (up to three samples weighted by
        /// normal agreement), which is the S2-4 leak-control measure that applies everywhere
        /// without hand-placed adjustment volumes.
        /// </summary>
        static string ConfigureProbeVolumeOptions()
        {
            const string path = "Assets/Settings/DefaultVolumeProfile.asset";
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null || !profile.TryGet<ProbeVolumesOptions>(out var options))
                return $"[APV] {path} or ProbeVolumesOptions missing; leak reduction unchanged.";
            var previous = options.leakReductionMode.value;
            options.leakReductionMode.value = APVLeakReductionMode.Quality;
            options.leakReductionMode.overrideState = true;
            options.normalBias.overrideState = true;
            options.samplingNoise.overrideState = true;
            if (previous != APVLeakReductionMode.Quality)
            {
                EditorUtility.SetDirty(options);
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
            }
            return $"[APV options] leak reduction {previous} -> {APVLeakReductionMode.Quality}, " +
                   "normal bias 0.33, sampling noise 0.1.";
        }

        static ProbeVolumeBakingSet CreateBakingSet(string sceneGuid)
        {
            if (!AssetDatabase.IsValidFolder(ApvFolder))
                AssetDatabase.CreateFolder("Assets/Scenes", "HybridRenderPipeline");

            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            set.name = "HybridRP Baking Set";
            InvokeInstance(set, "SetDefaults");
            var singleScene = typeof(ProbeVolumeBakingSet).GetField("singleSceneMode",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (singleScene != null)
                singleScene.SetValue(set, true);
            set.TryAddScene(sceneGuid);
            AssetDatabase.CreateAsset(set, ApvBakingSetPath);
            return set;
        }

        static bool ContainsScene(ProbeVolumeBakingSet set, string sceneGuid)
        {
            var field = typeof(ProbeVolumeBakingSet).GetField("m_SceneGUIDs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(set) is List<string> scenes)
                return scenes.Contains(sceneGuid);
            return false;
        }

        // ---------------------------------------------------------------- bake

        public static string Bake()
        {
            if (Lightmapping.isRunning)
                return "A lighting bake is already running.";

            // The lightmapper bakes lightmaps + baked reflection probes. APV has its own async bake
            // and only joins the lightmap bake when its system is already initialized, which is not
            // the case right after the probe volume is created, so schedule it explicitly.
            Lightmapping.BakeAsync();
            EditorApplication.update -= BakeApvAfterLightmaps;
            EditorApplication.update += BakeApvAfterLightmaps;
            return "Lighting bake started (lightmaps, reflection probes, then APV). Poll with: unity command lighting bake_status";
        }

        static void BakeApvAfterLightmaps()
        {
            if (Lightmapping.isRunning)
                return;
            EditorApplication.update -= BakeApvAfterLightmaps;

            var type = Type.GetType("UnityEngine.Rendering.AdaptiveProbeVolumes, Unity.RenderPipelines.Core.Editor");
            var bake = type?.GetMethod("BakeAsync", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (bake == null)
            {
                Debug.LogWarning("[lighting] AdaptiveProbeVolumes.BakeAsync not found; APV was not baked.");
                return;
            }
            bool started = (bool)bake.Invoke(null, null);
            Debug.Log($"[lighting] APV bake {(started ? "started" : "not started (nothing to bake or already running)")}.");
        }

        // ---------------------------------------------------------------- reflection helpers

        static void InvokeStatic(Type type, string method, params object[] args)
        {
            var candidates = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var candidate in candidates)
            {
                if (candidate.Name != method || candidate.GetParameters().Length != args.Length)
                    continue;
                candidate.Invoke(null, args);
                return;
            }
        }

        static void InvokeInstance(object target, string method, params object[] args)
        {
            var candidates = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var candidate in candidates)
            {
                if (candidate.Name != method || candidate.GetParameters().Length != args.Length)
                    continue;
                candidate.Invoke(target, args);
                return;
            }
        }
    }
}
