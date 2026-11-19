#ifndef HYBRID_GTAO_COMMON_INCLUDED
#define HYBRID_GTAO_COMMON_INCLUDED

// Ground-truth ambient occlusion, ported for Unity 6000.0 / URP 17.0.4 from IllusionRP's
// GroundTruthAmbientOcclusion (Unity Companion License, see NOTICE.md), which in
// turn follows HDRP's GTAO. Kept: the half-resolution horizon search on a packed min-depth
// pyramid, the two-direction spatial rotation, AO/depth packed into one R32_SFloat so the
// bilateral upscale can gather 2x2 neighbours, and the geometry-aware upsample (MiniEngine,
// MIT, notice in HybridGTAOBlurUpsample.compute). Dropped: stencil NO_AO (no such usage in
// this project), full-resolution tracing, temporal accumulation, XR arrays, world-scale
// remapping (our world is 1 unit = 1 m) and the raster blur fallbacks (D3D11 compute only).

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
#include "HybridGTAOConstants.hlsl"

// Packed min-depth atlas (mip 0 = the camera depth, later mips packed below/right).
TEXTURE2D_X(_DepthPyramid);

#define _AOThickness                        _AOParams0.x
#define _AOFOVCorrection                    _AOParams0.y
#define _AORadius                           _AOParams0.z
#define _AOStepCount                        (uint)_AOParams0.w

#define _AOIntensity                        _AOParams1.x
#define _AOInvRadiusSq                      _AOParams1.y

#define _AODirectionCount                   _AOParams2.x
#define DOWNSAMPLE                          _AOParams2.y
#define _AOInvStepCountPlusOne              _AOParams2.z
#define _AOMaxRadiusInPixels                (int)_AOParams2.w

#define _BlurTolerance                      _AOParams3.x
#define _UpsampleTolerance                  _AOParams3.y
#define _NoiseFilterStrength                _AOParams3.z
#define _StepSize                           _AOParams3.w

#define _AOSpatialBilateralAggressiveness   _AOParams4.w
#define _FirstDepthMipOffset                _FirstTwoDepthMipOffsets.xy
#define _SecondDepthMipOffset               _FirstTwoDepthMipOffsets.zw

// The bilateral filter is geometry-aware when the two normals agree.
static const half kGeometryCoeff = half(0.8);

static const half HALF_ZERO = half(0.0);
static const half HALF_ONE  = half(1.0);
static const half HALF_TWO  = half(2.0);

half CompareNormal(half3 d1, half3 d2)
{
    return smoothstep(kGeometryCoeff, half(1.0), dot(d1, d2));
}

uint2 GetScreenSpacePosition(float2 uv)
{
    return uint2(uv * _ScreenParams.xy * DOWNSAMPLE);
}

// Camera normals are full resolution; GTAO runs at half resolution.
half3 SampleNormalAtFullRes(uint2 pixelCoord)
{
    return half3(LoadSceneNormals(pixelCoord));
}

float IntegrateArcCosWeighted(float horizon1, float horizon2, float n, float cosN)
{
    float h1 = horizon1 * 2.0;
    float h2 = horizon2 * 2.0;
    float sinN = sin(n);
    return 0.25 * ((-cos(h1 - n) + cosN + h1 * sinN) + (-cos(h2 - n) + cosN + h2 * sinN));
}

float GTAOFastAcos(float x)
{
    float outVal = -0.156583 * abs(x) + HALF_PI;
    outVal *= sqrt(1.0 - abs(x));
    return x >= 0 ? outVal : PI - outVal;
}

// Central depth comes from the first pyramid mip (half resolution), neighbours from the
// second for the later, wider steps. Both offsets are atlas coordinates.
float GetDepthForCentral(float2 positionSS)
{
    return LOAD_TEXTURE2D_X(_DepthPyramid, _FirstDepthMipOffset + (uint2)positionSS.xy).r;
}

float GetDepthSample(float2 positionSS, bool lowerRes)
{
    float depth;
    if (lowerRes)
        depth = LOAD_TEXTURE2D_X(_DepthPyramid, _SecondDepthMipOffset + (uint2)positionSS.xy / 2).r;
    else
        depth = LOAD_TEXTURE2D_X(_DepthPyramid, _FirstDepthMipOffset + (uint2)positionSS.xy).r;
    return depth;
}

// Ray start offset inside the first step, rotated per pixel so neighbouring pixels cover
// different parts of a step; no temporal accumulation, so it is purely spatial.
float GetOffset(uint2 positionSS)
{
    return frac(0.25 * ((positionSS.y - positionSS.x) & 0x3));
}

float2 GetDirection(uint2 positionSS, int offset)
{
    float noise = InterleavedGradientNoise(positionSS.xy, 0);
    float rotations[] = { 60.0, 300.0, 180.0, 240.0, 120.0, 0.0 };
    noise += rotations[offset] / 360.0;
    noise *= PI;
    return float2(cos(noise), sin(noise));
}

float3 GetPositionVS(float2 positionSS, float depth)
{
    float linearDepth = LinearEyeDepth(depth, _ZBufferParams);
    return float3((positionSS * _AODepthToViewParams.xy - _AODepthToViewParams.zw) * linearDepth, linearDepth);
}

float3 GetNormalVS(float3 normalWS)
{
    float3 normalVS = normalize(mul((float3x3)UNITY_MATRIX_V, normalWS));
    return float3(normalVS.xy, -normalVS.z);
}

float UpdateHorizon(float maxH, float candidateH, float distSq)
{
    float falloff = saturate(1.0 - distSq * _AOInvRadiusSq);
    return candidateH > maxH ? lerp(maxH, candidateH, falloff) : lerp(maxH, candidateH, _AOThickness);
}

