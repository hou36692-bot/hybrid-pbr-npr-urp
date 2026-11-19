#ifndef HYBRID_GTAO_CONSTANTS_INCLUDED
#define HYBRID_GTAO_CONSTANTS_INCLUDED

// Ground-truth ambient occlusion parameters, packed into the same eight vectors the HDRP /
// IllusionRP GTAO uses, so the ported tracing code keeps its layout. Set per camera by
// HybridGTAOPass from the HybridGTAO volume component.
CBUFFER_START(HybridGTAOParameters)
    float4 _AOBufferSize;               // xy: AO resolution, zw: 1 / resolution
    float4 _AOParams0;                  // x thickness^2, y fov correction, z radius (m), w step count
    float4 _AOParams1;                  // x intensity, y 1 / radius^2, z/w unused (no temporal)
    float4 _AOParams2;                  // x direction count, y 1 / downsample, z 1 / (steps + 1), w max radius px
    float4 _AOParams3;                  // x blur tolerance, y upsample tolerance, z noise filter, w step size
    float4 _AOParams4;                  // w spatial bilateral aggressiveness
    float4 _FirstTwoDepthMipOffsets;    // xy mip 1 offset, zw mip 2 offset in the packed atlas
    float4 _AODepthToViewParams;        // pixel position + linear depth -> view position
CBUFFER_END

#endif
