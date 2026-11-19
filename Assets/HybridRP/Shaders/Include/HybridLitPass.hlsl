#ifndef HYBRID_LIT_PASS_INCLUDED
#define HYBRID_LIT_PASS_INCLUDED

#include "HybridLitInput.hlsl"
#include "HybridCharacterLighting.hlsl"

struct HybridLitAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct HybridLitVaryings
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

HybridLitVaryings HybridLitVertex(HybridLitAttributes input)
{
    HybridLitVaryings output = (HybridLitVaryings)0;
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

// Surface data shared by the Hybrid and PBR paths.
struct HybridLitSurface
{
    half3 albedo;
    half alpha;
    half3 normalWS;
    half metallic;
    half smoothness;
    half occlusion;
};

HybridLitSurface SampleHybridLitSurface(float2 uv, half3 normalWS, half4 tangentWS)
{
    HybridLitSurface s;
    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
    s.albedo = albedoAlpha.rgb;
    s.alpha = albedoAlpha.a;

    half4 p = SAMPLE_TEXTURE2D(_PMap, sampler_PMap, uv);
    half usePMap = saturate(_UsePMap);
    s.metallic = saturate(lerp(0.0h, p.r, usePMap) * _Metallic);
    s.smoothness = saturate(lerp(_Smoothness, p.a, usePMap) * _SmoothnessScale);
    s.occlusion = lerp(1.0h, p.b, usePMap * _OcclusionStrength);

    half4 normalSample = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv);
    s.normalWS = HybridNormalFromMap(normalSample, _BumpScale, normalWS, tangentWS);
    return s;
}

