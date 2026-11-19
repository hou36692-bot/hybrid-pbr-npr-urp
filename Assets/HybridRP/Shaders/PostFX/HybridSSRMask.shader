// Mask for the reflective rendering layer: the SSR pass is evaluated only on these pixels.
// The mask is depth-tested against the camera depth so a reflector hidden behind other geometry
// cannot mark the occluder's pixels as reflective.
Shader "Hidden/HybridRP/SSRMask"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "SSRMask"
            ZTest Always
            ZWrite Off
            Cull Off
            ColorMask R

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMask

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half FragMask(Varyings input) : SV_Target
            {
                float sceneDepth = LoadSceneDepth(input.positionCS.xy);
                // In the fragment stage SV_POSITION.z is already the device depth (0..1), the same
                // space as _CameraDepthTexture. Reversed Z: nearer surfaces have larger raw depth,
                // so a reflector behind the visible surface must not set the mask.
                float fragmentDepth = input.positionCS.z;
                clip(fragmentDepth - sceneDepth + 0.0001);
                return 1.0h;
            }
            ENDHLSL
        }
    }
}
