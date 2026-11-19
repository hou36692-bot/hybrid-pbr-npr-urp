#ifndef HYBRID_CHARACTER_COMMON_INCLUDED
#define HYBRID_CHARACTER_COMMON_INCLUDED

// Shared lighting math for the Hybrid character shaders.
//
// The character deliberately does NOT reuse URP's UniversalFragmentPBR: it keeps URP's
// lights, shadows, baked GI (APV), reflection probes and fog, but replaces the diffuse
// response with a ramp lookup, the face response with an SDF, and adds the hair band and
// Fresnel rim. _HybridShadingMode = 1 switches all of that back to a URP-like PBR path so
// the portfolio can show a Hybrid-vs-PBR comparison from the same materials.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

// ---------------------------------------------------------------------------
// Global controls. These are set with Shader.SetGlobal* by HybridCharacterDriver
// (or by the CLI / debug panel), so a whole effect can be flipped for A/B captures
// without touching a material. Keeping them out of UnityPerMaterial also means one
// value controls all 36 character submeshes.
// ---------------------------------------------------------------------------
float _HybridRampWeight;          // 0..1, blends the ramp against plain Lambert
float _HybridShadowColorWeight;   // 0..1, shadow colour tint on indirect/diffuse
float _HybridSpecWeight;          // 0..1, direct GGX + hair band intensity
float _HybridSDFWeight;           // 0..1, SDF face shadow contribution
float _HybridHairHLWeight;        // 0..1, directional hair highlight band
float _HybridRimWeight;           // 0..1, Fresnel rim
float _HybridSSAOWeight;          // 0..1, global scale on the per-material SSAO strength
float _HybridIndirectScale;       // owned by ShowcaseRig (LowAmbient preset)
float _HybridBlushWeight;         // overlay only
float _HybridTearWeight;          // overlay only
int   _HybridShadingMode;         // 0 = Hybrid, 1 = PBR
int   _HybridDebugView;           // 0 = off, see HybridDebugColor

// Head basis in world space, published by HybridCharacterDriver. Only the face shader
// uses it, but declaring it here keeps the SDF math in one place.
float4 _HybridHeadRight;
float4 _HybridHeadForward;
float4 _HybridHeadUp;
float4 _HybridHeadPosition;

// Character/environment integration (Stage 2, S2-2), also published by the driver. Each
// weight at 0 reproduces the Phase 3-5 shading exactly.
float4 _HybridCharacterCenter;    // xyz: world-space centre of the character's bounds
float _HybridGINormalFlatten;     // 0..1, probe-GI normal blended toward the radial direction
float _HybridGIPositionLock;      // 0..1, probe-GI position blended toward the centre
float _HybridEnvRimWeight;        // 0..2, Fresnel rim lit by the probe GI
float _HybridBackRimWeight;       // 0..2, Fresnel rim lit by a main light behind the character

// Portfolio pass. 0 reproduces the Stage 2 shading.
float _HybridSelfShadowSmoothing; // 0..1, fine self-shadow removed (see HybridSmoothedShadow)
float _HybridShadowLift;          // 0..1, fraction of the direct light kept as a coloured fill in shadow
float _HybridScreenRimWeight;     // 0..2, screen-space depth rim (constant pixel width)
float _HybridScreenRimWidth;      // rim width in pixels at 1080p

// Light-driven highlights (v4 pass). 0 = the shading without them. The reflection probes are baked
// once, so their reflections on metal and satin stayed put when the sun moved or changed colour, and
// the Fresnel rim was a fixed white: on screen both read as highlights pasted onto the character.
// At 1 the probe reflection follows the key light (HybridReflectionLightScale) and the rim takes
// the key light's hue and shadow (HybridRim).
float _HybridHighlightLightDrive;

