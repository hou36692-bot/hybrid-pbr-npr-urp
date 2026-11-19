// Full-resolution composite of the half-res volumetric fog: a depth-weighted 2x2 upsample of
// in-scattering/transmittance over the scene colour. Written for this project (S2-5).
Shader "Hidden/HybridRP/FogComposite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        TEXTURE2D_X(_FogScatter);
        TEXTURE2D_X_FLOAT(_FogDepth);

        float4 _FogScatterSize;   // xy: half-res size, zw: 1 / size

        float4 FragFog(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord.xy;
            float4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

            float sceneDepth = LoadSceneDepth(input.positionCS.xy);
            // Sky pixels carry no fog; without this early-out their sentinel depth makes every
            // tap weight vanish and the sky would be multiplied toward black.
            if (sceneDepth == UNITY_RAW_FAR_CLIP_VALUE)
                return source;

            float linearDepth = LinearEyeDepth(sceneDepth, _ZBufferParams);

            // Exact 2x2 half-res neighbourhood: local coordinates, floor, bilinear weights. At a
            // silhouette the background tap loses to the foreground one instead of bleeding fog
            // over the character.
            float2 local = uv * _FogScatterSize.xy - 0.5;
            int2 baseCoord = (int2)floor(local);
            float2 fraction = frac(local);
            int2 maxCoord = int2(_FogScatterSize.xy) - 1;
            int2 offsets[4] = { int2(0, 0), int2(1, 0), int2(0, 1), int2(1, 1) };
            float4 accum = 0;
            float weightSum = 0;

            [unroll]
            for (int i = 0; i < 4; i++)
            {
                int2 coord = clamp(baseCoord + offsets[i], int2(0, 0), maxCoord);
                float tapDepth = LOAD_TEXTURE2D_X(_FogDepth, coord).r;
                float4 tap = LOAD_TEXTURE2D_X(_FogScatter, coord);
                float bilinear = (offsets[i].x == 0 ? 1 - fraction.x : fraction.x) *
                                 (offsets[i].y == 0 ? 1 - fraction.y : fraction.y);
                // Relative depth weight so the rejection works at every distance.
                float depthWeight = 1.0 / (abs(tapDepth - linearDepth) + 0.05 + 0.02 * linearDepth);
                float weight = bilinear * depthWeight;
                accum += tap * weight;
                weightSum += weight;
            }
            float4 fog = accum / max(weightSum, 1e-5);

            float3 color = source.rgb * fog.a + fog.rgb;
            return float4(color, source.a);
        }
        ENDHLSL

        Pass
        {
            Name "HybridFogComposite"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragFog
            ENDHLSL
        }
    }
}
