using UnityEngine;
using UnityEngine.Rendering;

namespace HybridRP
{
    /// <summary>
    /// Ground-truth ambient occlusion (Stage 2, ported from IllusionRP/HDRP). Half resolution,
    /// traced against the camera depth pyramid and normals, denoised by a geometry-aware
    /// bilateral filter and published as URP's screen-space occlusion texture so every URP Lit,
    /// Terrain Lit and Hybrid character material consumes it without changes. Enable this and
    /// disable URP's Screen Space Ambient Occlusion feature: both write the same slot.
    /// </summary>
    [System.Serializable]
    [SupportedOnRenderPipeline(typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset))]
    [VolumeComponentMenu("HybridRP/GTAO")]
    public sealed class HybridGTAO : VolumeComponent
    {
        public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        [Tooltip("Raises the AO curve. 1 matches the traced visibility.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 4f);

        [Tooltip("How much AO also darkens direct lighting. URP SSAO's default is 0.")]
        public ClampedFloatParameter directLightingStrength = new ClampedFloatParameter(0.25f, 0f, 1f);

        [Tooltip("World-space sampling radius in metres.")]
        public ClampedFloatParameter radius = new ClampedFloatParameter(2f, 0.25f, 5f);

        [Tooltip("Thin/Thick heuristic: lower values let distant geometry occlude more.")]
        public ClampedFloatParameter thickness = new ClampedFloatParameter(0.5f, 0.001f, 1f);

        [Tooltip("Steps along each of the two ray directions.")]
        public ClampedIntParameter stepCount = new ClampedIntParameter(6, 2, 32);

        [Tooltip("Directions per pixel. No temporal accumulation: higher is less noisy and slower.")]
        public ClampedIntParameter directionCount = new ClampedIntParameter(2, 1, 6);

        [Tooltip("Radius clamp in pixels at 1080p (scaled with resolution).")]
        public ClampedIntParameter maximumRadiusInPixels = new ClampedIntParameter(40, 16, 256);

        [Tooltip("Bilateral blur sharpness: lower is smoother and noisier at edges.")]
        public ClampedFloatParameter blurSharpness = new ClampedFloatParameter(0.1f, 0f, 1f);

        public bool IsActive() => enable.value;
    }
}
