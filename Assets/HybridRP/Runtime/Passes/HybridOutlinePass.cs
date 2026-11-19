using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    /// <summary>
    /// Phase 4 character outline: an R8 visible-character mask, then a depth/normal edge
    /// composite before post-processing. Moved unchanged out of the old HybridOutlineFeature.
    /// </summary>
    internal sealed class HybridOutlinePass : ScriptableRenderPass
    {
        private static readonly ShaderTagId MaskTag = new ShaderTagId("HybridOutlineMask");
        private static readonly ProfilingSampler MaskSampler = new ProfilingSampler("HybridRP.OutlineMask");
        private static readonly ProfilingSampler CompositeSampler = new ProfilingSampler("HybridRP.OutlineComposite");
        private static readonly int OutlineWeightId = Shader.PropertyToID("_HybridOutlineWeight");
        private static readonly int MaskId = Shader.PropertyToID("_HybridOutlineMaskTexture");
        private static readonly int DepthId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int NormalsId = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int ParamsId = Shader.PropertyToID("_OutlineParams");
        private static readonly int ColorId = Shader.PropertyToID("_OutlineColor");
        private static readonly int TexelId = Shader.PropertyToID("_OutlineTexelSize");
        private static readonly int StrengthId = Shader.PropertyToID("_OutlineStrength");
        private static readonly int DebugId = Shader.PropertyToID("_OutlineDebug");
        private static readonly int SurfaceId = Shader.PropertyToID("_OutlineSurface");

        private Material _material;
        private Vector4 _parameters;
        private Vector4 _surface;
        private Color _color;
        private float _strength;
        private bool _debug;

        private sealed class MaskData
        {
            public RendererListHandle renderers;
        }

        private sealed class CompositeData
        {
            public Material material;
            public TextureHandle source, mask, depth, normals;
            public Vector4 parameters, texelSize, surface;
            public Color color;
            public float strength;
            public bool debug;
        }

        public HybridOutlinePass()
        {
            requiresIntermediateTexture = true;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        public void Setup(Material material, HybridOutline settings, bool debug)
        {
            _material = material;
            _parameters = new Vector4(settings.width.value, settings.depthThreshold.value,
                                      settings.normalThreshold.value, settings.normalStrength.value);
            _surface = new Vector4(settings.surfaceTint.value, settings.surfaceDarkness.value,
                                   settings.surfaceSaturation.value, 0f);
            _color = settings.color.value.linear;
            _strength = settings.intensity.value * Shader.GetGlobalFloat(OutlineWeightId);
            _debug = debug;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid() ||
                !resources.cameraNormalsTexture.IsValid())
                return;

            var source = resources.activeColorTexture;
            var descriptor = renderGraph.GetTextureDesc(source);
            descriptor.msaaSamples = MSAASamples.None;
            descriptor.bindTextureMS = false;
            descriptor.depthBufferBits = DepthBits.None;
            descriptor.name = "Hybrid Outline Color";
            descriptor.clearBuffer = false;
            var destination = renderGraph.CreateTexture(descriptor);

            var maskDescriptor = descriptor;
            maskDescriptor.name = "Hybrid Character Mask";
            maskDescriptor.colorFormat = GraphicsFormat.R8_UNorm;
            maskDescriptor.filterMode = FilterMode.Point;
            maskDescriptor.clearBuffer = true;
            maskDescriptor.clearColor = Color.clear;
            var mask = renderGraph.CreateTexture(maskDescriptor);

            var renderingData = frameData.Get<UniversalRenderingData>();
            var lightData = frameData.Get<UniversalLightData>();
            var drawing = RenderingUtils.CreateDrawingSettings(MaskTag, renderingData, cameraData,
                                                               lightData, cameraData.defaultOpaqueSortFlags);
            drawing.perObjectData = PerObjectData.None;
            var filtering = new FilteringSettings(RenderQueueRange.opaque, -1, HybridRenderFeature.CharacterRenderingLayer);
            var renderers = renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, drawing, filtering));

            using (var builder = renderGraph.AddRasterRenderPass<MaskData>("Hybrid Outline Mask", out var data, MaskSampler))
            {
                data.renderers = renderers;
                builder.UseRendererList(renderers);
                // Alpha-clipped original materials sample the resolved camera depth. No MSAA
                // depth attachment is needed, and hidden character fragments cannot mark the mask.
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(mask, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (MaskData pass, RasterGraphContext context) =>
                    context.cmd.DrawRendererList(pass.renderers));
            }

            using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("Hybrid Outline Composite", out var data, CompositeSampler))
            {
                data.material = _material;
                data.source = source;
                data.mask = mask;
                data.depth = resources.cameraDepthTexture;
                data.normals = resources.cameraNormalsTexture;
                data.parameters = _parameters;
                // 1080p line units -> UV radius. Render Scale must not alter apparent width.
                data.parameters.x /= 1080f;
                data.texelSize = new Vector4(1f / descriptor.width, 1f / descriptor.height, descriptor.width, descriptor.height);
                data.surface = _surface;
                data.color = _color;
                data.strength = _strength;
                data.debug = _debug;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(mask, AccessFlags.Read);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(data.normals, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                {
                    pass.material.SetTexture(MaskId, pass.mask);
                    pass.material.SetTexture(DepthId, pass.depth);
                    pass.material.SetTexture(NormalsId, pass.normals);
                    pass.material.SetVector(ParamsId, pass.parameters);
                    pass.material.SetVector(TexelId, pass.texelSize);
                    pass.material.SetVector(SurfaceId, pass.surface);
                    pass.material.SetColor(ColorId, pass.color);
                    pass.material.SetFloat(StrengthId, pass.strength);
                    pass.material.SetFloat(DebugId, pass.debug ? 1f : 0f);
                    Blitter.BlitTexture(context.cmd, pass.source, new Vector4(1f, 1f, 0f, 0f), pass.material, 0);
                });
            }
            resources.cameraColor = destination;
        }
    }
}