// Character light rig (v4 pass). A scene's sun and post exposure are chosen for its environment
// (the Wuling courtyard is lit with sun 2.6 and a positive post exposure); on the character that
// put lit skin into the tonemapper's shoulder and dark materials into the mid-tones, so near-black
// hair rendered grey.
// The character is re-exposed separately, as a cutscene light rig would light it. 1 = unchanged.
// Applied in both shading modes, like a camera exposure, so the Hybrid-vs-PBR comparison differs in
// shading only.
float _HybridCharacterExposure;
// Toe contrast below the pivot (exponent on the colour's value, hue kept). The references hold dark
// hair and black leather at ~0.5-0.7x their albedo on screen while lit skin sits at ~1.2x: more
// separation than a linear exposure under the Neutral curve gives. Values above the pivot are
// untouched, so lit skin and highlights keep their level. The value is the largest channel, not
// luminance: luminance counts a saturated red as dark and crushed the red hair tips. 1 = off.
float _HybridCharacterContrast;
float _HybridCharacterContrastPivot;

half3 HybridCharacterTone(half3 color)
{
    color *= _HybridCharacterExposure;
    if (_HybridCharacterContrast == 1.0)
        return color;
    half pivot = max((half)_HybridCharacterContrastPivot, 1e-3h);
    half value = Max3(color.r, color.g, color.b);
    half toned = value < pivot ? pivot * PositivePow(value / pivot, (half)_HybridCharacterContrast) : value;
    return color * (toned / max(value, 1e-4h));
}

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

// The character's normal maps store XY only (B = 0), so a standard UnpackNormal would
// produce a broken Z. Rebuild it from the unit-length constraint instead.
half3 HybridUnpackNormalXY(half4 packed)
{
    half2 xy = packed.rg * 2.0h - 1.0h;
    half z = sqrt(saturate(1.0h - dot(xy, xy)));
    return half3(xy, z);
}

half3 HybridNormalFromMap(half4 packed, half bumpScale, half3 normalWS, half4 tangentWS)
{
    half3 nTS = HybridUnpackNormalXY(packed);
    nTS.xy *= bumpScale;
    nTS = normalize(nTS);
    half3 bitangent = tangentWS.w * cross(normalWS, tangentWS.xyz);
    real3x3 tangentToWorld = real3x3(tangentWS.xyz, bitangent, normalWS);
    return normalize(TransformTangentToWorld(nTS, tangentToWorld));
}

// Equirectangular tangent used only by the hair highlight band: it runs "around" the
// head instead of following the mesh's UV tangent, which is what makes the band read as
// a directional highlight rather than a normal-map specular.
half3 HybridEquirectTangent(half3 normalWS)
{
    half3 axis = half3(0.0h, 1.0h, 0.0h);
    if (abs(normalWS.y) > 0.99h)
        axis = half3(0.0h, 0.0h, 1.0h);
    half3 bitangent = normalize(cross(normalWS, axis));
    return normalize(cross(normalWS, bitangent));
}

// SSAO is sampled by our shaders instead of URP's Lit so that the per-material
// _SSAOStrength can keep the large (1.2 m) environment radius from painting broad grey
// patches on a 20 cm head. Strength 0 = no AO on that material.
half HybridSampleSSAO(float2 normalizedScreenSpaceUV)
{
#if defined(_SCREEN_SPACE_OCCLUSION) && !defined(_SURFACE_TYPE_TRANSPARENT)
    half ssao = saturate(SampleAmbientOcclusion(normalizedScreenSpaceUV) + (1.0h - _AmbientOcclusionParam.x));
    return ssao;
#else
    return 1.0h;
#endif
}

half HybridApplySSAO(half rawAO, half strength)
{
    half weight = saturate(strength * _HybridSSAOWeight);
    return lerp(1.0h, rawAO, weight);
}

