#ifndef HYBRID_FACE_PASS_INCLUDED
#define HYBRID_FACE_PASS_INCLUDED

#include "HybridFaceInput.hlsl"
#include "HybridCharacterLighting.hlsl"

struct HybridFaceAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct HybridFaceVaryings
{
    float2 uv                       : TEXCOORD0;
    float3 positionWS               : TEXCOORD1;
    float3 normalWS                 : TEXCOORD2;
    float4 shadowCoord              : TEXCOORD3;
    half  fogFactor                 : TEXCOORD4;
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD9;
#endif
    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

HybridFaceVaryings HybridFaceVertex(HybridFaceAttributes input)
{
    HybridFaceVaryings output = (HybridFaceVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

    output.positionWS = positionInputs.positionWS;
    output.positionCS = positionInputs.positionCS;
    output.normalWS = normalInputs.normalWS;
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
    output.shadowCoord = TransformWorldToShadowCoord(positionInputs.positionWS);

    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(positionInputs.positionWS);
    OUTPUT_SH4(positionInputs.positionWS, normalInputs.normalWS, viewDirWS, output.vertexSH, output.probeOcclusion);

    return output;
}

// SDF face shadow. The map's bright side is the light side; flip U when the light comes
// from the other side, then threshold the map value against the light angle.
// Returns the ramp coordinate (1 = lit), scene shadow included. faceShade carries the geometric
// terms as a smooth multiplier on the direct light: the light falling behind the face's plane
// (the SDF only knows the horizontal angle) and the jaw/chin turning away (cm_M.g, where the SDF
// does not describe the shape). Fed through the ramp, as the reference does, partial values of
// these terms hovered at the ramp's white knee (~0.8) and drew a hard warm-to-white line across
// the cheek under a three-quarter key.
half HybridFaceSDFLit(float2 uv, half3 normalWS, half3 lightDirWS, half sceneShadow, half chinMask,
                      out half sdfValue, out half faceShade)
{
    half sideSign;
    half threshold = HybridSDFThreshold(lightDirWS, _HybridHeadRight.xyz, _HybridHeadForward.xyz, _HybridHeadUp.xyz, sideSign);
    threshold = saturate(threshold + _SDFThresholdOffset);

    float2 sdfUV = uv;
    if (sideSign < 0.0h)
        sdfUV.x = 1.0h - sdfUV.x;

    half4 sdfSample = SAMPLE_TEXTURE2D(_SDFMap, sampler_SDFMap, sdfUV);
    sdfValue = (sdfSample.r + sdfSample.g) * 0.5h;

    half softness = max(0.005h, _SDFSoftness);
    half sdfLit = smoothstep(threshold - softness, threshold + softness, sdfValue);

    half NoL = dot(normalWS, lightDirWS);
    half geoLit = smoothstep(-0.12h, 0.3h, NoL);
    half chinLit = smoothstep(0.1h, 0.65h, NoL * 0.5h + 0.5h);
    half weight = saturate(_SDFOn * _SDFWeight * _HybridSDFWeight);
    // Squaring the chin mask feathers its border; the 0.6 cap darkens the jaw toward the far side
    // instead of taking it to full shadow.
    faceShade = lerp(1.0h, geoLit * lerp(1.0h, chinLit, chinMask * chinMask * 0.6h), weight);

    half normalCoord = HybridRampCoordinate(NoL, sceneShadow);
    return lerp(normalCoord, saturate(min(sdfLit, sceneShadow)), weight);
}

half4 HybridFaceFragment(HybridFaceVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
    clip(albedoAlpha.a - _Cutoff);

    float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 normalWS = normalize(input.normalWS);

    // cm_M: R = face-front area (side tint), G = chin, A = rim mask.
    half4 faceMask = SAMPLE_TEXTURE2D(_FaceMaskMap, sampler_FaceMaskMap, input.uv);
    half sideMask = 1.0h - saturate(faceMask.r);
    half3 sideTint = lerp(1.0h, _SideTintColor.rgb, sideMask * _SideTintStrength);
    half3 albedo = albedoAlpha.rgb * sideTint;

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.normalizedScreenSpaceUV = screenUV;

    half4 shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);
    half3 bakedGI;
    HYBRID_SAMPLE_URP_GI(input, normalWS, viewDirWS, bakedGI, shadowMask)
    bakedGI = HybridCharacterProbeGI(bakedGI, input.positionWS, normalWS, viewDirWS);
    bakedGI *= _HybridIndirectScale;

    Light mainLight = HybridGetMainLight(input.positionWS, shadowMask);

    half NoL = dot(normalWS, mainLight.direction);
    half distanceAtten = mainLight.distanceAttenuation;
    half shadowAtten = HybridHeadShadow(mainLight);
    mainLight.shadowAttenuation = shadowAtten;
    half attenuation = distanceAtten * shadowAtten;
    half3 radiance = mainLight.color * distanceAtten;

    half sdfValue = 1.0h;
    half faceShade = 1.0h;
    half rampCoord;
#if defined(_SDF_ON)
    rampCoord = HybridFaceSDFLit(input.uv, normalWS, mainLight.direction, shadowAtten, faceMask.g, sdfValue, faceShade);
#else
    rampCoord = HybridRampCoordinate(NoL, shadowAtten);
#endif
    half litFromSDF = rampCoord * faceShade;
    half4 rampSample = SAMPLE_TEXTURE2D_LOD(_RampMap, sampler_RampMap, float2(rampCoord, 0.5h), 0);
    half rampWeight = saturate(_RampWeight * _HybridRampWeight);

    half3 f0 = 0.04h * _SpecColor.rgb;
    half3 directDiffuse;
    half3 directSpecular = 0.0h;
    half3 lipHighlight = 0.0h;
    half3 rim = 0.0h;
    half litMask = rampSample.a;
    half sdfWeight = saturate(_SDFOn * _SDFWeight * _HybridSDFWeight);
    // The ramp's A already gates the diffuse (the SDF drives its coordinate); gating it again by
    // the SDF squared every soft terminator into a dark band. The gate stays on the specular.
    half faceGate = lerp(1.0h, saturate(litFromSDF), sdfWeight);

    if (_HybridShadingMode == 1)
    {
        BRDFData brdfData;
        half alpha = 1.0h;
        InitializeBRDFData(albedo, 0.0h, _SpecColor.rgb, _Smoothness, alpha, brdfData);
        directDiffuse = LightingPhysicallyBased(brdfData, mainLight, normalWS, viewDirWS);
        litMask = saturate(NoL) * shadowAtten;
    }
    else
    {
        half3 rampDiffuse = albedo * rampSample.rgb * (rampSample.a * faceShade);
        // The Lambert reference path has no ramp, so it takes the SDF through the gate instead.
        half3 lambertDiffuse = albedo * lerp(saturate(dot(normalWS, mainLight.direction)),
                                             saturate(dot(normalWS, mainLight.direction) * 0.5h + 0.5h), faceMask.g)
                               * shadowAtten * faceGate;
#if defined(_RAMP_ON)
        directDiffuse = lerp(lambertDiffuse, rampDiffuse, rampWeight) * radiance;
#else
        directDiffuse = lambertDiffuse * radiance;
#endif

#if defined(_SPEC_ON)
        // A sphere normal avoids the flat-face highlight the mesh normal would give. Unshaped GGX:
        // a smoothstep clamp on it turns it into a white disc on the cheek.
        half3 sphereNormal = normalize(input.positionWS - _HybridHeadPosition.xyz);
        half3 specNormal = normalize(lerp(normalWS, sphereNormal, 0.65h * (1.0h - faceMask.g)));
        directSpecular = HybridGGXSpecular(f0, _Smoothness, specNormal, mainLight.direction, viewDirWS,
                                           radiance, shadowAtten, _SpecIntensity * _HybridSpecWeight);
#endif

        // face_03_hl_M paints the nose-tip and lip highlights. They slide a little with the view, as
        // in the reference, and follow the key light. Light-driven highlights drop the 40% floor in
        // shadow: a lit nose tip on a shadowed face reads as painted on.
        half lipOffset = dot(viewDirWS, _HybridHeadRight.xyz) * 0.03h;
        half lipMask = SAMPLE_TEXTURE2D(_LipMaskMap, sampler_LipMaskMap, input.uv + half2(lipOffset, 0.0h)).r;
        half lipFloor = 0.4h * (1.0h - (half)saturate(_HybridHighlightLightDrive));
        lipHighlight = _LipColor.rgb * radiance * (lipMask * _LipIntensity * max(lipFloor, saturate(litFromSDF)));

#if defined(_RIM_ON)
        half frontFacing = smoothstep(0.15h, 0.85h, dot(viewDirWS, _HybridHeadForward.xyz));
        rim = HybridRim(normalWS, viewDirWS, mainLight.direction, HybridKeyLightTint(mainLight), _RimColor.rgb, _RimPower,
                        _RimIntensity * _HybridRimWeight, faceMask.a * _FaceRimStrength * frontFacing);
        rim += HybridIntegrationRim(normalWS, viewDirWS, mainLight, bakedGI, _RimPower,
                                    _HybridRimWeight * faceMask.a * _FaceRimStrength);
        rim += HybridScreenSpaceRim(normalWS, input.positionCS, screenUV, mainLight, bakedGI, albedo, _HybridRimWeight * 0.5h);
#endif

        // Main light only: the additional lights are accumulated below and must keep
        // their own response on the shadowed side of the face.
        directSpecular *= faceGate;
    }

    HybridAccumulateAdditionalLights(inputData, shadowMask, TEXTURE2D_ARGS(_RampMap, sampler_RampMap),
                                     albedo, albedo, 0.0h, _SpecColor.rgb, _Smoothness, normalWS, viewDirWS,
                                     rampWeight, _SpecIntensity * _HybridSpecWeight, directDiffuse, directSpecular);

    half shadowAmount = lerp(1.0h - saturate(litMask), 1.0h - saturate(litFromSDF), sdfWeight);
    half3 shadowTint = HybridShadowTint(shadowAmount, _ShadowColor.rgb, _ShadowColorStrength);

    half rawSSAO = HybridSampleSSAO(screenUV);
    half ssao = HybridApplySSAO(rawSSAO, _SSAOStrength);
    half ao = ssao;

    half3 indirect = HybridIndirectLighting(albedo, 0.0h, _SpecColor.rgb, _Smoothness, normalWS, viewDirWS,
                                            input.positionWS, screenUV, bakedGI, ao, shadowTint,
                                            HybridReflectionLightScale(saturate(litFromSDF)))
                   * (_HybridShadingMode == 0 ? _GIIntensity : 1.0h);

    half3 color;
    if (_HybridShadingMode == 1)
        color = directDiffuse + indirect;
    else
        color = (directDiffuse + directSpecular + lipHighlight + rim) * _DirectLightingIntensity + indirect
              + HybridShadowFill(albedo, mainLight, shadowAmount, _ShadowColor.rgb) * ao;
    color = HybridCharacterTone(color);

    if (_HybridDebugView != 0)
        color = HybridDebugColor(_HybridDebugView, albedo, normalWS, NoL, shadowAtten, rampSample.rgb,
                                 directSpecular, rim, bakedGI, ao, litFromSDF);

    color = MixFog(color, input.fogFactor);
    return half4(color, 1.0h);
}

#endif // HYBRID_FACE_PASS_INCLUDED
