using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    [Serializable, VolumeComponentMenu("HybridRP/Character Outline")]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    public sealed class HybridOutline : VolumeComponent, IPostProcessComponent
    {
        public BoolParameter enabled = new BoolParameter(false);
        public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 1f);
        public ColorParameter color = new ColorParameter(new Color(0.035f, 0.025f, 0.045f, 1f), false, false, true);

        [Tooltip("Line radius in pixels at 1080p, scaled with the final output height, not Render Scale.")]
        public ClampedFloatParameter width = new ClampedFloatParameter(1.5f, 0.5f, 4f);
        [Tooltip("Relative eye-depth discontinuity. Larger values suppress internal depth edges.")]
        public ClampedFloatParameter depthThreshold = new ClampedFloatParameter(0.015f, 0.001f, 0.2f);
        [Tooltip("Normal discontinuity (1 - dot). 0.4 is about 53 degrees.")]
        public ClampedFloatParameter normalThreshold = new ClampedFloatParameter(0.4f, 0.05f, 0.85f);
        public ClampedFloatParameter normalStrength = new ClampedFloatParameter(0.35f, 0f, 1f);

        [Tooltip("0 = the fixed line colour; 1 = a darker, more saturated shade of the surface under the line " +
                 "(warm brown on skin, near-black on hair, grey on white cloth), as in Endfield's per-material lines.")]
        public ClampedFloatParameter surfaceTint = new ClampedFloatParameter(0f, 0f, 1f);
        [Tooltip("Brightness of the surface-derived line relative to the surface.")]
        public ClampedFloatParameter surfaceDarkness = new ClampedFloatParameter(0.3f, 0.02f, 1f);
        [Tooltip("Saturation gain of the surface-derived line (chroma raised to this power).")]
        public ClampedFloatParameter surfaceSaturation = new ClampedFloatParameter(1.5f, 0f, 4f);

        public bool IsActive() => enabled.value && intensity.value > 0f;
    }
}