// GGX specular for one light. This follows URP's DirectBRDFSpecular terminology exactly
// (perceptual roughness, roughness = ppr², roughness2 = ppr⁴, normalizationTerm), so the
// Hybrid and PBR paths differ in the stylised shaping, not in the lobe math.
half3 HybridGGXSpecular(half3 f0, half smoothness, half3 normalWS, half3 lightDirWS, half3 viewDirWS, half3 radiance, half attenuation, half intensity)
{
    float perceptualRoughness = saturate(1.0 - (float)smoothness);
    float roughness = max(perceptualRoughness * perceptualRoughness, (float)HALF_MIN_SQRT);
    float roughness2 = max(roughness * roughness, (float)HALF_MIN);
    float3 halfDir = SafeNormalize(float3(lightDirWS) + float3(viewDirWS));
    float NoH = saturate(dot(float3(normalWS), halfDir));
    float LoH = saturate(dot(float3(lightDirWS), halfDir));
    float NoL = saturate(dot(normalWS, lightDirWS));

    float d = NoH * NoH * (roughness2 - 1.0) + 1.00001;
    float spec = roughness2 / ((d * d) * max(0.1, LoH * LoH) * (roughness * 4.0 + 2.0));
    spec = min(spec, 1000.0);

    float3 result = float3(f0) * float3(radiance) * (spec * NoL * (float)attenuation * (float)intensity);
    return half3((half)result.x, (half)result.y, (half)result.z);
}

// Screen-space depth rim. A Fresnel rim widens with the mesh's curvature (flat cloth gets none,
// a sphere gets a wide band); the stylised rim in Endfield-like renders is a thin, even line on
// the silhouette. Step from the pixel along the view-space normal for a fixed number of pixels
// and compare scene depth there: where the step leaves the character (the background is much
// farther), the pixel is within `width` pixels of the silhouette. Requires the depth prepass,
// which GTAO/the outline already request. Returns 0..1 coverage.
half HybridScreenRim(half3 normalWS, float4 positionCS, float2 screenUV)
{
#if defined(_SURFACE_TYPE_TRANSPARENT)
    // Transparent surfaces are not in the depth prepass: every pixel would see the opaque
    // depth behind it as a silhouette.
    return 0.0h;
#endif
    if (_HybridScreenRimWeight <= 0.0)
        return 0.0h;
    half3 normalVS = TransformWorldToViewNormal(normalWS, true);
    float2 dir = normalVS.xy;
    float len = length(dir);
    if (len < 1e-3)
        return 0.0h;
    dir /= len;
    // Pixels at 1080p, scaled with the output height; aspect corrected so the line is round.
    float pixels = _HybridScreenRimWidth * (_ScreenParams.y / 1080.0);
    float2 offset = dir * pixels * (_ScreenParams.zw - 1.0);
    float sceneDepth = LinearEyeDepth(positionCS.z, _ZBufferParams);
    float neighbour = LinearEyeDepth(SampleSceneDepth(screenUV + offset), _ZBufferParams);
    // Relative threshold: 25 cm at 2 m, scaled with distance, so folds and the arm over the
    // body (small depth steps) stay clean while the outer silhouette fires.
    float edge = saturate((neighbour - sceneDepth) / max(0.12 * sceneDepth, 0.05) - 1.0);
    // Grazing normals only: a front-facing pixel next to a depth step is an occlusion edge
    // drawn by the outline, not a rim.
    return half(edge * smoothstep(0.15, 0.6, len));
}

// Fresnel rim on the key light's side. keyLight is HybridKeyLightTint (the key light's colour,
// attenuation and shadow at unit luminance). With _HybridHighlightLightDrive the rim takes the light's
// hue, vanishes in cast shadow and narrows to the side facing the light, while its brightness stays
// the material's: the character is exposed apart from the scene (HybridCharacterTone).
half3 HybridRim(half3 normalWS, half3 viewDirWS, half3 lightDirWS, half3 keyLight, half3 color, half power, half intensity, half mask)
{
    float fresnel = pow(saturate(1.0 - saturate(dot(normalWS, viewDirWS))), max(0.01, (float)power));
    float halfLambert = saturate(dot(normalWS, lightDirWS) * 0.5 + 0.5);
    float drive = saturate(_HybridHighlightLightDrive);
    float litSide = lerp(halfLambert, halfLambert * halfLambert, drive);
    float3 light = lerp(float3(1.0, 1.0, 1.0), float3(keyLight), drive);
    float3 result = float3(color) * light * (fresnel * (float)intensity * (float)mask * litSide);
    return half3((half)result.x, (half)result.y, (half)result.z);
}

