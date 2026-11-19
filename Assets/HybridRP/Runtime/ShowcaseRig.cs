using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HybridRP
{
    /// <summary>
    /// Repeatable camera framings and lighting states for comparison captures.
    /// Both are expressed relative to the character (or another anchor), so they survive
    /// moving the character around the scene. Static entry points exist for CLI/eval use:
    /// <c>ShowcaseRig.ApplyBookmark("Face"); ShowcaseRig.ApplyPreset("Side");</c>
    /// </summary>
    [ExecuteAlways]
    public class ShowcaseRig : MonoBehaviour
    {
        [Serializable]
        public class CameraBookmark
        {
            public string name;
            [Tooltip("Frame relative to this transform; defaults to the rig's character.")]
            public Transform anchor;
            [Tooltip("Camera position in the anchor's local space (yaw only, metres).")]
            public Vector3 position;
            [Tooltip("Look-at point in the anchor's local space.")]
            public Vector3 lookAt;
            [Range(5f, 90f)] public float fieldOfView = 35f;
            [Tooltip("Shown while this bookmark is active and hidden for every other bookmark (e.g. the calibration board).")]
            public GameObject reveal;
            [Tooltip("Lens f-stop for URP Bokeh depth of field through the rig's lens volume; 0 = no depth of field.")]
            public float aperture;
            [Tooltip("Lens focal length (mm): with the f-stop it sets the blur size.")]
            public float focalLength = 85f;
            [Tooltip("Focus point in the anchor's local space (the eyes for a portrait).")]
            public Vector3 focusPoint;
        }

        [Serializable]
        public class LightingPreset
        {
            public string name;
            [Tooltip("Direction the light comes from, around the character: 0 = front, 90 = character's right, 180 = behind.")]
            [Range(-180f, 180f)] public float yaw;
            [Tooltip("Light elevation above the horizon.")]
            [Range(-10f, 90f)] public float elevation = 35f;
            public float intensity = 1.15f;
            public Color color = new Color(1f, 0.877f, 0.7f);
            // Max 1: RenderSettings clamps reflection intensity to 0..1, so a higher scale would
            // brighten ambient but silently leave reflections unchanged.
            [Tooltip("Dims ambient/environment lighting and reflections; < 1 for the low-ambient test.")]
            [Range(0f, 1f)] public float ambientScale = 1f;
        }

        static readonly int IndirectScaleId = Shader.PropertyToID("_HybridIndirectScale");

        [SerializeField] private Camera _camera;
        [SerializeField] private Light _sun;
        [SerializeField] private Transform _character;
        [Tooltip("Optional global volume whose profile holds a DepthOfField override. Bookmarks with an aperture " +
                 "turn it on and focus it; others turn it off. The profile is a dedicated asset the rig drives.")]
        [SerializeField] private Volume _lensVolume;
        [SerializeField] private List<CameraBookmark> _bookmarks = new List<CameraBookmark>();
        [SerializeField] private List<LightingPreset> _presets = new List<LightingPreset>();

        // Ambient state captured from the scene, so ambientScale is applied relative to it
        // instead of compounding across repeated presets.
        [SerializeField, HideInInspector] private bool _ambientCaptured;
        [SerializeField, HideInInspector] private Color _baseAmbientSky, _baseAmbientEquator, _baseAmbientGround;
        [SerializeField, HideInInspector] private float _baseAmbientIntensity = 1f, _baseReflectionIntensity = 1f;
        // Shader globals reset on every domain reload, so the active scale is kept to re-publish it.
        [SerializeField, HideInInspector] private float _activeIndirectScale = 1f;

        /// <summary>Everything a preset/bookmark changes, so captures can leave the scene as they found it.</summary>
        public struct State
        {
            internal Vector3 cameraPosition;
            internal Quaternion cameraRotation;
            internal float cameraFov;
            internal Quaternion sunRotation;
            internal float sunIntensity;
            internal Color sunColor;
            internal Color ambientSky, ambientEquator, ambientGround;
            internal float ambientIntensity, reflectionIntensity, indirectScale;
            internal string bookmark, preset;
            internal float lensWeight, lensFocus, lensAperture, lensFocalLength;
        }

        public string ActiveBookmark { get; private set; }
        public string ActivePreset { get; private set; }
        public Camera Camera => _camera;
        public Light Sun => _sun;
        public IReadOnlyList<CameraBookmark> Bookmarks => _bookmarks;
        public IReadOnlyList<LightingPreset> Presets => _presets;

        void Reset()
        {
            _camera = Camera.main;
            _sun = RenderSettings.sun;
            // Project-specific convenience: the bookmarks below are authored in this model's frame.
            var character = GameObject.Find("ZhuangFangyi");
            if (character != null)
                _character = character.transform;
            _bookmarks = new List<CameraBookmark>
            {
                new CameraBookmark { name = "Overview",      position = new Vector3(-2.6f, 2.2f, 9f),  lookAt = new Vector3(0f, 1.2f, 0f),  fieldOfView = 50f },
                new CameraBookmark { name = "FullBody",      position = new Vector3(0f, 1.0f, 3.8f),    lookAt = new Vector3(0f, 0.95f, 0f), fieldOfView = 32f },
                new CameraBookmark { name = "Face",          position = new Vector3(0f, 1.6f, 0.75f),   lookAt = new Vector3(0f, 1.58f, 0f), fieldOfView = 24f },
                new CameraBookmark { name = "Hair",          position = new Vector3(-0.85f, 1.8f, -0.7f), lookAt = new Vector3(0f, 1.55f, 0f), fieldOfView = 30f },
                new CameraBookmark { name = "ContactShadow", position = new Vector3(2.4f, 1.1f, 1.2f),  lookAt = new Vector3(0f, 0.3f, -0.4f), fieldOfView = 40f },
            };
            _presets = new List<LightingPreset>
            {
                new LightingPreset { name = "Front",      yaw = 20f,  elevation = 50f },
                new LightingPreset { name = "Side",       yaw = -90f, elevation = 50f },
                new LightingPreset { name = "Back",       yaw = 165f, elevation = 38f },
                new LightingPreset { name = "LowAmbient", yaw = 50f,  elevation = 30f, intensity = 0.9f, ambientScale = 0.2f },
            };
            CaptureAmbient();
        }

        void OnEnable()
        {
            if (!_ambientCaptured)
                CaptureAmbient();
            Shader.SetGlobalFloat(IndirectScaleId, _activeIndirectScale);
        }

        public State Capture()
        {
            var state = new State
            {
                ambientSky = RenderSettings.ambientSkyColor,
                ambientEquator = RenderSettings.ambientEquatorColor,
                ambientGround = RenderSettings.ambientGroundColor,
                ambientIntensity = RenderSettings.ambientIntensity,
                reflectionIntensity = RenderSettings.reflectionIntensity,
                indirectScale = _activeIndirectScale,
                bookmark = ActiveBookmark,
                preset = ActivePreset,
            };
            if (_camera != null)
            {
                state.cameraPosition = _camera.transform.position;
                state.cameraRotation = _camera.transform.rotation;
                state.cameraFov = _camera.fieldOfView;
            }
            if (_sun != null)
            {
                state.sunRotation = _sun.transform.rotation;
                state.sunIntensity = _sun.intensity;
                state.sunColor = _sun.color;
            }
            if (TryGetLens(out var dof))
            {
                state.lensWeight = _lensVolume.weight;
                state.lensFocus = dof.focusDistance.value;
                state.lensAperture = dof.aperture.value;
                state.lensFocalLength = dof.focalLength.value;
            }
            return state;
        }

        public void Restore(State state)
        {
            if (_camera != null)
            {
                _camera.transform.SetPositionAndRotation(state.cameraPosition, state.cameraRotation);
                _camera.fieldOfView = state.cameraFov;
            }
            if (_sun != null)
            {
                _sun.transform.rotation = state.sunRotation;
                _sun.intensity = state.sunIntensity;
                _sun.color = state.sunColor;
            }
            RenderSettings.ambientSkyColor = state.ambientSky;
            RenderSettings.ambientEquatorColor = state.ambientEquator;
            RenderSettings.ambientGroundColor = state.ambientGround;
            RenderSettings.ambientIntensity = state.ambientIntensity;
            RenderSettings.reflectionIntensity = state.reflectionIntensity;
            _activeIndirectScale = state.indirectScale;
            Shader.SetGlobalFloat(IndirectScaleId, _activeIndirectScale);
            ActiveBookmark = state.bookmark;
            ActivePreset = state.preset;
            UpdateRevealed(_bookmarks.Find(b => b.name == state.bookmark));
            if (TryGetLens(out var dof))
            {
                _lensVolume.weight = state.lensWeight;
                SetLens(dof, state.lensFocus, state.lensAperture, state.lensFocalLength);
            }
        }

        /// <summary>Re-reads the scene's current ambient/reflection settings as the 1.0 reference.</summary>
        public void CaptureAmbient()
        {
            _baseAmbientSky = RenderSettings.ambientSkyColor;
            _baseAmbientEquator = RenderSettings.ambientEquatorColor;
            _baseAmbientGround = RenderSettings.ambientGroundColor;
            _baseAmbientIntensity = RenderSettings.ambientIntensity;
            _baseReflectionIntensity = RenderSettings.reflectionIntensity;
            _ambientCaptured = true;
        }

        public bool Apply(string bookmarkName)
        {
            var bookmark = _bookmarks.Find(b => string.Equals(b.name, bookmarkName, StringComparison.OrdinalIgnoreCase));
            if (bookmark == null || _camera == null)
                return false;

            Transform anchor = bookmark.anchor != null ? bookmark.anchor : _character;
            if (anchor == null)
                Debug.LogWarning("[ShowcaseRig] No character assigned: bookmarks are applied in world space.", this);
            Matrix4x4 frame = anchor != null ? YawFrame(anchor) : Matrix4x4.identity;
            Vector3 position = frame.MultiplyPoint3x4(bookmark.position);
            Vector3 target = frame.MultiplyPoint3x4(bookmark.lookAt);

            _camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position, Vector3.up));
            _camera.fieldOfView = bookmark.fieldOfView;
            ActiveBookmark = bookmark.name;
            UpdateRevealed(bookmark);
            ApplyLens(bookmark, frame);
            return true;
        }

        // URP's Bokeh depth of field takes the focus distance along the view axis; it is measured to the
        // bookmark's focus point so the eyes stay sharp whatever the framing.
        void ApplyLens(CameraBookmark bookmark, Matrix4x4 frame)
        {
            if (!TryGetLens(out var dof))
                return;
            if (bookmark.aperture <= 0f)
            {
                _lensVolume.weight = 0f;
                return;
            }
            Vector3 focus = frame.MultiplyPoint3x4(bookmark.focusPoint);
            float distance = Mathf.Max(0.1f, Vector3.Dot(focus - _camera.transform.position, _camera.transform.forward));
            SetLens(dof, distance, bookmark.aperture, bookmark.focalLength);
            _lensVolume.weight = 1f;
        }

        bool TryGetLens(out DepthOfField dof)
        {
            dof = null;
            return _lensVolume != null && _lensVolume.sharedProfile != null && _lensVolume.sharedProfile.TryGet(out dof);
        }

        static void SetLens(DepthOfField dof, float focusDistance, float aperture, float focalLength)
        {
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(focusDistance);
            dof.aperture.Override(Mathf.Max(1f, aperture));
            dof.focalLength.Override(Mathf.Max(1f, focalLength));
        }

        void UpdateRevealed(CameraBookmark active)
        {
            // Objects owned by several bookmarks stay visible while any of them is active.
            foreach (var other in _bookmarks)
                if (other.reveal != null && other.reveal != active?.reveal)
                    other.reveal.SetActive(false);
            if (active?.reveal != null)
                active.reveal.SetActive(true);
        }

        public bool ApplyLighting(string presetName)
        {
            var preset = _presets.Find(p => string.Equals(p.name, presetName, StringComparison.OrdinalIgnoreCase));
            if (preset == null || _sun == null)
                return false;

            Vector3 forward = _character != null ? Flatten(_character.forward) : Vector3.forward;
            Vector3 toLight = Quaternion.AngleAxis(preset.yaw, Vector3.up) * forward;
            Vector3 axis = Vector3.Cross(Vector3.up, toLight);
            toLight = Quaternion.AngleAxis(-preset.elevation, axis) * toLight;

            // A light shines along its forward axis, i.e. away from where it "comes from".
            _sun.transform.rotation = Quaternion.LookRotation(-toLight, Vector3.up);
            _sun.intensity = preset.intensity;
            _sun.color = preset.color;

            ApplyAmbientScale(preset.ambientScale);
            ActivePreset = preset.name;
            return true;
        }

        /// <summary>
        /// Scales ambient and reflection lighting relative to the captured scene values (1 = scene as
        /// authored), and with <paramref name="scaleProbes"/> also the baked-probe lighting our shaders
        /// read. Values above 1 only brighten ambient: RenderSettings clamps reflection intensity to 0..1.
        /// </summary>
        public void ApplyAmbientScale(float s, bool scaleProbes = true)
        {
            RenderSettings.ambientSkyColor = _baseAmbientSky * s;
            RenderSettings.ambientEquatorColor = _baseAmbientEquator * s;
            RenderSettings.ambientGroundColor = _baseAmbientGround * s;
            RenderSettings.ambientIntensity = _baseAmbientIntensity * s;
            RenderSettings.reflectionIntensity = _baseReflectionIntensity * s;
            // Baked probe lighting can't be rescaled through RenderSettings; our shaders read this instead.
            _activeIndirectScale = scaleProbes ? s : 1f;
            Shader.SetGlobalFloat(IndirectScaleId, _activeIndirectScale);
        }

        public static bool ApplyBookmark(string name) => Find()?.Apply(name) ?? false;
        public static bool ApplyPreset(string name) => Find()?.ApplyLighting(name) ?? false;

        public static ShowcaseRig Find() => FindFirstObjectByType<ShowcaseRig>();

        static Matrix4x4 YawFrame(Transform anchor)
        {
            Vector3 forward = Flatten(anchor.forward);
            return Matrix4x4.TRS(anchor.position, Quaternion.LookRotation(forward, Vector3.up), Vector3.one);
        }

        static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
