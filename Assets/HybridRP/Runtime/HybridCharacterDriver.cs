using System.Collections.Generic;
using UnityEngine;

namespace HybridRP
{
    /// <summary>
    /// Publishes the character's head basis and the global Hybrid shading weights.
    ///
    /// The SDF face shadow needs a head-right / head-forward frame in world space; the
    /// shader can't read a bone transform, so this component writes the vectors as shader
    /// globals. It also owns the scene-wide effect weights, which is what lets one
    /// Shader.SetGlobal call switch an effect off for an A/B capture. Globals are reset by
    /// a domain reload, so everything is republished in OnEnable and every frame.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class HybridCharacterDriver : MonoBehaviour
    {
        [Header("Head")]
        [SerializeField] private Transform _headBone;
        [SerializeField] private string _headBoneName = "頭";

        [Header("Effect weights")]
        [SerializeField, Range(0f, 1f)] private float _rampWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _shadowColorWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _specWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _sdfWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _hairHighlightWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _rimWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _outlineWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _ssaoWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float _blushWeight = 0f;
        [SerializeField, Range(0f, 1f)] private float _tearWeight = 0f;

        [Header("Environment integration (Stage 2)")]
        [Tooltip("Probe-GI normal blended toward the body capsule: keeps the environment's direction and colour, drops normal-map and fold detail the ramp would turn into blotches.")]
        [SerializeField, Range(0f, 1f)] private float _giNormalFlatten = 0.75f;
        [Tooltip("Probe-GI sample position slid toward the body axis (height kept): removes per-pixel grain and wall leaks from a 1 m probe grid.")]
        [SerializeField, Range(0f, 1f)] private float _giPositionLock = 0.85f;
        [Tooltip("Fresnel rim lit by the surrounding probe light.")]
        [SerializeField, Range(0f, 2f)] private float _envRimWeight = 0.35f;
        [Tooltip("Fresnel rim lit by a main light behind the character.")]
        [SerializeField, Range(0f, 2f)] private float _backRimWeight = 0.8f;

        [Header("Stylised shadows (portfolio pass)")]
        [Tooltip("Drops strand- and fold-scale self-shadow from the cascade map (large cast shadows stay). 0 = URP shadows as is.")]
        [SerializeField, Range(0f, 1f)] private float _selfShadowSmoothing = 0f;
        [Tooltip("Share of the main light kept in shadow as the material shadow colour, so shadow sides stay readable in daylight.")]
        [SerializeField, Range(0f, 1f)] private float _shadowLift = 0f;
        [Tooltip("Screen-space depth rim: an even, thin silhouette line lit by the sun and the probe GI.")]
        [SerializeField, Range(0f, 2f)] private float _screenRimWeight = 0f;
        [Tooltip("Screen-space rim width in pixels at 1080p.")]
        [SerializeField, Range(0.5f, 8f)] private float _screenRimWidth = 3f;

        [Header("Character light rig (v4 pass)")]
        [Tooltip("Exposure of the character relative to the scene. A scene's sun and post exposure are set for its " +
                 "environment; this re-exposes the character so its albedo lands where the Endfield references put it " +
                 "(lit skin ~1.2x albedo on screen, dark hair ~0.5x). 1 = lit like the environment.")]
        [SerializeField, Range(0.05f, 2f)] private float _characterExposure = 1f;
        [Tooltip("Toe contrast on the character below the pivot (exponent on the colour's largest channel, hue kept): " +
                 "darkens dark materials (hair, leather) without touching lit skin and highlights. 1 = off.")]
        [SerializeField, Range(0.5f, 2.5f)] private float _characterContrast = 1f;
        [Tooltip("Largest channel (after the character exposure) above which the toe contrast does nothing.")]
        [SerializeField, Range(0.05f, 4f)] private float _characterContrastPivot = 1f;

        [Header("Light-driven highlights (v4 pass)")]
        [Tooltip("0 = off. 1 = the reflection-probe specular follows the key light (15% in the character's shadow, " +
                 "60% where the key falls) and the Fresnel rim takes the key light's hue and shadow, so highlights " +
                 "change with the lighting instead of reading as painted on.")]
        [SerializeField, Range(0f, 1f)] private float _highlightLightDrive = 0f;

        [Header("Modes")]
        [SerializeField, Range(0, 1)] private int _shadingMode = 0;   // 0 Hybrid, 1 PBR
        [SerializeField, Range(0, 11)] private int _debugView = 0;

        static readonly int HeadRightId = Shader.PropertyToID("_HybridHeadRight");
        static readonly int HeadForwardId = Shader.PropertyToID("_HybridHeadForward");
        static readonly int HeadUpId = Shader.PropertyToID("_HybridHeadUp");
        static readonly int HeadPositionId = Shader.PropertyToID("_HybridHeadPosition");
        static readonly int RampWeightId = Shader.PropertyToID("_HybridRampWeight");
        static readonly int ShadowColorWeightId = Shader.PropertyToID("_HybridShadowColorWeight");
        static readonly int SpecWeightId = Shader.PropertyToID("_HybridSpecWeight");
        static readonly int SdfWeightId = Shader.PropertyToID("_HybridSDFWeight");
        static readonly int HairHighlightWeightId = Shader.PropertyToID("_HybridHairHLWeight");
        static readonly int RimWeightId = Shader.PropertyToID("_HybridRimWeight");
        static readonly int OutlineWeightId = Shader.PropertyToID("_HybridOutlineWeight");
        static readonly int SsaoWeightId = Shader.PropertyToID("_HybridSSAOWeight");
        static readonly int BlushWeightId = Shader.PropertyToID("_HybridBlushWeight");
        static readonly int TearWeightId = Shader.PropertyToID("_HybridTearWeight");
        static readonly int ShadingModeId = Shader.PropertyToID("_HybridShadingMode");
        static readonly int DebugViewId = Shader.PropertyToID("_HybridDebugView");
        static readonly int CharacterCenterId = Shader.PropertyToID("_HybridCharacterCenter");
        static readonly int GINormalFlattenId = Shader.PropertyToID("_HybridGINormalFlatten");
        static readonly int GIPositionLockId = Shader.PropertyToID("_HybridGIPositionLock");
        static readonly int EnvRimWeightId = Shader.PropertyToID("_HybridEnvRimWeight");
        static readonly int BackRimWeightId = Shader.PropertyToID("_HybridBackRimWeight");
        static readonly int SelfShadowSmoothingId = Shader.PropertyToID("_HybridSelfShadowSmoothing");
        static readonly int ShadowLiftId = Shader.PropertyToID("_HybridShadowLift");
        static readonly int ScreenRimWeightId = Shader.PropertyToID("_HybridScreenRimWeight");
        static readonly int ScreenRimWidthId = Shader.PropertyToID("_HybridScreenRimWidth");
        static readonly int CharacterExposureId = Shader.PropertyToID("_HybridCharacterExposure");
        static readonly int CharacterContrastId = Shader.PropertyToID("_HybridCharacterContrast");
        static readonly int CharacterContrastPivotId = Shader.PropertyToID("_HybridCharacterContrastPivot");
        static readonly int HighlightLightDriveId = Shader.PropertyToID("_HybridHighlightLightDrive");

        Renderer[] _renderers;

        /// <summary>Driver fields that a capture tool must put back after a sheet.</summary>
        public struct DriverState
        {
            public float ramp, shadowColor, specular, sdf, hairHighlight, rim, outline, ssao, blush, tear;
            public float giNormalFlatten, giPositionLock, envRim, backRim, selfShadowSmoothing, shadowLift, screenRim;
            public float screenRimWidth, characterExposure, characterContrast, characterContrastPivot, highlightLightDrive;
            public int shadingMode, debugView;
        }

        public Transform HeadBone => _headBone;

        public DriverState CaptureState() => new DriverState
        {
            ramp = _rampWeight,
            shadowColor = _shadowColorWeight,
            specular = _specWeight,
            sdf = _sdfWeight,
            hairHighlight = _hairHighlightWeight,
            rim = _rimWeight,
            outline = _outlineWeight,
            ssao = _ssaoWeight,
            blush = _blushWeight,
            tear = _tearWeight,
            giNormalFlatten = _giNormalFlatten,
            giPositionLock = _giPositionLock,
            envRim = _envRimWeight,
            backRim = _backRimWeight,
            selfShadowSmoothing = _selfShadowSmoothing,
            shadowLift = _shadowLift,
            screenRim = _screenRimWeight,
            screenRimWidth = _screenRimWidth,
            characterExposure = _characterExposure,
            characterContrast = _characterContrast,
            characterContrastPivot = _characterContrastPivot,
            highlightLightDrive = _highlightLightDrive,
            shadingMode = _shadingMode,
            debugView = _debugView,
        };

        public void RestoreState(DriverState state)
        {
            _rampWeight = state.ramp;
            _shadowColorWeight = state.shadowColor;
            _specWeight = state.specular;
            _sdfWeight = state.sdf;
            _hairHighlightWeight = state.hairHighlight;
            _rimWeight = state.rim;
            _outlineWeight = state.outline;
            _ssaoWeight = state.ssao;
            _blushWeight = state.blush;
            _tearWeight = state.tear;
            _giNormalFlatten = state.giNormalFlatten;
            _giPositionLock = state.giPositionLock;
            _envRimWeight = state.envRim;
            _backRimWeight = state.backRim;
            _selfShadowSmoothing = state.selfShadowSmoothing;
            _shadowLift = state.shadowLift;
            _screenRimWeight = state.screenRim;
            // A default-constructed state carries 0 here; keep the current values then (an exposure of 0
            // would black out the character).
            if (state.screenRimWidth > 0f)
                _screenRimWidth = state.screenRimWidth;
            if (state.characterExposure > 0f)
                _characterExposure = state.characterExposure;
            if (state.characterContrast > 0f)
                _characterContrast = state.characterContrast;
            if (state.characterContrastPivot > 0f)
                _characterContrastPivot = state.characterContrastPivot;
            _highlightLightDrive = state.highlightLightDrive;
            _shadingMode = state.shadingMode;
            _debugView = state.debugView;
            PublishWeights();
        }

        // Without a driver (material preview, a different scene) the shader globals would
        // all be 0: black specular/SSAO and no GI. Publish neutral defaults first; a scene
        // driver and ShowcaseRig overwrite them in OnEnable.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void PublishGlobalDefaults()
        {
            Shader.SetGlobalFloat(RampWeightId, 1f);
            Shader.SetGlobalFloat(ShadowColorWeightId, 1f);
            Shader.SetGlobalFloat(SpecWeightId, 1f);
            Shader.SetGlobalFloat(SdfWeightId, 1f);
            Shader.SetGlobalFloat(HairHighlightWeightId, 1f);
            Shader.SetGlobalFloat(RimWeightId, 1f);
            Shader.SetGlobalFloat(OutlineWeightId, 1f);
            Shader.SetGlobalFloat(SsaoWeightId, 1f);
            Shader.SetGlobalFloat(BlushWeightId, 0f);
            Shader.SetGlobalFloat(TearWeightId, 0f);
            Shader.SetGlobalFloat(GINormalFlattenId, 0f);
            Shader.SetGlobalFloat(GIPositionLockId, 0f);
            Shader.SetGlobalFloat(EnvRimWeightId, 0f);
            Shader.SetGlobalFloat(BackRimWeightId, 0f);
            Shader.SetGlobalFloat(SelfShadowSmoothingId, 0f);
            Shader.SetGlobalFloat(ShadowLiftId, 0f);
            Shader.SetGlobalFloat(ScreenRimWeightId, 0f);
            Shader.SetGlobalFloat(ScreenRimWidthId, 3f);
            Shader.SetGlobalFloat(CharacterExposureId, 1f);
            Shader.SetGlobalFloat(CharacterContrastId, 1f);
            Shader.SetGlobalFloat(CharacterContrastPivotId, 1f);
            Shader.SetGlobalFloat(HighlightLightDriveId, 0f);
            // w < 0 marks "no character": the probe-GI override then returns URP's own value.
            Shader.SetGlobalVector(CharacterCenterId, new Vector4(0f, 0f, 0f, -1f));
            // _HybridIndirectScale belongs to ShowcaseRig; setting it here would overwrite
            // a LowAmbient state after a domain reload.
            Shader.SetGlobalInt(ShadingModeId, 0);
            Shader.SetGlobalInt(DebugViewId, 0);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void PublishEditorDefaults() => PublishGlobalDefaults();
#endif

        void OnEnable()
        {
            ResolveHeadBone();
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (Renderer characterRenderer in renderers)
                characterRenderer.renderingLayerMask |= HybridRenderFeature.CharacterRenderingLayer;
            // Only the body parts define the capsule; a particle/trail/line renderer under the
            // root would inflate the bounds.
            var bounded = new List<Renderer>(renderers.Length);
            foreach (var characterRenderer in renderers)
                if (characterRenderer is SkinnedMeshRenderer || characterRenderer is MeshRenderer)
                    bounded.Add(characterRenderer);
            _renderers = bounded.ToArray();
            PublishWeights();
            PublishHeadBasis();
            PublishCapsule();
        }

        void OnDisable()
        {
            // The weights are global: without a reset a disabled driver (or the next scene, if it has
            // no character) would keep this character's exposure, light drive and stylised shadows.
            PublishGlobalDefaults();
        }

        void LateUpdate()
        {
            PublishHeadBasis();
            PublishCapsule();
        }

        void OnValidate()
        {
            if (!isActiveAndEnabled)
                return;
            PublishWeights();
            PublishHeadBasis();
            PublishCapsule();
        }

        void ResolveHeadBone()
        {
            if (_headBone != null || string.IsNullOrEmpty(_headBoneName))
                return;

            foreach (Transform candidate in GetComponentsInChildren<Transform>(true))
            {
                if (candidate.name == _headBoneName)
                {
                    _headBone = candidate;
                    break;
                }
            }
        }

        void PublishWeights()
        {
            Shader.SetGlobalFloat(RampWeightId, _rampWeight);
            Shader.SetGlobalFloat(ShadowColorWeightId, _shadowColorWeight);
            Shader.SetGlobalFloat(SpecWeightId, _specWeight);
            Shader.SetGlobalFloat(SdfWeightId, _sdfWeight);
            Shader.SetGlobalFloat(HairHighlightWeightId, _hairHighlightWeight);
            Shader.SetGlobalFloat(RimWeightId, _rimWeight);
            Shader.SetGlobalFloat(OutlineWeightId, _outlineWeight);
            Shader.SetGlobalFloat(SsaoWeightId, _ssaoWeight);
            Shader.SetGlobalFloat(BlushWeightId, _blushWeight);
            Shader.SetGlobalFloat(TearWeightId, _tearWeight);
            Shader.SetGlobalFloat(GINormalFlattenId, _giNormalFlatten);
            Shader.SetGlobalFloat(GIPositionLockId, _giPositionLock);
            Shader.SetGlobalFloat(EnvRimWeightId, _envRimWeight);
            Shader.SetGlobalFloat(BackRimWeightId, _backRimWeight);
            Shader.SetGlobalFloat(SelfShadowSmoothingId, _selfShadowSmoothing);
            Shader.SetGlobalFloat(ShadowLiftId, _shadowLift);
            Shader.SetGlobalFloat(ScreenRimWeightId, _screenRimWeight);
            Shader.SetGlobalFloat(ScreenRimWidthId, _screenRimWidth);
            Shader.SetGlobalFloat(CharacterExposureId, _characterExposure);
            Shader.SetGlobalFloat(CharacterContrastId, _characterContrast);
            Shader.SetGlobalFloat(CharacterContrastPivotId, _characterContrastPivot);
            Shader.SetGlobalFloat(HighlightLightDriveId, _highlightLightDrive);
            Shader.SetGlobalInt(ShadingModeId, _shadingMode);
            Shader.SetGlobalInt(DebugViewId, _debugView);
        }

        void PublishHeadBasis()
        {
            Transform source = _headBone != null ? _headBone : transform;
            Vector3 forward = Vector3.ProjectOnPlane(source.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            forward.Normalize();

            Vector3 right = Vector3.ProjectOnPlane(source.right, Vector3.up);
            if (right.sqrMagnitude < 1e-6f)
                right = Vector3.ProjectOnPlane(transform.right, Vector3.up);
            right.Normalize();

            Vector3 up = Vector3.Cross(forward, right).normalized;

            Shader.SetGlobalVector(HeadRightId, new Vector4(right.x, right.y, right.z, 0f));
            Shader.SetGlobalVector(HeadForwardId, new Vector4(forward.x, forward.y, forward.z, 0f));
            Shader.SetGlobalVector(HeadUpId, new Vector4(up.x, up.y, up.z, 0f));
            Shader.SetGlobalVector(HeadPositionId, new Vector4(source.position.x, source.position.y, source.position.z, 1f));
        }

        // Vertical capsule around the body for the probe-GI override: xyz = bounds centre,
        // w = half-length of the axis segment. The radius is a body radius, not the bounds'
        // half-width: outstretched arms widen the bounds and would flatten the capsule into a
        // sphere. Skinned bounds follow the animation.
        const float BodyRadius = 0.25f;

        void PublishCapsule()
        {
            // No character (no renderers, all disabled, or between disable/enable): mark the
            // capsule invalid so the probe-GI override falls back to URP's own sample.
            var invalid = new Vector4(0f, 0f, 0f, -1f);
            if (_renderers == null || _renderers.Length == 0)
            {
                Shader.SetGlobalVector(CharacterCenterId, invalid);
                return;
            }
            bool any = false;
            Bounds bounds = default;
            foreach (var characterRenderer in _renderers)
            {
                if (characterRenderer == null || !characterRenderer.enabled || !characterRenderer.gameObject.activeInHierarchy)
                    continue;
                if (!any)
                    bounds = characterRenderer.bounds;
                else
                    bounds.Encapsulate(characterRenderer.bounds);
                any = true;
            }
            if (!any)
            {
                Shader.SetGlobalVector(CharacterCenterId, invalid);
                return;
            }
            float halfLength = Mathf.Max(0f, bounds.extents.y - BodyRadius);
            Shader.SetGlobalVector(CharacterCenterId, new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, halfLength));
        }

        public void SetIntegrationWeights(float giNormalFlatten, float giPositionLock, float envRim, float backRim)
        {
            _giNormalFlatten = Mathf.Clamp01(giNormalFlatten);
            _giPositionLock = Mathf.Clamp01(giPositionLock);
            _envRimWeight = Mathf.Clamp(envRim, 0f, 2f);
            _backRimWeight = Mathf.Clamp(backRim, 0f, 2f);
            PublishWeights();
        }

        public void SetStylisedShadows(float selfShadowSmoothing, float shadowLift, float screenRim = 0f, float screenRimWidth = 3f)
        {
            _selfShadowSmoothing = Mathf.Clamp01(selfShadowSmoothing);
            _shadowLift = Mathf.Clamp01(shadowLift);
            _screenRimWeight = Mathf.Clamp(screenRim, 0f, 2f);
            _screenRimWidth = Mathf.Clamp(screenRimWidth, 0.5f, 8f);
            PublishWeights();
        }

        public float CharacterExposure => _characterExposure;

        public void SetCharacterExposure(float exposure, float contrast = 1f, float contrastPivot = 1f)
        {
            _characterExposure = Mathf.Clamp(exposure, 0.05f, 2f);
            _characterContrast = Mathf.Clamp(contrast, 0.5f, 2.5f);
            _characterContrastPivot = Mathf.Clamp(contrastPivot, 0.05f, 4f);
            PublishWeights();
        }

        public void SetHighlightLightDrive(float drive)
        {
            _highlightLightDrive = Mathf.Clamp01(drive);
            PublishWeights();
        }

        // Runtime/eval hooks used by the capture and comparison tools.
        public void SetEffectWeights(float ramp, float shadowColor, float specular, float sdf,
                                     float hairHighlight, float rim, float ssao)
        {
            _rampWeight = Mathf.Clamp01(ramp);
            _shadowColorWeight = Mathf.Clamp01(shadowColor);
            _specWeight = Mathf.Clamp01(specular);
            _sdfWeight = Mathf.Clamp01(sdf);
            _hairHighlightWeight = Mathf.Clamp01(hairHighlight);
            _rimWeight = Mathf.Clamp01(rim);
            _ssaoWeight = Mathf.Clamp01(ssao);
            PublishWeights();
        }

        public void SetOutlineWeight(float weight)
        {
            _outlineWeight = Mathf.Clamp01(weight);
            Shader.SetGlobalFloat(OutlineWeightId, _outlineWeight);
        }

        public void SetOverlayWeights(float blush, float tear)
        {
            _blushWeight = Mathf.Clamp01(blush);
            _tearWeight = Mathf.Clamp01(tear);
            PublishWeights();
        }

        public void SetShadingMode(int mode)
        {
            _shadingMode = Mathf.Clamp(mode, 0, 1);
            Shader.SetGlobalInt(ShadingModeId, _shadingMode);
        }

        public void SetDebugView(int view)
        {
            _debugView = Mathf.Clamp(view, 0, 11);
            Shader.SetGlobalInt(DebugViewId, _debugView);
        }

        public static HybridCharacterDriver Find() => FindFirstObjectByType<HybridCharacterDriver>();

        public static void ApplyShadingMode(int mode) => Find()?.SetShadingMode(mode);
        public static void ApplyDebugView(int view) => Find()?.SetDebugView(view);
    }
}
