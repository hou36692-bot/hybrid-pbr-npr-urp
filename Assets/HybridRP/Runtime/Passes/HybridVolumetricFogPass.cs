using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    /// <summary>
    /// Half-resolution raymarched height fog with main-light shadowing (S2-5), composited over
    /// the scene colour with a depth-aware upsample. Our own implementation; see
    /// HybridVolumetricFog.compute for the model and the differences from froxel fog.
    /// </summary>
    internal sealed class HybridVolumetricFogPass : ScriptableRenderPass
    {
        static readonly ProfilingSampler RaymarchSampler = new ProfilingSampler("HybridRP.VolumetricFog.Raymarch");
        static readonly ProfilingSampler CompositeSampler = new ProfilingSampler("HybridRP.VolumetricFog.Composite");

        static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int ScatterId = Shader.PropertyToID("_FogScatter");
        static readonly int FogDepthId = Shader.PropertyToID("_FogDepth");
        static readonly int ScatterSizeId = Shader.PropertyToID("_FogScatterSize");
        static readonly int Params0Id = Shader.PropertyToID("_FogParams0");
        static readonly int Params1Id = Shader.PropertyToID("_FogParams1");
        static readonly int FogColorId = Shader.PropertyToID("_FogColor");
        static readonly int AmbientColorId = Shader.PropertyToID("_FogAmbientColor");
        static readonly int LightDirectionId = Shader.PropertyToID("_FogLightDirection");
        static readonly int LightColorId = Shader.PropertyToID("_MainLightColor");
        static readonly int MainShadowId = Shader.PropertyToID("_MainLightShadowmapTexture");

        readonly ComputeShader _compute;
        readonly Material _composite;
        int _kernel = -1;

        sealed class RaymarchData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle depth, scatter, fogDepth;
            public TextureHandle mainShadows;
            public bool hasMainShadows;
            public int width, height;
            public Vector4 params0, params1, color, ambient, lightDirection, lightColor;
        }

        sealed class CompositeData
        {
            public Material material;
            public TextureHandle source, scatter, fogDepth, depth;
            public Vector4 scatterSize;
        }

        public HybridVolumetricFogPass(ComputeShader compute, Shader compositeShader)
        {
            _compute = compute;
            _composite = compositeShader != null ? CoreUtils.CreateEngineMaterial(compositeShader) : null;
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public bool IsValid
        {
            get
            {
                if (_compute != null && _kernel < 0)
                {
                    try { _kernel = _compute.FindKernel("Raymarch"); }
                    catch { _kernel = -1; }
                }
                return _compute != null && _kernel >= 0 && _composite != null;
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData = frameData.Get<UniversalLightData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            if (!resources.cameraDepthTexture.IsValid() || resources.isActiveTargetBackBuffer)
                return;

            var settings = VolumeManager.instance.stack.GetComponent<HybridVolumetricFog>();
            if (settings == null || !settings.IsActive())
                return;

            var descriptor = cameraData.cameraTargetDescriptor;
            int fullWidth = descriptor.width;
            int fullHeight = descriptor.height;
            int width = Mathf.Max(1, Mathf.CeilToInt(fullWidth * 0.5f));
            int height = Mathf.Max(1, Mathf.CeilToInt(fullHeight * 0.5f));

            TextureHandle scatter = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                name = "_FogScatter",
            });
            TextureHandle fogDepth = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                colorFormat = GraphicsFormat.R32_SFloat,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                name = "_FogDepth",
            });

            // No main light means no sun in-scatter; a white fallback would fog the scene brightly.
            Vector3 lightDirection = Vector3.up;
            Color lightColor = Color.black;
            int index = lightData.mainLightIndex;
            if (index >= 0 && index < renderingData.cullResults.visibleLights.Length)
            {
                VisibleLight visibleLight = renderingData.cullResults.visibleLights[index];
                if (visibleLight.light != null)
                {
                    lightDirection = -visibleLight.light.transform.forward;
                    // finalColor already carries the light colour (including temperature) and intensity.
                    lightColor = visibleLight.finalColor;
                }
            }

            var params0 = new Vector4(settings.density.value, settings.heightFalloff.value,
                                      settings.baseHeight.value, settings.maxDistance.value);
            var params1 = new Vector4(settings.anisotropy.value, settings.lightScattering.value,
                                      settings.ambientScattering.value, settings.sampleCount.value);
            var color = (Vector4)settings.color.value.linear;
            // In a linear project RenderSettings ambient colours are already linear; Color.linear
            // would gamma-convert them a second time and make the ambient in-scatter ~4x too dark.
            var ambient = (Vector4)RenderSettings.ambientSkyColor;

            using (var builder = renderGraph.AddComputePass<RaymarchData>("HybridRP.VolumetricFog.Raymarch", out var data, RaymarchSampler))
            {
                data.cs = _compute;
                data.kernel = _kernel;
                data.depth = resources.cameraDepthTexture;
                data.scatter = scatter;
                data.fogDepth = fogDepth;
                data.width = width;
                data.height = height;
                data.params0 = params0;
                data.params1 = params1;
                data.color = color;
                data.ambient = ambient;
                data.lightDirection = lightDirection;
                data.lightColor = lightColor;
                data.mainShadows = resources.mainShadowsTexture;
                data.hasMainShadows = resources.mainShadowsTexture.IsValid();
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(data.scatter, AccessFlags.Write);
                builder.UseTexture(data.fogDepth, AccessFlags.Write);
                if (data.hasMainShadows)
                    builder.UseTexture(data.mainShadows, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (RaymarchData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeVectorParam(pass.cs, Params0Id, pass.params0);
                    cmd.SetComputeVectorParam(pass.cs, Params1Id, pass.params1);
                    cmd.SetComputeVectorParam(pass.cs, FogColorId, pass.color);
                    cmd.SetComputeVectorParam(pass.cs, AmbientColorId, pass.ambient);
                    cmd.SetComputeVectorParam(pass.cs, LightDirectionId, pass.lightDirection);
                    cmd.SetComputeVectorParam(pass.cs, LightColorId, pass.lightColor);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, CameraDepthId, pass.depth);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, ScatterId, pass.scatter);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, FogDepthId, pass.fogDepth);
                    if (pass.hasMainShadows)
                        cmd.SetComputeTextureParam(pass.cs, pass.kernel, MainShadowId, pass.mainShadows);
                    cmd.DispatchCompute(pass.cs, pass.kernel, DivRoundUp(pass.width, 8), DivRoundUp(pass.height, 8), 1);
                });
            }

            var source = resources.activeColorTexture;
            var destinationDescriptor = renderGraph.GetTextureDesc(source);
            destinationDescriptor.msaaSamples = MSAASamples.None;
            destinationDescriptor.bindTextureMS = false;
            destinationDescriptor.depthBufferBits = DepthBits.None;
            destinationDescriptor.clearBuffer = false;
            destinationDescriptor.name = "Hybrid Fog Color";
            var destination = renderGraph.CreateTexture(destinationDescriptor);

            using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("HybridRP.VolumetricFog.Composite", out var data, CompositeSampler))
            {
                data.material = _composite;
                data.source = source;
                data.scatter = scatter;
                data.fogDepth = fogDepth;
                data.depth = resources.cameraDepthTexture;
                data.scatterSize = new Vector4(width, height, 1f / width, 1f / height);
                builder.UseTexture(data.source, AccessFlags.Read);
                builder.UseTexture(data.scatter, AccessFlags.Read);
                builder.UseTexture(data.fogDepth, AccessFlags.Read);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                {
                    pass.material.SetTexture(ScatterId, pass.scatter);
                    pass.material.SetTexture(FogDepthId, pass.fogDepth);
                    pass.material.SetTexture(CameraDepthId, pass.depth);
                    pass.material.SetVector(ScatterSizeId, pass.scatterSize);
                    Blitter.BlitTexture(context.cmd, pass.source, new Vector4(1f, 1f, 0f, 0f), pass.material, 0);
                });
            }
            resources.cameraColor = destination;
        }

        public void Dispose()
        {
            CoreUtils.Destroy(_composite);
        }

        static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}
