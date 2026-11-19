#ifndef HYBRID_HAIR_PASS_INCLUDED
#define HYBRID_HAIR_PASS_INCLUDED

#include "HybridHairInput.hlsl"
#include "HybridCharacterLighting.hlsl"

struct HybridHairAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct HybridHairVaryings
{
    float2 uv                       : TEXCOORD0;
    float3 positionWS               : TEXCOORD1;
    float3 normalWS                 : TEXCOORD2;
    half4 tangentWS                 : TEXCOORD3;
    float4 shadowCoord              : TEXCOORD4;
    half  fogFactor                 : TEXCOORD5;
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD9;
#endif
    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

HybridHairVaryings HybridHairVertex(HybridHairAttributes input)
{
    HybridHairVaryings output = (HybridHairVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    output.positionWS = positionInputs.positionWS;
    output.positionCS = positionInputs.positionCS;
    output.normalWS = normalInputs.normalWS;
    output.tangentWS = half4(normalInputs.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
    output.shadowCoord = TransformWorldToShadowCoord(positionInputs.positionWS);

    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(positionInputs.positionWS);
    OUTPUT_SH4(positionInputs.positionWS, normalInputs.normalWS, viewDirWS, output.vertexSH, output.probeOcclusion);

    return output;
}

half4 HybridHairFragment(HybridHairVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
    clip(albedoAlpha.a - _Cutoff);
    albedoAlpha.rgb = HybridHairAccentSaturation(albedoAlpha.rgb, _HairAccentSaturation);

    half4 hairP = SAMPLE_TEXTURE2D(_PMap, sampler_PMap, input.uv);
    half aoMap = lerp(1.0h, hairP.b, _OcclusionStrength);

    float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 normalWS = normalize(input.normalWS);

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
    half NoV = saturate(dot(normalWS, viewDirWS));
    half distanceAtten = mainLight.distanceAttenuation;
    half shadowAtten = HybridSmoothedShadow(mainLight, input.positionWS, 0.2h);
    mainLight.shadowAttenuation = shadowAtten;
    half attenuation = distanceAtten * shadowAtten;
    half3 radiance = mainLight.color * distanceAtten;

    half4 rampSample = SAMPLE_TEXTURE2D_LOD(_RampMap, sampler_RampMap, float2(HybridRampCoordinate(NoL, attenuation), 0.5h), 0);
    half rampWeight = saturate(_RampWeight * _HybridRampWeight);
    half3 f0 = 0.04h * _SpecColor.rgb;

    half3 directDiffuse;
    half3 directSpecular = 0.0h;
    half3 hairHighlight = 0.0h;
    half3 rim = 0.0h;
    half litMask = rampSample.a;

    if (_HybridShadingMode == 1)
    {
        BRDFData brdfData;
        half alpha = 1.0h;
        InitializeBRDFData(albedoAlpha.rgb, 0.0h, _SpecColor.rgb, _Smoothness, alpha, brdfData);
        directDiffuse = LightingPhysicallyBased(brdfData, mainLight, normalWS, viewDirWS);
        litMask = saturate(NoL) * shadowAtten;
    }
    else
    {
        half3 rampDiffuse = albedoAlpha.rgb * rampSample.rgb * rampSample.a;
        half3 lambertDiffuse = albedoAlpha.rgb * saturate(NoL) * shadowAtten;
#if defined(_RAMP_ON)
        directDiffuse = lerp(lambertDiffuse, rampDiffuse, rampWeight) * radiance;
#else
        directDiffuse = lambertDiffuse * radiance;
#endif

#if defined(_SPEC_ON)
        // Plain GGX, unshaped: a smoothstep clamp on it saturates any lobe to a white
        // sheet across the crown. The strand highlight below carries the hair's look; this only
        // adds a faint gloss where the band is not.
        directSpecular = HybridGGXSpecular(f0, _Smoothness, normalWS, mainLight.direction, viewDirWS,
                                           radiance, shadowAtten, _SpecIntensity * _HybridSpecWeight);
#endif

#if defined(_HAIRHL_ON)
        // _HN smooths the band across the low-poly strands, hairst_01_ST.r breaks it up per strand,
        // _P.g is the authored strand mask and _P.a the region intensity.
        half4 hairNormalSample = SAMPLE_TEXTURE2D(_HairNormalMap, sampler_HairNormalMap, input.uv);
        half3 bandNormal = HybridNormalFromMap(hairNormalSample, _BumpScale, normalWS, input.tangentWS);
        half shiftNoise = SAMPLE_TEXTURE2D(_HairShiftMap, sampler_HairShiftMap, input.uv).r;
        half viewFalloff = pow(saturate(NoV), max(0.5h, _HairHLViewPower));
        half strandMask = lerp(1.0h, hairP.g, saturate(_HairHLMaskStrength));
        hairHighlight = HybridHairStrandHighlight(albedoAlpha.rgb, bandNormal, viewDirWS, mainLight, shiftNoise,
                                                  strandMask * viewFalloff, hairP.a,
                                                  _HairHLColor.rgb, _HairHLIntensity * _HybridHairHLWeight, _HairHLPower,
                                                  _HairHLShift, _HairHLWidth, _HairHLAlbedoTint,
                                                  _HairSheenIntensity * _HybridHairHLWeight, _HairSheenPower);
#endif

#if defined(_RIM_ON)
        rim = HybridRim(normalWS, viewDirWS, mainLight.direction, HybridKeyLightTint(mainLight), _RimColor.rgb, _RimPower,
                        _RimIntensity * _HybridRimWeight, 1.0h);
        rim += HybridIntegrationRim(normalWS, viewDirWS, mainLight, bakedGI, _RimPower, _HybridRimWeight);
        rim += HybridScreenSpaceRim(normalWS, input.positionCS, screenUV, mainLight, bakedGI, albedoAlpha.rgb, _HybridRimWeight);
#endif
    }

    HybridAccumulateAdditionalLights(inputData, shadowMask, TEXTURE2D_ARGS(_RampMap, sampler_RampMap),
                                     albedoAlpha.rgb, albedoAlpha.rgb, 0.0h, _SpecColor.rgb, _Smoothness, normalWS, viewDirWS,
                                     rampWeight, _SpecIntensity * _HybridSpecWeight, directDiffuse, directSpecular);

    half3 shadowTint = HybridShadowTint(1.0h - saturate(litMask), _ShadowColor.rgb, _ShadowColorStrength);

    half rawSSAO = HybridSampleSSAO(screenUV);
    half ssao = HybridApplySSAO(rawSSAO, _SSAOStrength);
    half ao = aoMap * ssao;

    half3 indirect = HybridIndirectLighting(albedoAlpha.rgb, 0.0h, _SpecColor.rgb, _Smoothness, normalWS, viewDirWS,
                                            input.positionWS, screenUV, bakedGI, ao, shadowTint,
                                            HybridReflectionLightScale(saturate(NoL * 0.5h + 0.5h) * shadowAtten))
                   * (_HybridShadingMode == 0 ? _GIIntensity : 1.0h);

    half3 color;
    if (_HybridShadingMode == 1)
        color = directDiffuse + indirect;
    else
        color = (directDiffuse + directSpecular + hairHighlight + rim) * _DirectLightingIntensity + indirect
              + HybridShadowFill(albedoAlpha.rgb, mainLight, 1.0h - saturate(litMask), _ShadowColor.rgb) * ao;
    color = HybridCharacterTone(color);

    if (_HybridDebugView != 0)
        color = HybridDebugColor(_HybridDebugView, albedoAlpha.rgb, normalWS, NoL, shadowAtten, rampSample.rgb,
                                 directSpecular + hairHighlight, rim, bakedGI, ao, litMask);

    color = MixFog(color, input.fogFactor);
    return half4(color, 1.0h);
}

#endif // HYBRID_HAIR_PASS_INCLUDED
