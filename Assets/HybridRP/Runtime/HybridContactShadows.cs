using UnityEngine;
using UnityEngine.Rendering;

namespace HybridRP
{
    /// <summary>
    /// Screen-space contact shadows for the main directional light (Stage 2, ported from
    /// IllusionRP/HDRP). Rays are traced in screen space from each visible surface toward the
    /// light; the result is combined with the cascaded shadow map into URP's screen-space shadow
    /// texture, so environment and character materials receive it unchanged.
    /// </summary>
    [System.Serializable]
    [SupportedOnRenderPipeline(typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset))]
    [VolumeComponentMenu("HybridRP/Contact Shadows")]
    public sealed class HybridContactShadows : VolumeComponent
    {
        public BoolParameter enable = new BoolParameter(false, BoolParameter.DisplayType.EnumPopup);

        [Tooltip("Ray length in metres (scaled by distanceScaleFactor and the pixel's depth).")]
        public ClampedFloatParameter length = new ClampedFloatParameter(0.15f, 0f, 10f);

        [Tooltip("Ray length is multiplied by max(0.5, linearDepth * this).")]
        public ClampedFloatParameter distanceScaleFactor = new ClampedFloatParameter(0.5f, 0f, 1f);

        [Tooltip("Eye distance at which contact shadows have completely faded out.")]
        public MinFloatParameter maxDistance = new MinFloatParameter(50f, 0f);

        [Tooltip("Eye distance at which contact shadows begin.")]
        public MinFloatParameter minDistance = new MinFloatParameter(0f, 0f);

        [Tooltip("Length of the fade-out before maxDistance (contact shadows fade from maxDistance - fadeDistance to maxDistance).")]
        public MinFloatParameter fadeDistance = new MinFloatParameter(5f, 0f);

        [Tooltip("Length of the fade-in after minDistance.")]
        public MinFloatParameter fadeInDistance = new MinFloatParameter(0f, 0f);

        [Tooltip("Bias along the view ray, as a fraction of the distance to the camera.")]
        public ClampedFloatParameter rayBias = new ClampedFloatParameter(0.2f, 0f, 1f);

        [Tooltip("Object thickness assumed along the ray.")]
        public ClampedFloatParameter thicknessScale = new ClampedFloatParameter(0.15f, 0.02f, 10f);

        public ClampedIntParameter sampleCount = new ClampedIntParameter(10, 4, 64);

        public bool IsActive() => enable.value && length.value > 0f;
    }
}
