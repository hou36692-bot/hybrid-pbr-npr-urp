#ifndef HYBRID_HAIR_INPUT_INCLUDED
#define HYBRID_HAIR_INPUT_INCLUDED

// Properties for HybridRP/Character/Hair. _HN is an XY-only "highlight normal" used only
// for the band; hairst_01_ST.r shifts the band along the strand; _P.g is the band mask
// and _P.a its intensity (as authored in the source asset's hair textures).

#include "HybridCharacterCommon.hlsl"

TEXTURE2D(_RampMap);
SAMPLER(sampler_RampMap);
TEXTURE2D(_PMap);
SAMPLER(sampler_PMap);
TEXTURE2D(_HairNormalMap);
SAMPLER(sampler_HairNormalMap);
TEXTURE2D(_HairShiftMap);
SAMPLER(sampler_HairShiftMap);

CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
half4 _BaseColor;
half4 _ShadowColor;
half4 _SpecColor;
half4 _RimColor;
half4 _HairHLColor;
half _Cutoff;
half _Cull;
half _BumpScale;
half _Smoothness;
half _OcclusionStrength;
half _RampWeight;
half _ShadowColorStrength;
half _SpecIntensity;
half _RimPower;
half _RimIntensity;
half _SSAOStrength;
half _DirectLightingIntensity;
half _GIIntensity;
half _HairHLIntensity;
half _HairHLPower;
half _HairHLWidth;
half _HairHLShift;
half _HairHLViewPower;
half _HairHLMaskStrength;
half _HairHLAlbedoTint;
half _HairSheenIntensity;
half _HairSheenPower;
half _HairAccentSaturation;
half _RampOn;
half _ShadowColorOn;
half _SpecOn;
half _RimOn;
half _HairHLOn;
CBUFFER_END

#endif // HYBRID_HAIR_INPUT_INCLUDED
