using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    /// <summary>
    /// Screen-space reflections for renderers tagged with the reflector rendering layer (S2-6, our
    /// own implementation): a mask pass, a full-resolution view-space ray march against the
    /// camera depth, and an additive composite. Misses add nothing, so reflectors keep their
    /// reflection-probe response. Limited to the tagged layer on purpose: URP Lit has no SSR input,
    /// so applying this everywhere would reflect on every rough surface.
    /// </summary>
    internal sealed class HybridSSRPass : ScriptableRenderPass
    {
        public const uint ReflectorRenderingLayer = 1u << 2;

        static readonly ProfilingSampler MaskSampler = new ProfilingSampler("HybridRP.SSR.Mask");
        static readonly ProfilingSampler TraceSampler = new ProfilingSampler("HybridRP.SSR.Trace");
        static readonly ProfilingSampler CompositeSampler = new ProfilingSampler("HybridRP.SSR.Composite");

        static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int CameraNormalsId = Shader.PropertyToID("_CameraNormalsTexture");
        static readonly int ColorSourceId = Shader.PropertyToID("_SSRColorSource");
        static readonly int MaskId = Shader.PropertyToID("_SSRMask");
        static readonly int SSRTextureId = Shader.PropertyToID("_SSRTexture");
        static readonly int IntensityId = Shader.PropertyToID("_SSRIntensity");
        static readonly int Params0Id = Shader.PropertyToID("_SSRParams0");
        static readonly int Params1Id = Shader.PropertyToID("_SSRParams1");

        readonly ComputeShader _compute;
        readonly Material _maskMaterial;
        readonly Material _compositeMaterial;
        int _kernel = -1;

        sealed class MaskData
        {
            public RendererListHandle renderers;
            public TextureHandle mask;
            public Material material;
            public TextureHandle depth;
        }

        sealed class TraceData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle depth, normals, colorSource, mask, output;
            public int width, height;
            public Vector4 params0, params1;
        }

        sealed class CompositeData
        {
            public Material material;
            public TextureHandle source, ssr;
            public float intensity;
        }

        public HybridSSRPass(ComputeShader compute, Shader maskShader, Shader compositeShader)
        {
            _compute = compute;
            _maskMaterial = maskShader != null ? CoreUtils.CreateEngineMaterial(maskShader) : null;
            _compositeMaterial = compositeShader != null ? CoreUtils.CreateEngineMaterial(compositeShader) : null;
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        public bool IsValid
        {
            get
            {
                if (_compute != null && _kernel < 0)
                {
                    try { _kernel = _compute.FindKernel("SSR"); }
                    catch { _kernel = -1; }
                }
                return _compute != null && _maskMaterial != null && _compositeMaterial != null && _kernel >= 0;
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var lightData = frameData.Get<UniversalLightData>();
            if (!resources.cameraDepthTexture.IsValid() || !resources.cameraNormalsTexture.IsValid() ||
                !resources.cameraColor.IsValid() || resources.isActiveTargetBackBuffer)
                return;

            var settings = VolumeManager.instance.stack.GetComponent<HybridSSR>();
            if (settings == null || !settings.IsActive())
                return;

            var descriptor = cameraData.cameraTargetDescriptor;
            int width = descriptor.width;
            int height = descriptor.height;

            var mask = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                colorFormat = GraphicsFormat.R8_UNorm,
                filterMode = FilterMode.Point,
                name = "_SSRMask",
                clearBuffer = true,
                clearColor = Color.clear,
            });

            var drawing = RenderingUtils.CreateDrawingSettings(new ShaderTagId("UniversalForward"),
                renderingData, cameraData, lightData, cameraData.defaultOpaqueSortFlags);
            drawing.overrideMaterial = _maskMaterial;
            drawing.overrideMaterialPassIndex = 0;
            drawing.perObjectData = PerObjectData.None;
            // Opaque only: a transparent reflector has no depth in _CameraDepthTexture, so the
            // depth test below and the SSR march cannot reason about it.
            var filtering = new FilteringSettings(RenderQueueRange.opaque, -1, ReflectorRenderingLayer);
            var renderers = renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, drawing, filtering));

            using (var builder = renderGraph.AddRasterRenderPass<MaskData>("HybridRP.SSR.Mask", out var data, MaskSampler))
            {
                data.renderers = renderers;
                data.mask = mask;
                data.material = _maskMaterial;
                data.depth = resources.cameraDepthTexture;
                builder.UseRendererList(renderers);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.SetRenderAttachment(mask, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (MaskData pass, RasterGraphContext context) =>
                {
                    pass.material.SetTexture(CameraDepthId, pass.depth);
                    context.cmd.DrawRendererList(pass.renderers);
                });
            }

            var ssr = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                name = "_SSRTexture",
            });

            using (var builder = renderGraph.AddComputePass<TraceData>("HybridRP.SSR.Trace", out var data, TraceSampler))
            {
                data.cs = _compute;
                data.kernel = _kernel;
                data.depth = resources.cameraDepthTexture;
                data.normals = resources.cameraNormalsTexture;
                data.colorSource = resources.cameraColor;
                data.mask = mask;
                data.output = ssr;
                data.width = width;
                data.height = height;
                data.params0 = new Vector4(settings.maxDistance.value, settings.thickness.value,
                                           settings.stepCount.value, settings.intensity.value);
                data.params1 = new Vector4(width, height, 0f, 0f);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(data.normals, AccessFlags.Read);
                builder.UseTexture(data.colorSource, AccessFlags.Read);
                builder.UseTexture(data.mask, AccessFlags.Read);
                builder.UseTexture(data.output, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (TraceData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeVectorParam(pass.cs, Params0Id, pass.params0);
                    cmd.SetComputeVectorParam(pass.cs, Params1Id, pass.params1);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, CameraDepthId, pass.depth);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, CameraNormalsId, pass.normals);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, ColorSourceId, pass.colorSource);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, MaskId, pass.mask);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, SSRTextureId, pass.output);
                    cmd.DispatchCompute(pass.cs, pass.kernel, DivRoundUp(pass.width, 8), DivRoundUp(pass.height, 8), 1);
                });
            }

            var source = resources.cameraColor;
            var destinationDescriptor = renderGraph.GetTextureDesc(source);
            destinationDescriptor.msaaSamples = MSAASamples.None;
            destinationDescriptor.bindTextureMS = false;
            destinationDescriptor.depthBufferBits = DepthBits.None;
            destinationDescriptor.clearBuffer = false;
            destinationDescriptor.name = "Hybrid SSR Color";
            var destination = renderGraph.CreateTexture(destinationDescriptor);

            using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("HybridRP.SSR.Composite", out var data, CompositeSampler))
            {
                data.material = _compositeMaterial;
                data.source = source;
                data.ssr = ssr;
                data.intensity = settings.intensity.value;
                builder.UseTexture(data.source, AccessFlags.Read);
                builder.UseTexture(data.ssr, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                {
                    pass.material.SetTexture(SSRTextureId, pass.ssr);
                    pass.material.SetFloat(IntensityId, pass.intensity);
                    Blitter.BlitTexture(context.cmd, pass.source, new Vector4(1f, 1f, 0f, 0f), pass.material, 0);
                });
            }
            resources.cameraColor = destination;
        }

        public void Dispose()
        {
            CoreUtils.Destroy(_maskMaterial);
            CoreUtils.Destroy(_compositeMaterial);
        }

        static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}
