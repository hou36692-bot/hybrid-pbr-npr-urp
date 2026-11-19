using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    /// <summary>
    /// Contact shadows (Stage 2, ported from IllusionRP/HDRP, see NOTICE.md): a
    /// full-resolution compute raymarch toward the main light, then a screen-space shadow resolve
    /// that multiplies the cascaded shadow-map visibility by the contact map and publishes it in
    /// URP's own screen-space shadow texture. Keywords are restored before transparents by
    /// <see cref="HybridScreenSpaceShadowRestorePass"/>.
    /// </summary>
    internal sealed class HybridContactShadowsPass : ScriptableRenderPass
    {
        static readonly ProfilingSampler ContactSampler = new ProfilingSampler("HybridRP.ContactShadows");
        static readonly ProfilingSampler ResolveSampler = new ProfilingSampler("HybridRP.ScreenSpaceShadows");

        static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int ContactMapId = Shader.PropertyToID("_ContactShadowMap");
        static readonly int ContactUavId = Shader.PropertyToID("_ContactShadowTextureUAV");
        static readonly int Params1Id = Shader.PropertyToID("_ContactShadowParamsParameters");
        static readonly int Params2Id = Shader.PropertyToID("_ContactShadowParamsParameters2");
        static readonly int Params3Id = Shader.PropertyToID("_ContactShadowParamsParameters3");
        static readonly int LightDirectionId = Shader.PropertyToID("_ContactShadowLightDirection");
        static readonly int ViewToClipId = Shader.PropertyToID("_ContactShadowViewToClip");
        static readonly int ScreenSpaceShadowmapId = Shader.PropertyToID("_ScreenSpaceShadowmapTexture");

        internal static readonly GlobalKeyword MainLightShadowsKeyword = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS");
        internal static readonly GlobalKeyword MainLightShadowCascadesKeyword = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_CASCADE");
        internal static readonly GlobalKeyword MainLightShadowScreenKeyword = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_SCREEN");

        readonly ComputeShader _compute;
        readonly Material _material;
        int _kernel = -1;
        readonly bool _supportsR8LoadStore;

        sealed class ContactData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle depth, contact;
            public int width, height;
            public Vector4 params1, params2, params3, lightDirection;
            public Matrix4x4 viewToClip;
        }

        sealed class ResolveData
        {
            public Material material;
            public TextureHandle shadow, contact, depth;
        }

        public HybridContactShadowsPass(ComputeShader compute, Shader resolveShader)
        {
            _compute = compute;
            _material = resolveShader != null ? CoreUtils.CreateEngineMaterial(resolveShader) : null;
            _supportsR8LoadStore = SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.LoadStore);
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public bool IsValid
        {
            get
            {
                if (_compute != null && _kernel < 0)
                {
                    try { _kernel = _compute.FindKernel("ContactShadowMap"); }
                    catch { _kernel = -1; }
                }
                return _compute != null && _material != null && _kernel >= 0;
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData = frameData.Get<UniversalLightData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var shadowData = frameData.Get<UniversalShadowData>();

            var settings = VolumeManager.instance.stack.GetComponent<HybridContactShadows>();
            if (settings == null || !settings.IsActive())
                return;
            if (!shadowData.supportsMainLightShadows || lightData.mainLightIndex < 0)
                return;
            if (!resources.cameraDepthTexture.IsValid() || !resources.mainShadowsTexture.IsValid())
                return;
            if (cameraData.camera == null || cameraData.camera.orthographic)
                return;

            int index = lightData.mainLightIndex;
            if (index >= renderingData.cullResults.visibleLights.Length)
                return;
            VisibleLight visibleLight = renderingData.cullResults.visibleLights[index];
            if (visibleLight.lightType != LightType.Directional)
                return;
            // Without a shadow-casting main light URP did not render a cascade this frame; the
            // resolve would sample a stale atlas and the restore pass would set cascade keywords
            // that have no data. Leave the normal path alone.
            if (visibleLight.light != null && visibleLight.light.shadows == LightShadows.None)
                return;
            Vector3 toLight = visibleLight.light != null
                ? -visibleLight.light.transform.forward
                : -(Vector3)visibleLight.localToWorldMatrix.GetColumn(2);

            var descriptor = cameraData.cameraTargetDescriptor;
            int width = descriptor.width;
            int height = descriptor.height;

            var contactFormat = _supportsR8LoadStore ? GraphicsFormat.R8_UNorm : GraphicsFormat.R32_SFloat;
            var contact = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                colorFormat = contactFormat,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                name = "_ContactShadowMap",
            });

            float fadeEnd = Mathf.Max(0f, settings.maxDistance.value);
            float fadeRange = Mathf.Clamp(settings.fadeDistance.value, 0f, fadeEnd);
            float fadeIn = Mathf.Clamp(settings.fadeInDistance.value, 1e-6f, Mathf.Max(1e-6f, fadeEnd));
            var params1 = new Vector4(settings.length.value, settings.distanceScaleFactor.value,
                                      fadeEnd, 1f / Mathf.Max(1e-6f, fadeRange));
            var params2 = new Vector4(0f, Mathf.Min(settings.minDistance.value, fadeEnd),
                                      fadeIn, settings.rayBias.value * 0.01f);
            // A fixed frame index keeps captures deterministic; the interleaved gradient noise
            // still varies per pixel.
            var params3 = new Vector4(settings.sampleCount.value, settings.thicknessScale.value * 10f, 0f, 0f);

            using (var builder = renderGraph.AddComputePass<ContactData>("HybridRP.ContactShadows", out var data, ContactSampler))
            {
                data.cs = _compute;
                data.kernel = _kernel;
                data.depth = resources.cameraDepthTexture;
                data.contact = contact;
                data.width = width;
                data.height = height;
                data.params1 = params1;
                data.params2 = params2;
                data.params3 = params3;
                data.lightDirection = toLight;
                data.viewToClip = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, true);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(data.contact, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ContactData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeVectorParam(pass.cs, Params1Id, pass.params1);
                    cmd.SetComputeVectorParam(pass.cs, Params2Id, pass.params2);
                    cmd.SetComputeVectorParam(pass.cs, Params3Id, pass.params3);
                    cmd.SetComputeVectorParam(pass.cs, LightDirectionId, pass.lightDirection);
                    cmd.SetComputeMatrixParam(pass.cs, ViewToClipId, pass.viewToClip);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, CameraDepthId, pass.depth);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, ContactUavId, pass.contact);
                    cmd.DispatchCompute(pass.cs, pass.kernel, DivRoundUp(pass.width, 8), DivRoundUp(pass.height, 8), 1);
                });
            }

            // --- Resolve into URP's screen-space shadow texture ---------------------------------
            var shadowDescriptor = descriptor;
            shadowDescriptor.msaaSamples = 1;
            shadowDescriptor.depthStencilFormat = GraphicsFormat.None;
            shadowDescriptor.graphicsFormat = SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.Blend)
                ? GraphicsFormat.R8_UNorm
                : GraphicsFormat.B8G8R8A8_UNorm;
            var shadow = renderGraph.CreateTexture(new TextureDesc(shadowDescriptor)
            {
                name = "_ScreenSpaceShadowmapTexture",
                clearBuffer = true,
                clearColor = Color.white,
            });

            using (var builder = renderGraph.AddRasterRenderPass<ResolveData>("HybridRP.ScreenSpaceShadows", out var data, ResolveSampler))
            {
                data.material = _material;
                data.shadow = shadow;
                data.contact = contact;
                data.depth = resources.cameraDepthTexture;
                builder.UseTexture(data.contact, AccessFlags.Read);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.SetRenderAttachment(shadow, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetGlobalTextureAfterPass(shadow, ScreenSpaceShadowmapId);
                builder.SetRenderFunc(static (ResolveData pass, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    pass.material.SetTexture(ContactMapId, pass.contact);
                    Blitter.BlitTexture(cmd, pass.shadow, Vector2.one, pass.material, 0);
                    // Materials now sample the resolved screen-space shadow instead of the CSM.
                    cmd.SetKeyword(MainLightShadowsKeyword, false);
                    cmd.SetKeyword(MainLightShadowCascadesKeyword, false);
                    cmd.SetKeyword(MainLightShadowScreenKeyword, true);
                });
            }
        }

        public void Dispose()
        {
            CoreUtils.Destroy(_material);
        }

        static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }

    /// <summary>
    /// Restores the cascade shadow keywords before transparents: the screen-space shadow texture
    /// is only valid for opaque geometry (transparent shaders keep the CSM path).
    /// </summary>
    internal sealed class HybridScreenSpaceShadowRestorePass : ScriptableRenderPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("HybridRP.RestoreShadowKeywords");

        sealed class Data
        {
            public bool mainLightShadows;
            public int cascades;
        }

        public HybridScreenSpaceShadowRestorePass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var shadowData = frameData.Get<UniversalShadowData>();
            using (var builder = renderGraph.AddRasterRenderPass<Data>("HybridRP.RestoreShadowKeywords", out var data, Sampler))
            {
                data.mainLightShadows = shadowData.supportsMainLightShadows;
                data.cascades = shadowData.mainLightShadowCascadesCount;
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (Data pass, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetKeyword(HybridContactShadowsPass.MainLightShadowScreenKeyword, false);
                    cmd.SetKeyword(HybridContactShadowsPass.MainLightShadowsKeyword,
                                   pass.mainLightShadows && pass.cascades == 1);
                    cmd.SetKeyword(HybridContactShadowsPass.MainLightShadowCascadesKeyword,
                                   pass.mainLightShadows && pass.cascades > 1);
                });
            }
        }
    }
}
