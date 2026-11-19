#ifndef HYBRID_CHARACTER_DEPTH_PASSES_INCLUDED
#define HYBRID_CHARACTER_DEPTH_PASSES_INCLUDED

// ShadowCaster / DepthOnly / DepthNormals for every Hybrid character shader.
//
// All three passes use the same _BaseMap / _BaseColor / _Cutoff names and the same
// UnityPerMaterial layout as the forward pass, so the material stays SRP Batcher
// compatible. DepthNormals writes world-space normals, which is what URP's SSAO and the
// Phase 4 screen-space outline read.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

float3 _LightDirection;
float3 _LightPosition;

struct HybridDepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct HybridDepthVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

half HybridBaseAlpha(float2 uv)
{
    half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    return albedo.a * _BaseColor.a;
}

void HybridAlphaClip(float2 uv)
{
    clip(HybridBaseAlpha(uv) - _Cutoff);
}

// ---------------------------------------------------------------------------
// ShadowCaster
// ---------------------------------------------------------------------------
float4 HybridGetShadowPositionHClip(HybridDepthAttributes input)
{
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    return ApplyShadowClamping(positionCS);
}

HybridDepthVaryings HybridShadowVertex(HybridDepthAttributes input)
{
    HybridDepthVaryings output = (HybridDepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionCS = HybridGetShadowPositionHClip(input);
    return output;
}

half4 HybridShadowFragment(HybridDepthVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    HybridAlphaClip(input.uv);
    return 0;
}

// ---------------------------------------------------------------------------
// DepthOnly
// ---------------------------------------------------------------------------
HybridDepthVaryings HybridDepthOnlyVertex(HybridDepthAttributes input)
{
    HybridDepthVaryings output = (HybridDepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

half4 HybridDepthOnlyFragment(HybridDepthVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    HybridAlphaClip(input.uv);
    return 0;
}

// ---------------------------------------------------------------------------
// DepthNormals
// ---------------------------------------------------------------------------
HybridDepthVaryings HybridDepthNormalsVertex(HybridDepthAttributes input)
{
    HybridDepthVaryings output = (HybridDepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.normalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(input.normalOS));
    return output;
}

half4 HybridDepthNormalsFragment(HybridDepthVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    HybridAlphaClip(input.uv);
    return half4(NormalizeNormalPerPixel(input.normalWS), 0.0h);
}

// ---------------------------------------------------------------------------
// Visible character coverage; original material alpha/culling, no hull expansion.
// Sampling resolved depth keeps this single-sample R8 pass independent of MSAA.
// ---------------------------------------------------------------------------
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

half4 HybridOutlineMaskFragment(HybridDepthVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    HybridAlphaClip(input.uv);
    float2 uv = GetNormalizedScreenSpaceUV(input.positionCS);
    float rawSceneDepth = SampleSceneDepth(uv);
    float sceneDepth = IsPerspectiveProjection() ? LinearEyeDepth(rawSceneDepth, _ZBufferParams) : LinearDepthToEyeDepth(rawSceneDepth);
    float fragmentDepth = IsPerspectiveProjection() ? LinearEyeDepth(input.positionCS.z, _ZBufferParams) : LinearDepthToEyeDepth(input.positionCS.z);
    // A small relative tolerance covers depth-copy precision without leaking through walls.
    clip(sceneDepth + max(0.001, sceneDepth * 0.0001) - fragmentDepth);
    return 1.0h;
}

#endif // HYBRID_CHARACTER_DEPTH_PASSES_INCLUDED
