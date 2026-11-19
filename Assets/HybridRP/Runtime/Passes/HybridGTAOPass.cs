using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    /// <summary>
    /// GTAO (Stage 2, ported from IllusionRP/HDRP, see NOTICE.md). It builds a packed
    /// min-depth pyramid, traces half-resolution horizon-based AO against it, blurs and
    /// bilaterally upscales to a full-resolution visibility texture, then publishes it through
    /// URP's own slot (`_ScreenSpaceOcclusionTexture` + `_SCREEN_SPACE_OCCLUSION` +
    /// `_AmbientOcclusionParam`) so every URP material consumes it unchanged. URP's SSAO feature
    /// must be off while this runs; both write the same slot.
    /// </summary>
    internal sealed class HybridGTAOPass : ScriptableRenderPass
    {
        static readonly ProfilingSampler PyramidSampler = new ProfilingSampler("HybridRP.GTAO.DepthPyramid");
        static readonly ProfilingSampler TraceSampler = new ProfilingSampler("HybridRP.GTAO.Trace");
        static readonly ProfilingSampler UpsampleSampler = new ProfilingSampler("HybridRP.GTAO.Upsample");

        static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int CameraNormalsId = Shader.PropertyToID("_CameraNormalsTexture");
        static readonly int DepthMipChainId = Shader.PropertyToID("_DepthMipChain");
        static readonly int DepthPyramidId = Shader.PropertyToID("_DepthPyramid");
        static readonly int DepthPyramidSrcId = Shader.PropertyToID("_DepthPyramidSrc");
        static readonly int DepthPyramidDstId = Shader.PropertyToID("_DepthPyramidDst");
        static readonly int AOPackedId = Shader.PropertyToID("_AOPackedData");
        static readonly int OcclusionId = Shader.PropertyToID("_OcclusionTexture");
        static readonly int ScreenSpaceOcclusionId = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");
        static readonly int AmbientOcclusionParamId = Shader.PropertyToID("_AmbientOcclusionParam");
        static readonly int AOBufferSizeId = Shader.PropertyToID("_AOBufferSize");
        static readonly int AOParams0Id = Shader.PropertyToID("_AOParams0");
        static readonly int AOParams1Id = Shader.PropertyToID("_AOParams1");
        static readonly int AOParams2Id = Shader.PropertyToID("_AOParams2");
        static readonly int AOParams3Id = Shader.PropertyToID("_AOParams3");
        static readonly int AOParams4Id = Shader.PropertyToID("_AOParams4");
        static readonly int MipOffsetsId = Shader.PropertyToID("_FirstTwoDepthMipOffsets");
        static readonly int DepthToViewId = Shader.PropertyToID("_AODepthToViewParams");
        // URP's ShaderGlobalKeywords is internal in 17.0.4; create the same global keyword.
        static readonly GlobalKeyword ScreenSpaceOcclusionKeyword = GlobalKeyword.Create("_SCREEN_SPACE_OCCLUSION");

        const int MaxMipLevels = 16;

        /// <summary>
        /// Packed atlas layout: mip 0 is the camera depth, every further mip is packed into the
        /// same texture (below or beside the previous one) because GPUs cannot bind individual
        /// mips to compute UAVs and gather has no LOD. Ported from the HDRP packing IllusionRP
        /// uses, without checkerboard or dynamic resolution.
        /// </summary>
        sealed class MipChainInfo
        {
            public Vector2Int textureSize;
            public int mipLevelCount;
            public readonly Vector2Int[] sizes = new Vector2Int[MaxMipLevels];
            public readonly Vector2Int[] offsets = new Vector2Int[MaxMipLevels];
            public int cachedWidth = -1, cachedHeight = -1;

            public void Compute(int width, int height)
            {
                if (width == cachedWidth && height == cachedHeight)
                    return;
                cachedWidth = width;
                cachedHeight = height;

                sizes[0] = new Vector2Int(width, height);
                offsets[0] = Vector2Int.zero;
                int level = 0;
                Vector2Int size = sizes[0];
                Vector2Int atlas = new Vector2Int(width, height);
                do
                {
                    level++;
                    size = new Vector2Int(Math.Max(1, (size.x + 1) >> 1), Math.Max(1, (size.y + 1) >> 1));
                    sizes[level] = size;

                    Vector2Int previousSize = sizes[level - 1];
                    Vector2Int previousBegin = offsets[level - 1];
                    // First mip below the full-resolution one, then alternate down/right.
                    bool down = level == 1 || (level & 1) == 1;
                    Vector2Int begin = down
                        ? new Vector2Int(previousBegin.x, previousBegin.y + previousSize.y)
                        : new Vector2Int(previousBegin.x + previousSize.x, previousBegin.y);
                    offsets[level] = begin;

                    atlas.x = Math.Max(atlas.x, begin.x + size.x);
                    atlas.y = Math.Max(atlas.y, begin.y + size.y);
                } while (size.x > 1 || size.y > 1);

                mipLevelCount = level + 1;
                textureSize = atlas;
            }
        }

        sealed class PyramidData
        {
            public ComputeShader cs;
            public int copyKernel, downsampleKernel;
            public TextureHandle depth, pyramid;
            public MipChainInfo info;
            public int width, height;
        }

        sealed class TraceData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle pyramid, normals, packed;
            public int width, height;
            public Vector4 bufferSize, params0, params1, params2, params3, params4, mipOffsets, depthToView;
        }

        sealed class UpsampleData
        {
            public ComputeShader cs;
            public int kernel;
            public TextureHandle packed, depth, output;
            public int width, height;
            public Vector4 bufferSize, params0, params1, params2, params3, params4, mipOffsets, depthToView;
            public float directLightingStrength;
        }

        readonly ComputeShader _depthPyramidShader;
        readonly ComputeShader _traceShader;
        readonly ComputeShader _upsampleShader;
        // Resolved lazily: a compute shader that failed to compile has no kernels, and the
        // renderer feature must not throw (it also owns the outline).
        int _copyKernel = -1, _downsampleKernel = -1, _traceKernel = -1, _upsampleKernel = -1;
        readonly MipChainInfo _mipInfo = new MipChainInfo();
        readonly bool _supportsR8LoadStore;

        public HybridGTAOPass(ComputeShader depthPyramidShader, ComputeShader traceShader, ComputeShader upsampleShader)
        {
            _depthPyramidShader = depthPyramidShader;
            _traceShader = traceShader;
            _upsampleShader = upsampleShader;
            _supportsR8LoadStore = SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.LoadStore);
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        public bool IsValid
        {
            get
            {
                ResolveKernels();
                return _copyKernel >= 0 && _downsampleKernel >= 0 && _traceKernel >= 0 && _upsampleKernel >= 0;
            }
        }

        void ResolveKernels()
        {
            if (_depthPyramidShader != null)
            {
                if (_copyKernel < 0)
                    TryFindKernel(_depthPyramidShader, "CopyDepth", ref _copyKernel);
                if (_downsampleKernel < 0)
                    TryFindKernel(_depthPyramidShader, "Downsample", ref _downsampleKernel);
            }
            if (_traceShader != null && _traceKernel < 0)
                TryFindKernel(_traceShader, "GTAOMain", ref _traceKernel);
            if (_upsampleShader != null && _upsampleKernel < 0)
                TryFindKernel(_upsampleShader, "BlurUpsample", ref _upsampleKernel);
        }

        static void TryFindKernel(ComputeShader shader, string name, ref int kernel)
        {
            try { kernel = shader.FindKernel(name); }
            catch { kernel = -1; }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            if (!resources.cameraDepthTexture.IsValid() || !resources.cameraNormalsTexture.IsValid())
                return;
            var camera = cameraData.camera;
            if (camera == null || camera.orthographic)
                return;

            var settings = VolumeManager.instance.stack.GetComponent<HybridGTAO>();
            if (settings == null || !settings.IsActive())
                return;

            var descriptor = cameraData.cameraTargetDescriptor;
            int fullWidth = descriptor.width;
            int fullHeight = descriptor.height;
            int width = Mathf.CeilToInt(fullWidth * 0.5f);
            int height = Mathf.CeilToInt(fullHeight * 0.5f);
            if (width <= 0 || height <= 0)
                return;

            _mipInfo.Compute(fullWidth, fullHeight);
            // Transient atlas: a persistent RTHandle would be reallocated whenever the Game and
            // Scene views alternate sizes, and Render Graph tracks the transient's lifetime.
            var pyramid = renderGraph.CreateTexture(new TextureDesc(_mipInfo.textureSize.x, _mipInfo.textureSize.y)
            {
                colorFormat = GraphicsFormat.R32_SFloat,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                name = "HybridRP Depth Pyramid",
            });

            // --- Depth pyramid -----------------------------------------------------------------
            using (var builder = renderGraph.AddComputePass<PyramidData>("HybridRP.GTAO.DepthPyramid", out var data, PyramidSampler))
            {
                data.cs = _depthPyramidShader;
                data.copyKernel = _copyKernel;
                data.downsampleKernel = _downsampleKernel;
                data.depth = resources.cameraDepthTexture;
                data.pyramid = pyramid;
                data.info = _mipInfo;
                data.width = fullWidth;
                data.height = fullHeight;
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(pyramid, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PyramidData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeTextureParam(pass.cs, pass.copyKernel, CameraDepthId, pass.depth);
                    cmd.SetComputeTextureParam(pass.cs, pass.copyKernel, DepthMipChainId, pass.pyramid);
                    cmd.DispatchCompute(pass.cs, pass.copyKernel, DivRoundUp(pass.width, 8), DivRoundUp(pass.height, 8), 1);
                    // Each kernel has its own parameter table; the downsample kernel needs the
                    // atlas bound as well (only the copy kernel was set so far).
                    cmd.SetComputeTextureParam(pass.cs, pass.downsampleKernel, DepthMipChainId, pass.pyramid);

                    var info = pass.info;
                    for (int mip = 1; mip < info.mipLevelCount; mip++)
                    {
                        Vector2Int dstSize = info.sizes[mip];
                        Vector2Int dstOffset = info.offsets[mip];
                        Vector2Int srcSize = info.sizes[mip - 1];
                        Vector2Int srcOffset = info.offsets[mip - 1];
                        cmd.SetComputeVectorParam(pass.cs, DepthPyramidSrcId,
                            new Vector4(srcOffset.x, srcOffset.y, srcSize.x - 1, srcSize.y - 1));
                        cmd.SetComputeVectorParam(pass.cs, DepthPyramidDstId,
                            new Vector4(dstOffset.x, dstOffset.y, dstSize.x, dstSize.y));
                        cmd.DispatchCompute(pass.cs, pass.downsampleKernel,
                            DivRoundUp(dstSize.x, 8), DivRoundUp(dstSize.y, 8), 1);
                    }
                });
            }

            // --- Parameters ---------------------------------------------------------------------
            var parameters = BuildParameters(settings, camera, width, height);
            var packed = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                colorFormat = GraphicsFormat.R32_SFloat,
                enableRandomWrite = true,
                name = "HybridRP GTAO Packed",
            });
            var finalFormat = _supportsR8LoadStore ? GraphicsFormat.R8_UNorm : GraphicsFormat.R32_SFloat;
            var output = renderGraph.CreateTexture(new TextureDesc(fullWidth, fullHeight)
            {
                colorFormat = finalFormat,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                name = "_ScreenSpaceOcclusionTexture",
            });

            // --- Trace --------------------------------------------------------------------------
            using (var builder = renderGraph.AddComputePass<TraceData>("HybridRP.GTAO.Trace", out var data, TraceSampler))
            {
                data.cs = _traceShader;
                data.kernel = _traceKernel;
                data.pyramid = pyramid;
                data.normals = resources.cameraNormalsTexture;
                data.packed = packed;
                data.width = width;
                data.height = height;
                parameters.CopyTo(data);
                builder.UseTexture(data.pyramid, AccessFlags.Read);
                builder.UseTexture(data.normals, AccessFlags.Read);
                builder.UseTexture(data.packed, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (TraceData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    SetParameters(cmd, pass.cs, pass.kernel, pass);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, DepthPyramidId, pass.pyramid);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, CameraNormalsId, pass.normals);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, AOPackedId, pass.packed);
                    cmd.DispatchCompute(pass.cs, pass.kernel, DivRoundUp(pass.width, 8), DivRoundUp(pass.height, 8), 1);
                });
            }

            // --- Blur + bilateral upsample ------------------------------------------------------
            using (var builder = renderGraph.AddComputePass<UpsampleData>("HybridRP.GTAO.Upsample", out var data, UpsampleSampler))
            {
                data.cs = _upsampleShader;
                data.kernel = _upsampleKernel;
                data.packed = packed;
                data.depth = resources.cameraDepthTexture;
                data.output = output;
                data.width = width;
                data.height = height;
                parameters.CopyTo(data);
                data.directLightingStrength = settings.directLightingStrength.value;
                builder.UseTexture(data.packed, AccessFlags.Read);
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(data.output, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (UpsampleData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    SetParameters(cmd, pass.cs, pass.kernel, pass);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, AOPackedId, pass.packed);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, CameraDepthId, pass.depth);
                    cmd.SetComputeTextureParam(pass.cs, pass.kernel, OcclusionId, pass.output);
                    // One extra thread row/column: thread d writes full-res pixels 2d-1 and 2d,
                    // so the last even-width column needs d = width.
                    cmd.DispatchCompute(pass.cs, pass.kernel, DivRoundUp(pass.width + 1, 8),
                                        DivRoundUp(pass.height + 1, 8), 1);
                    cmd.SetGlobalVector(AmbientOcclusionParamId, new Vector4(1f, 0f, 0f, pass.directLightingStrength));
                    cmd.SetKeyword(ScreenSpaceOcclusionKeyword, true);
                });
                // Registered after the pass so Render Graph keeps it alive for the opaque and
                // transparent draws (UseAllGlobalTextures only covers registered globals).
                builder.SetGlobalTextureAfterPass(output, ScreenSpaceOcclusionId);
            }
        }

        /// <summary>URP SSAO clears the keyword in OnCameraCleanup as well; transparents get no AO keyword.</summary>
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.SetKeyword(ScreenSpaceOcclusionKeyword, false);
        }

        public void Dispose()
        {
        }

        // Shared between the trace and upsample kernels; both read the same constant layout.
        struct Parameters
        {
            public Vector4 bufferSize, params0, params1, params2, params3, params4, mipOffsets, depthToView;

            public void CopyTo(TraceData data)
            {
                data.bufferSize = bufferSize; data.params0 = params0; data.params1 = params1;
                data.params2 = params2; data.params3 = params3; data.params4 = params4;
                data.mipOffsets = mipOffsets; data.depthToView = depthToView;
            }

            public void CopyTo(UpsampleData data)
            {
                data.bufferSize = bufferSize; data.params0 = params0; data.params1 = params1;
                data.params2 = params2; data.params3 = params3; data.params4 = params4;
                data.mipOffsets = mipOffsets; data.depthToView = depthToView;
            }
        }

        Parameters BuildParameters(HybridGTAO settings, Camera camera, int width, int height)
        {
            float fovRad = camera.fieldOfView * Mathf.Deg2Rad;
            float invHalfTanFov = 1f / Mathf.Tan(fovRad * 0.5f);

            float radius = settings.radius.value;
            var p = new Parameters
            {
                bufferSize = new Vector4(width, height, 1f / width, 1f / height),
                params0 = new Vector4(
                    Mathf.Clamp(settings.thickness.value * settings.thickness.value, 0f, 0.99f),
                    height * invHalfTanFov * 0.25f,
                    radius,
                    settings.stepCount.value),
                params1 = new Vector4(
                    settings.intensity.value,
                    1f / (radius * radius),
                    0f, 0f),
            };

            float aspect = (float)height / width;
            p.depthToView = new Vector4(
                2f / (invHalfTanFov * aspect * width),
                2f / (invHalfTanFov * height),
                1f / (invHalfTanFov * aspect),
                1f / invHalfTanFov);

            float scaleFactor = (float)width * height / (540f * 960f);
            float radiusInPixels = Mathf.Max(16f, settings.maximumRadiusInPixels.value * Mathf.Sqrt(scaleFactor));
            const float downsample = 2f;
            p.params2 = new Vector4(settings.directionCount.value, 1f / downsample,
                                    1f / (settings.stepCount.value + 1f), radiusInPixels);

            float blurTolerance = 1f - settings.blurSharpness.value;
            blurTolerance = -2.5f + blurTolerance * (0.25f - -2.5f);
            const float stepSize = 0.5f;
            float bTolerance = 1f - Mathf.Pow(10f, blurTolerance) * stepSize;
            bTolerance *= bTolerance;
            const float upsampleTolerance = -7f;
            float uTolerance = Mathf.Pow(10f, upsampleTolerance);
            float noiseFilterWeight = 1f / (1f + uTolerance);
            p.params3 = new Vector4(bTolerance, uTolerance, noiseFilterWeight, stepSize);

            // The ported half-resolution path has no full-res spatial denoise; kept for layout.
            p.params4 = Vector4.zero;

            int mip1 = Math.Min(1, _mipInfo.mipLevelCount - 1);
            int mip2 = Math.Min(2, _mipInfo.mipLevelCount - 1);
            p.mipOffsets = new Vector4(_mipInfo.offsets[mip1].x, _mipInfo.offsets[mip1].y,
                                       _mipInfo.offsets[mip2].x, _mipInfo.offsets[mip2].y);
            return p;
        }

        static void SetParameters(ComputeCommandBuffer cmd, ComputeShader cs, int kernel, TraceData data)
        {
            cmd.SetComputeVectorParam(cs, AOBufferSizeId, data.bufferSize);
            cmd.SetComputeVectorParam(cs, AOParams0Id, data.params0);
            cmd.SetComputeVectorParam(cs, AOParams1Id, data.params1);
            cmd.SetComputeVectorParam(cs, AOParams2Id, data.params2);
            cmd.SetComputeVectorParam(cs, AOParams3Id, data.params3);
            cmd.SetComputeVectorParam(cs, AOParams4Id, data.params4);
            cmd.SetComputeVectorParam(cs, MipOffsetsId, data.mipOffsets);
            cmd.SetComputeVectorParam(cs, DepthToViewId, data.depthToView);
        }

        static void SetParameters(ComputeCommandBuffer cmd, ComputeShader cs, int kernel, UpsampleData data)
        {
            cmd.SetComputeVectorParam(cs, AOBufferSizeId, data.bufferSize);
            cmd.SetComputeVectorParam(cs, AOParams0Id, data.params0);
            cmd.SetComputeVectorParam(cs, AOParams1Id, data.params1);
            cmd.SetComputeVectorParam(cs, AOParams2Id, data.params2);
            cmd.SetComputeVectorParam(cs, AOParams3Id, data.params3);
            cmd.SetComputeVectorParam(cs, AOParams4Id, data.params4);
            cmd.SetComputeVectorParam(cs, MipOffsetsId, data.mipOffsets);
            cmd.SetComputeVectorParam(cs, DepthToViewId, data.depthToView);
        }

        static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}
