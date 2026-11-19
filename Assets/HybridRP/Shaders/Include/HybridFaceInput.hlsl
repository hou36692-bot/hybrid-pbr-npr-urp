#ifndef HYBRID_FACE_INPUT_INCLUDED
#define HYBRID_FACE_INPUT_INCLUDED

// Properties for HybridRP/Character/Face. The face has no normal map; its shape comes
// from the SDF shadow map and the face masks (cm_M).

#include "HybridCharacterCommon.hlsl"

TEXTURE2D(_RampMap);
SAMPLER(sampler_RampMap);
TEXTURE2D(_SDFMap);
SAMPLER(sampler_SDFMap);
TEXTURE2D(_FaceMaskMap);
SAMPLER(sampler_FaceMaskMap);
TEXTURE2D(_LipMaskMap);
SAMPLER(sampler_LipMaskMap);

CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
half4 _BaseColor;
half4 _ShadowColor;
half4 _SpecColor;
half4 _RimColor;
half4 _SideTintColor;
half4 _LipColor;
half _Cutoff;
half _Cull;
half _RampWeight;
half _ShadowColorStrength;
half _SpecIntensity;
half _Smoothness;
half _RimPower;
half _RimIntensity;
half _FaceRimStrength;
half _SideTintStrength;
half _LipIntensity;
half _SSAOStrength;
half _DirectLightingIntensity;
half _GIIntensity;
half _SDFWeight;
half _SDFSoftness;
half _SDFThresholdOffset;
half _RampOn;
half _ShadowColorOn;
half _SpecOn;
half _RimOn;
half _SDFOn;
CBUFFER_END

#endif // HYBRID_FACE_INPUT_INCLUDED
