// Adds the screen-space reflection to the scene colour where the ray hit (alpha = confidence).
Shader "Hidden/HybridRP/SSRComposite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D_X(_SSRTexture);
        float _SSRIntensity;

        float4 FragSSR(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float4 ssr = SAMPLE_TEXTURE2D_X(_SSRTexture, sampler_LinearClamp, input.texcoord.xy);
            float3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord.xy).rgb;
            color += ssr.rgb * (ssr.a * _SSRIntensity);
            return float4(color, 1.0);
        }
        ENDHLSL

        Pass
        {
            Name "HybridSSRComposite"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragSSR
            ENDHLSL
        }
    }
}
