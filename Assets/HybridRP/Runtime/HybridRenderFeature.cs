using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    /// <summary>
    /// The one HybridRP renderer feature (modelled on IllusionRP's single "Illusion Graphics"
    /// feature). It owns every custom pass and decides per camera which of them run. URP keeps
    /// its own stages; this only inserts work and publishes the inputs our materials and URP Lit
    /// read. Per-camera history is added with the first temporal pass.
    /// </summary>
    [DisallowMultipleRendererFeature("HybridRP")]
    public sealed class HybridRenderFeature : ScriptableRendererFeature
    {
        // Keep bit 0: rendering layers also filter the character's lighting.
        public const uint CharacterRenderingLayer = 1u << 1;

#if UNITY_EDITOR
        // URP strips these keyword variants unless a built-in feature that uses them is active;
        // our passes publish the same slots, so keep them for player builds.
        [UnityEditor.ShaderKeywordFilter.SelectIf(true, keywordNames: new[] { "_SCREEN_SPACE_OCCLUSION" })]
        private const bool k_RequiresScreenSpaceOcclusion = true;
        [UnityEditor.ShaderKeywordFilter.SelectIf(true, keywordNames: new[] { "_MAIN_LIGHT_SHADOWS_SCREEN" })]
        private const bool k_RequiresScreenSpaceShadows = true;
#endif

        [SerializeField] private HybridRenderResources _resources = new HybridRenderResources();

        private static readonly int DebugViewId = Shader.PropertyToID("_HybridDebugView");
        private static readonly int OutlineWeightId = Shader.PropertyToID("_HybridOutlineWeight");

        private Material _outlineMaterial;
        private HybridOutlinePass _outlinePass;
        private HybridGTAOPass _gtaoPass;
        private HybridContactShadowsPass _contactShadowsPass;
        private HybridScreenSpaceShadowRestorePass _restoreShadowKeywordsPass;
        private HybridVolumetricFogPass _volumetricFogPass;
        private HybridSSRPass _ssrPass;

        public override void Create()
        {
            ReleaseResources();
            _outlineMaterial = _resources.outlineShader != null ? CoreUtils.CreateEngineMaterial(_resources.outlineShader) : null;
            _outlinePass = new HybridOutlinePass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            _gtaoPass = new HybridGTAOPass(_resources.depthPyramidCompute, _resources.gtaoCompute,
                                           _resources.gtaoBlurUpsampleCompute);
            _contactShadowsPass = new HybridContactShadowsPass(_resources.contactShadowsCompute,
                                                               _resources.screenSpaceShadowsShader);
            _restoreShadowKeywordsPass = new HybridScreenSpaceShadowRestorePass();
            _volumetricFogPass = new HybridVolumetricFogPass(_resources.volumetricFogCompute,
                                                             _resources.fogCompositeShader);
            _ssrPass = new HybridSSRPass(_resources.ssrCompute, _resources.ssrMaskShader,
                                         _resources.ssrCompositeShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var eligibility = Classify(ref renderingData.cameraData);
            if (eligibility == CameraEligibility.Full)
            {
                EnqueueOutline(renderer);
                // These two replace the camera colour, which is only safe on a standalone base
                // camera that resolves the final target (not on camera-stack bases or scene view).
                EnqueueVolumetricFog(renderer);
                EnqueueSSR(renderer);
            }
            if (eligibility != CameraEligibility.None)
            {
                EnqueueGTAO(renderer);
                EnqueueContactShadows(renderer, ref renderingData);
            }
        }

        // Screen-space lighting only needs depth and normals; the outline swaps camera colour.
        void EnqueueGTAO(ScriptableRenderer renderer)
        {
            if (_gtaoPass == null || !_gtaoPass.IsValid)
                return;
            var settings = VolumeManager.instance.stack.GetComponent<HybridGTAO>();
            if (settings == null || !settings.IsActive())
                return;
            renderer.EnqueuePass(_gtaoPass);
        }

        void EnqueueContactShadows(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_contactShadowsPass == null || !_contactShadowsPass.IsValid)
                return;
            var settings = VolumeManager.instance.stack.GetComponent<HybridContactShadows>();
            if (settings == null || !settings.IsActive())
                return;
            if (!renderingData.shadowData.supportsMainLightShadows || renderingData.lightData.mainLightIndex < 0)
                return;
            renderer.EnqueuePass(_contactShadowsPass);
            renderer.EnqueuePass(_restoreShadowKeywordsPass);
        }

        void EnqueueVolumetricFog(ScriptableRenderer renderer)
        {
            if (_volumetricFogPass == null || !_volumetricFogPass.IsValid)
                return;
            var settings = VolumeManager.instance.stack.GetComponent<HybridVolumetricFog>();
            if (settings == null || !settings.IsActive())
                return;
            renderer.EnqueuePass(_volumetricFogPass);
        }

        void EnqueueSSR(ScriptableRenderer renderer)
        {
            if (_ssrPass == null || !_ssrPass.IsValid)
                return;
            var settings = VolumeManager.instance.stack.GetComponent<HybridSSR>();
            if (settings == null || !settings.IsActive())
                return;
            renderer.EnqueuePass(_ssrPass);
        }

        void EnqueueOutline(ScriptableRenderer renderer)
        {
            if (_outlineMaterial == null)
                return;
            var settings = VolumeManager.instance.stack.GetComponent<HybridOutline>();
            int debugView = Shader.GetGlobalInt(DebugViewId);
            bool debugMask = debugView == 11;
            if (settings == null || (!debugMask && (debugView != 0 || !settings.IsActive() ||
                Shader.GetGlobalFloat(OutlineWeightId) <= 0f)))
                return;

            _outlinePass.Setup(_outlineMaterial, settings, debugMask);
            renderer.EnqueuePass(_outlinePass);
        }

        internal enum CameraEligibility
        {
            None,
            // Scene View: screen-space lighting only. The outline swaps the transient camera
            // colour, which is only safe for a standalone base camera.
            ScreenSpaceOnly,
            Full,
        }

        internal static CameraEligibility Classify(ref CameraData cameraData)
        {
            if (UniversalRenderer.IsOffscreenDepthTexture(ref cameraData))
                return CameraEligibility.None;
            switch (cameraData.cameraType)
            {
                case CameraType.Game:
                    // Screen-space lighting works for any base camera, including a stack base that
                    // renders into an intermediate texture. Only the outline needs the final target.
                    return cameraData.renderType == CameraRenderType.Base
                        ? (cameraData.resolveFinalTarget ? CameraEligibility.Full : CameraEligibility.ScreenSpaceOnly)
                        : CameraEligibility.None;
                case CameraType.SceneView:
                    return CameraEligibility.ScreenSpaceOnly;
                default:
                    // Reflection-probe and preview cameras: captured radiance must not contain
                    // screen-space effects.
                    return CameraEligibility.None;
            }
        }

        void ReleaseResources()
        {
            CoreUtils.Destroy(_outlineMaterial);
            _outlineMaterial = null;
            _gtaoPass?.Dispose();
            _gtaoPass = null;
            _contactShadowsPass?.Dispose();
            _contactShadowsPass = null;
            _volumetricFogPass?.Dispose();
            _volumetricFogPass = null;
            _ssrPass?.Dispose();
            _ssrPass = null;
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseResources();
        }
    }

    /// <summary>Shaders and compute shaders the feature needs; assigned by the Editor setup.</summary>
    [Serializable]
    public sealed class HybridRenderResources
    {
        public Shader outlineShader;
        public ComputeShader depthPyramidCompute;
        public ComputeShader gtaoCompute;
        public ComputeShader gtaoBlurUpsampleCompute;
        public ComputeShader contactShadowsCompute;
        public Shader screenSpaceShadowsShader;
        public ComputeShader volumetricFogCompute;
        public Shader fogCompositeShader;
        public ComputeShader ssrCompute;
        public Shader ssrMaskShader;
        public Shader ssrCompositeShader;
    }
}
