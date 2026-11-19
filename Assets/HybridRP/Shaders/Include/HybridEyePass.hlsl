#ifndef HYBRID_EYE_PASS_INCLUDED
#define HYBRID_EYE_PASS_INCLUDED

#include "HybridEyeInput.hlsl"
#include "HybridCharacterLighting.hlsl"

struct HybridEyeAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct HybridEyeVaryings
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

HybridEyeVaryings HybridEyeVertex(HybridEyeAttributes input)
{
    HybridEyeVaryings output = (HybridEyeVaryings)0;
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

half4 HybridEyeFragment(HybridEyeVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 albedoAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
    clip(albedoAlpha.a * _BaseColor.a - _Cutoff);
    // The references' iris is a muted olive-gold; the texture's teal and orange rings at full
    // chroma read as a glowing yellow-green once the character is exposed correctly. The texture is
    // desaturated first, so _BaseColor tints what is left instead of being desaturated with it.
    albedoAlpha.rgb = lerp(dot(albedoAlpha.rgb, half3(0.2126h, 0.7152h, 0.0722h)), albedoAlpha.rgb, _Saturation)
                    * _BaseColor.rgb;
    // In the references the upper part of the iris sits in the lid's shadow (a dark brown band above
    // the pupil). The fan texture is lit evenly, so the band is painted in its UV space, where
    // v = 1 is the top of the iris. The end is kept above the start: smoothstep divides by their gap.
    albedoAlpha.rgb *= lerp(1.0h, _IrisShadeColor.rgb,
                            smoothstep(_IrisShadeStart, max(_IrisShadeEnd, _IrisShadeStart + 0.001h), (half)input.uv.y));

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
    half shadowAtten = HybridHeadShadow(mainLight);
    mainLight.shadowAttenuation = shadowAtten;
    half distanceAtten = mainLight.distanceAttenuation;
    // Eyes stay readable in shadow: the ambient term is boosted and the direct term is
    // deliberately soft (anime eyes are an art-directed material, not skin).
    half directLit = saturate(NoL * 0.5h + 0.5h) * shadowAtten;
    half rawSSAO = HybridSampleSSAO(screenUV);
    half ao = HybridApplySSAO(rawSSAO, _SSAOStrength);

    half3 direct = albedoAlpha.rgb * mainLight.color * (distanceAtten * directLit);
    half3 ambient = albedoAlpha.rgb * bakedGI * (ao * _AmbientStrength);
    half3 specular = HybridGGXSpecular(half3(0.04h, 0.04h, 0.04h), _Smoothness, normalWS, mainLight.direction, viewDirWS,
                                       mainLight.color * distanceAtten, shadowAtten, _SpecIntensity * _HybridSpecWeight);
    half3 emission = albedoAlpha.rgb * _EmissionColor.rgb * _EmissionStrength;

    // Same shadow fill as the skin, untinted: the iris keeps its own hue in shadow instead of
    // collapsing to the probe light, which under an eave left the eyes black.
    half3 fill = HybridShadowFill(albedoAlpha.rgb, mainLight, 1.0h - directLit, half3(1.0h, 1.0h, 1.0h));

    half3 color = HybridCharacterTone(direct + ambient + fill + specular + emission);

    if (_HybridDebugView != 0)
        color = HybridDebugColor(_HybridDebugView, albedoAlpha.rgb, normalWS, NoL, shadowAtten,
                                 half3(1.0h, 1.0h, 1.0h), specular, half3(0.0h, 0.0h, 0.0h), bakedGI, ao, 1.0h);

    color = MixFog(color, input.fogFactor);
    return half4(color, 1.0h);
}

#endif // HYBRID_EYE_PASS_INCLUDED
