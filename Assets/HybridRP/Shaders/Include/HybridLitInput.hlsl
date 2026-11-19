#ifndef HYBRID_LIT_INPUT_INCLUDED
#define HYBRID_LIT_INPUT_INCLUDED

// Properties for HybridRP/Character/Lit (skin + cloth). Keep every property in the
// UnityPerMaterial cbuffer and identical in every pass - that is what makes the shader
// SRP Batcher compatible.

#include "HybridCharacterCommon.hlsl"

TEXTURE2D(_PMap);
SAMPLER(sampler_PMap);
TEXTURE2D(_RampMap);
SAMPLER(sampler_RampMap);

CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
half4 _BaseColor;
half4 _ShadowColor;
half4 _SpecColor;
half4 _RimColor;
half _Cutoff;
half _Cull;
half _BumpScale;
half _UsePMap;
half _Metallic;
half _Smoothness;
half _SmoothnessScale;
half _OcclusionStrength;
half _RampWeight;
half _ShadowColorStrength;
half _SpecIntensity;
half _RimPower;
half _RimIntensity;
half _SSAOStrength;
half _DirectLightingIntensity;
half _GIIntensity;
half _SelfShadowOffset;
half _MetalDiffuse;
half _RampOn;
half _ShadowColorOn;
half _SpecOn;
half _RimOn;
CBUFFER_END

#endif // HYBRID_LIT_INPUT_INCLUDED
