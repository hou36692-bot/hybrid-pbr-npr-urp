using UnityEngine;
using UnityEngine.Rendering;

namespace HybridRP
{
    /// <summary>
    /// Height-attenuated participating media with directional scattering and main-light shadowing
    /// (Stage 2). Replaces the exponential fog for the showcase look; applied to the environment
    /// and the character in the same pass.
    /// </summary>
    [System.Serializable]
    [SupportedOnRenderPipeline(typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset))]
    [VolumeComponentMenu("HybridRP/Volumetric Fog")]
    public sealed class HybridVolumetricFog : VolumeComponent
    {
        public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        public ColorParameter color = new ColorParameter(new Color(0.62f, 0.70f, 0.78f), false, false, true);

        [Tooltip("Density at the reference height (extinction per metre).")]
        public MinFloatParameter density = new MinFloatParameter(0.01f, 0f);

        [Tooltip("Height falloff: density * exp(-(y - baseHeight) / heightFalloff) above baseHeight; constant below it.")]
        public MinFloatParameter heightFalloff = new MinFloatParameter(40f, 0.01f);

        [Tooltip("World height at which density is the configured value.")]
        public FloatParameter baseHeight = new FloatParameter(0f);

        [Tooltip("Maximum raymarch distance in metres.")]
        public MinFloatParameter maxDistance = new MinFloatParameter(400f, 1f);

        [Tooltip("Raymarch steps per pixel.")]
        public ClampedIntParameter sampleCount = new ClampedIntParameter(24, 8, 96);

        [Tooltip("Scattering anisotropy: positive is forward scattering toward the sun.")]
        public ClampedFloatParameter anisotropy = new ClampedFloatParameter(0.35f, -0.9f, 0.9f);

        [Tooltip("In-scattered main-light energy.")]
        public ClampedFloatParameter lightScattering = new ClampedFloatParameter(1f, 0f, 4f);

        [Tooltip("Ambient in-scattering (sky colour) energy.")]
        public ClampedFloatParameter ambientScattering = new ClampedFloatParameter(0.35f, 0f, 4f);

        public bool IsActive() => enable.value && density.value > 0f;
    }
}