float HorizonLoop(float3 positionVS, float3 V, float2 rayStart, float2 rayDir, float rayOffset,
                  float rayStep, int mipModifier)
{
    float maxHorizon = -1.0;
    float t = rayOffset * rayStep + rayStep;

    const uint startWithLowerRes = min(max(0, _AOStepCount / 2 - 2), 3);
    for (uint i = 0; i < _AOStepCount; i++)
    {
        float2 samplePos = max(2, min(rayStart + t * rayDir, _AOBufferSize.xy - 2));
        float sampleDepth = GetDepthSample(samplePos, i > startWithLowerRes);
        float3 samplePosVS = GetPositionVS(samplePos.xy, sampleDepth);

        float3 deltaPos = samplePosVS - positionVS;
        float deltaLenSq = dot(deltaPos, deltaPos);

        float currHorizon = dot(deltaPos, V) * rsqrt(deltaLenSq + 0.0001);
        maxHorizon = UpdateHorizon(maxHorizon, currHorizon, deltaLenSq);

        t += rayStep;
    }
    return maxHorizon;
}

// ---------------------------------------------------------------------------
// AO / depth packing (23-bit depth + 8-bit AO in one float)
// ---------------------------------------------------------------------------

float PackAOOutput(float AO, float depth)
{
    uint packedDepth = PackFloatToUInt(depth, 0, 23);
    uint packedAO = PackFloatToUInt(AO, 24, 8);
    uint packedVal = packedAO | packedDepth;
    // A NaN would lose its bit pattern through a sampler and poison the depth-aware blur.
    if ((packedVal & 0x7FFFFFFF) > 0x7F800000)
        packedVal = packedAO;
    // A float texture because gather4 on an integer texture is not universally supported.
    return asfloat(packedVal);
}

void UnpackData(float data, out float AO, out float depth)
{
    depth = UnpackUIntToFloat(asuint(data), 0, 23);
    AO = UnpackUIntToFloat(asuint(data), 24, 8);
}

void UnpackGatheredData(float4 data, out float4 AOs, out float4 depths)
{
    UnpackData(data.x, AOs.x, depths.x);
    UnpackData(data.y, AOs.y, depths.y);
    UnpackData(data.z, AOs.z, depths.z);
    UnpackData(data.w, AOs.w, depths.w);
}

void GatherAOData(TEXTURE2D_X_FLOAT(_AODataSource), float2 UV, out float4 AOs, out float4 depths)
{
    float4 data = GATHER_TEXTURE2D_X(_AODataSource, sampler_PointClamp, UV);
    UnpackGatheredData(data, AOs, depths);
}

float OutputFinalAO(float AO)
{
    return PositivePow(AO, _AOIntensity);
}

float4 PackAODepthNormal(float ao, float depth)
{
    return PackAOOutput(ao, depth).xxxx;
}

// ---------------------------------------------------------------------------
// Tracing (HDRP.GTAO.compute)
// ---------------------------------------------------------------------------

float4 GTAO(uint2 positionSS)
{
    float currDepth = GetDepthForCentral(positionSS);
    float3 positionVS = GetPositionVS(positionSS, currDepth);
    uint2 fullResPositionSS = positionSS * rcp(DOWNSAMPLE);

    half3 norm_o = SampleNormalAtFullRes(fullResPositionSS);

    float offset = GetOffset(positionSS);
    float2 rayStart = positionSS;
    float integral = 0;

    const int dirCount = (int)_AODirectionCount;

    float3 V = normalize(-positionVS);
    float fovCorrectedRadiusSS = clamp(_AORadius * _AOFOVCorrection * rcp(positionVS.z),
                                       _AOStepCount, _AOMaxRadiusInPixels);
    float step = max(1, fovCorrectedRadiusSS * _AOInvStepCountPlusOne);

    [unroll]
    for (int i = 0; i < dirCount; ++i)
    {
        float2 dir = GetDirection(positionSS, i);

        // The epsilon works around a driver bug; it is far below one pixel.
        float2 negDir = -dir + 1e-30;

        float2 maxHorizons;
        maxHorizons.x = HorizonLoop(positionVS, V, rayStart, dir, offset, step, 0);
        maxHorizons.y = HorizonLoop(positionVS, V, rayStart, negDir, offset, step, 0);

        float3 normalVS = GetNormalVS(norm_o);

        float3 sliceN = normalize(cross(float3(dir.xy, 0.0), V.xyz));
        float3 projN = normalVS - sliceN * dot(normalVS, sliceN);
        float projNLen = length(projN);
        float cosN = dot(projN / projNLen, V);

        float3 T = cross(V, sliceN);
        float N = -sign(dot(projN, T)) * GTAOFastAcos(cosN);

        maxHorizons.x = -GTAOFastAcos(maxHorizons.x);
        maxHorizons.y = GTAOFastAcos(maxHorizons.y);
        maxHorizons.x = N + max(maxHorizons.x - N, -HALF_PI);
        maxHorizons.y = N + min(maxHorizons.y - N, HALF_PI);
        integral += AnyIsNaN(maxHorizons) ? 1 : IntegrateArcCosWeighted(maxHorizons.x, maxHorizons.y, N, cosN);
    }

    integral /= dirCount;

    if (currDepth == UNITY_RAW_FAR_CLIP_VALUE || integral < -1e-2)
        integral = 1;

    float ao = saturate(integral);
    return PackAODepthNormal(ao, currDepth);
}

#endif