// Scale on the reflection-probe specular. The probe image stays (it is what makes metal read as
// metal), but with _HybridHighlightLightDrive it drops to 15% in the character's shadow and 60% where
// the key light falls, so the GGX key highlight, which follows the light, carries the shape.
// keyLit is half-Lambert x shadow. The PBR reference path keeps URP's value.
half HybridReflectionLightScale(half keyLit)
{
    if (_HybridShadingMode != 0)
        return 1.0h;
    return lerp(1.0h, lerp(0.15h, 0.6h, saturate(keyLit)), (half)saturate(_HybridHighlightLightDrive));
}

// Ramp coordinate: URP shadow attenuation and half-Lambert compete, so a cast shadow
// pushes the lookup into the ramp's dark end even where the normal faces the light.
half HybridRampCoordinate(half ndotl, half attenuation)
{
    half halfLambert = ndotl * 0.5h + 0.5h;
    return saturate(min(halfLambert, attenuation));
}

// Angle threshold for the SDF. Adapted from TheOnlyProblem's Endfield URP Lit Shader
// (SetSDFProperty.cs; Copyright (c) 2025 TheOnlyProblem, MIT, see NOTICE.md), moved from C#
// into the shader: the light is projected into the head's horizontal plane, and a frontal
// light gives 0 (whole face lit), a side light 0.5 (half the face lit), a back light 1
// (whole face shadowed). sideSign < 0 means the light comes from the character's left, so
// the SDF U must flip.
half HybridSDFThreshold(half3 lightDirWS, half3 headRight, half3 headForward, half3 headUp, out half sideSign)
{
    half3 lightProj = lightDirWS - dot(lightDirWS, headUp) * headUp;
    sideSign = dot(lightProj, headRight);
    half x = dot(lightProj, -headForward);
    // A light straight overhead has no horizontal projection; atan2(0,0) would return 0
    // and the formula would shadow the whole face. Treat it as frontal instead.
    half lengthSq = dot(lightProj, lightProj);
    half normalizedAngle = lengthSq < 1e-5h ? 0.0h : atan2(sideSign, x) / PI;
    return normalizedAngle > 0.0h ? 1.0h - normalizedAngle : normalizedAngle + 1.0h;
}

// ---------------------------------------------------------------------------
// Debug views (global int, uniform branch - no extra variants).
//   1 albedo   2 normal   3 NdotL   4 shadow attenuation   5 ramp
//   6 SDF      7 specular 8 rim     9 GI/probe            10 AO
//   11 character/outline mask (all character materials are 1 in this phase)
// ---------------------------------------------------------------------------
half3 HybridDebugColor(int view, half3 albedo, half3 normalWS, half ndotl, half shadowAtten,
                       half3 ramp, half3 specular, half3 rim, half3 gi, half ao, half sdf)
{
    switch (view)
    {
        case 1:  return albedo;
        case 2:  return normalWS * 0.5h + 0.5h;
        case 3:  return half3(ndotl, ndotl, ndotl);
        case 4:  return half3(shadowAtten, shadowAtten, shadowAtten);
        case 5:  return ramp;
        case 6:  return half3(sdf, sdf, sdf);
        case 7:  return specular;
        case 8:  return rim;
        case 9:  return gi;
        case 10: return half3(ao, ao, ao);
        case 11: return half3(0.0h, 0.0h, 0.0h); // Full-screen outline pass supplies this view.
        default: return half3(0.0h, 0.0h, 0.0h);
    }
}

#endif // HYBRID_CHARACTER_COMMON_INCLUDED
