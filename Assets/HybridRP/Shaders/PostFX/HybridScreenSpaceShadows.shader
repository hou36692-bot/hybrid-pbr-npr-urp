// Screen-space main-light shadow resolve: cascaded shadow map visibility multiplied by our
// contact-shadow map, written to URP's _ScreenSpaceShadowmapTexture. Written from scratch
// against URP's own screen-space shadow resolve (Hidden/Universal Render Pipeline/
// ScreenSpaceShadows) and URP's Shadows.hlsl; no third-party shader code is copied here.
// The scene and character materials then read it through _MAIN_LIGHT_SHADOWS_SCREEN.
Shader "Hidden/HybridRP/ScreenSpaceShadows"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        TEXTURE2D_X_FLOAT(_ContactShadowMap);

        half4 Frag(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float deviceDepth = LoadSceneDepth(input.positionCS.xy);

            float3 positionWS = ComputeWorldSpacePosition(input.texcoord.xy, deviceDepth, unity_MatrixInvVP);
            float4 coords = TransformWorldToShadowCoord(positionWS);

            // Cascaded shadow-map visibility for the main light; contact shadows subtract from it.
            half realtimeShadow = MainLightRealtimeShadow(coords);
            half contact = SAMPLE_TEXTURE2D_X(_ContactShadowMap, sampler_PointClamp, input.texcoord.xy).r;
            return realtimeShadow * (1.0h - contact);
        }
        ENDHLSL

        Pass
        {
            Name "HybridScreenSpaceShadows"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma multi_compile _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
