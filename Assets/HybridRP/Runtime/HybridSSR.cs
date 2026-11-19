using UnityEngine;
using UnityEngine.Rendering;

namespace HybridRP
{
    /// <summary>
    /// Full-resolution screen-space reflections for renderers on the reflector rendering layer
    /// (Stage 2). Misses add nothing, so those materials keep their reflection-probe response.
    /// Off by default: URP Lit has no SSR input and the transparent pond water cannot be traced.
    /// </summary>
    [System.Serializable]
    [SupportedOnRenderPipeline(typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset))]
    [VolumeComponentMenu("HybridRP/Screen Space Reflections")]
    public sealed class HybridSSR : VolumeComponent
    {
        public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        [Tooltip("Raymarch steps against the camera depth (quadratic distribution, dense near the surface).")]
        public ClampedIntParameter stepCount = new ClampedIntParameter(48, 8, 128);

        [Tooltip("Maximum ray distance in metres.")]
        public MinFloatParameter maxDistance = new MinFloatParameter(30f, 1f);

        [Tooltip("Surface thickness accepted as a hit, in metres (widened to half the local step).")]
        public ClampedFloatParameter thickness = new ClampedFloatParameter(0.5f, 0.01f, 2f);

        [Tooltip("Reflection intensity.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 2f);

        public bool IsActive() => enable.value;
    }
}