half4 HybridLitFragment(HybridLitVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    HybridLitSurface surface = SampleHybridLitSurface(input.uv, input.normalWS, input.tangentWS);
    clip(surface.alpha - _Cutoff);

    float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.normalizedScreenSpaceUV = screenUV;

    // GI: APV on the dynamic character, with a plain SH fallback. The LowAmbient preset
    // scales it through _HybridIndirectScale because baked probes can't be rescaled.
    half4 shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);
    half3 bakedGI;
    HYBRID_SAMPLE_URP_GI(input, surface.normalWS, viewDirWS, bakedGI, shadowMask)
    bakedGI = HybridCharacterProbeGI(bakedGI, input.positionWS, surface.normalWS, viewDirWS);
    bakedGI *= _HybridIndirectScale;

    Light mainLight = HybridGetMainLight(input.positionWS, shadowMask);

    half NoL = dot(surface.normalWS, mainLight.direction);
    half distanceAtten = mainLight.distanceAttenuation;
    half shadowAtten = HybridSmoothedShadow(mainLight, input.positionWS, _SelfShadowOffset);
    mainLight.shadowAttenuation = shadowAtten;
    half attenuation = distanceAtten * shadowAtten;
    half3 radiance = mainLight.color * distanceAtten;

    half4 rampSample = SAMPLE_TEXTURE2D_LOD(_RampMap, sampler_RampMap, float2(HybridRampCoordinate(NoL, attenuation), 0.5h), 0);
    half3 f0 = 0.04h * _SpecColor.rgb + surface.albedo * surface.metallic;
    half rampWeight = saturate(_RampWeight * _HybridRampWeight);

    half3 directDiffuse;
    half3 directSpecular = 0.0h;
    half3 rim = 0.0h;
    half3 ramp = rampSample.rgb;
    half litMask = rampSample.a;
    // A metal has no diffuse response (its colour is the specular F0). Feeding the full albedo to
    // the ramp made every metallic part a flat, bright cream; the references show dark metal whose
    // shape comes from its reflections. With no diffuse at all, though, the baked probe reflection was
    // the metal's whole look and ignored the sun, so _MetalDiffuse keeps a share of the lit ramp.
    half3 diffuseAlbedo = surface.albedo * (1.0h - surface.metallic * (1.0h - _MetalDiffuse));

    if (_HybridShadingMode == 1)
    {
        // PBR reference path: URP's own physically based direct lighting, same GI/SSAO.
        BRDFData brdfData;
        half alpha = 1.0h;
        InitializeBRDFData(surface.albedo, surface.metallic, _SpecColor.rgb, surface.smoothness, alpha, brdfData);
        directDiffuse = LightingPhysicallyBased(brdfData, mainLight, surface.normalWS, viewDirWS);
        litMask = saturate(NoL) * shadowAtten;
        ramp = 1.0h;
    }
    else
    {
        half3 rampDiffuse = diffuseAlbedo * rampSample.rgb * rampSample.a;
        half3 lambertDiffuse = diffuseAlbedo * saturate(NoL) * shadowAtten;
#if defined(_RAMP_ON)
        directDiffuse = lerp(lambertDiffuse, rampDiffuse, rampWeight) * radiance;
#else
        directDiffuse = lambertDiffuse * radiance;
#endif

#if defined(_SPEC_ON)
        directSpecular = HybridGGXSpecular(f0, surface.smoothness, surface.normalWS, mainLight.direction,
                                            viewDirWS, radiance, shadowAtten, _SpecIntensity * _HybridSpecWeight);
#endif
#if defined(_RIM_ON)
        rim = HybridRim(surface.normalWS, viewDirWS, mainLight.direction, HybridKeyLightTint(mainLight), _RimColor.rgb,
                        _RimPower, _RimIntensity * _HybridRimWeight, 1.0h);
        rim += HybridIntegrationRim(surface.normalWS, viewDirWS, mainLight, bakedGI, _RimPower, _HybridRimWeight);
        rim += HybridScreenSpaceRim(normalize(input.normalWS), input.positionCS, screenUV, mainLight, bakedGI,
                                    surface.albedo, _HybridRimWeight);
#endif
    }

    HybridAccumulateAdditionalLights(inputData, shadowMask, TEXTURE2D_ARGS(_RampMap, sampler_RampMap),
                                     surface.albedo, diffuseAlbedo, surface.metallic, _SpecColor.rgb, surface.smoothness,
                                     surface.normalWS, viewDirWS, rampWeight, _SpecIntensity * _HybridSpecWeight,
                                     directDiffuse, directSpecular);

    half3 shadowTint = HybridShadowTint(1.0h - saturate(litMask), _ShadowColor.rgb, _ShadowColorStrength);

    half rawSSAO = HybridSampleSSAO(screenUV);
    half ssao = HybridApplySSAO(rawSSAO, _SSAOStrength);
    half ao = surface.occlusion * ssao;

    half3 indirect = HybridIndirectLighting(surface.albedo, surface.metallic, _SpecColor.rgb, surface.smoothness,
                                            surface.normalWS, viewDirWS, input.positionWS, screenUV, bakedGI, ao, shadowTint,
                                            HybridReflectionLightScale(saturate(NoL * 0.5h + 0.5h) * shadowAtten))
                   * (_HybridShadingMode == 0 ? _GIIntensity : 1.0h);

    half3 color;
    if (_HybridShadingMode == 1)
        color = directDiffuse + indirect;
    else
        color = (directDiffuse + directSpecular + rim) * _DirectLightingIntensity + indirect
              + HybridShadowFill(diffuseAlbedo, mainLight, 1.0h - saturate(litMask), _ShadowColor.rgb) * ao;
    color = HybridCharacterTone(color);

    if (_HybridDebugView != 0)
        color = HybridDebugColor(_HybridDebugView, surface.albedo, surface.normalWS, NoL, shadowAtten,
                                 ramp, directSpecular, rim, bakedGI, ao, 1.0h);

    color = MixFog(color, input.fogFactor);
    return half4(color, 1.0h);
}

#endif // HYBRID_LIT_PASS_INCLUDED
