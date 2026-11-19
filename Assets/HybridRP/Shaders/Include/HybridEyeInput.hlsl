#ifndef HYBRID_EYE_INPUT_INCLUDED
#define HYBRID_EYE_INPUT_INCLUDED

// Properties for HybridRP/Character/Eye (iris, sclera, 目HL). No parallax (out of scope).

#include "HybridCharacterCommon.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
half4 _BaseColor;
half4 _EmissionColor;
half4 _IrisShadeColor;
half _EmissionStrength;
half _Smoothness;
half _SpecIntensity;
half _SSAOStrength;
half _AmbientStrength;
half _Saturation;
half _IrisShadeStart;
half _IrisShadeEnd;
half _Cutoff;
half _Cull;
CBUFFER_END

#endif // HYBRID_EYE_INPUT_INCLUDED
