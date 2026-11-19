Shader "HybridRP/Character/Overlay"
{
    // Transparent face overlays: eye shadow and hair shadow (mask mode), blush and tears
    // (colour mode). The MMD material morphs did not survive FBX, so the duplicate meshes
    // are shown/hidden through _HybridBlushWeight / _HybridTearWeight.
    //
    // Multiply blend (v4 pass): the eyelid shadow on the eyeball, the bangs' shadow on the forehead
    // and the blush tint what is under them (out = lit colour x lerp(1, tint, alpha)). An alpha
    // blend toward black greyed the skin and, being unlit, could not follow the light. Tears keep
    // the alpha blend.
    Properties
    {
        [MainTexture] _BaseMap ("Overlay Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1,1,1,1)
        _OverlayColor ("Overlay Color", Color) = (0,0,0,1)
        _OverlayAlpha ("Overlay Alpha", Range(0,1)) = 0.3
        [Enum(Mask,0,Color,1)] _OverlayMode ("Mode", Float) = 0
        [Enum(None,0,Blush,1,Tear,2)] _OverlayGroup ("Group", Float) = 0
        // Set together with _SrcBlend/_DstBlend by CharacterShaderSetup; hidden so the Inspector
        // cannot change the shader branch without the blend factors.
        [HideInInspector] _OverlayBlend ("Blend (0 Alpha, 1 Multiply)", Float) = 0
        [HideInInspector] _SrcBlend ("Src Blend", Float) = 5
        [HideInInspector] _DstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite Off
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]
            Offset -1, -1

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex HybridOverlayVertex
            #pragma fragment HybridOverlayFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _HybridBlushWeight;
            float _HybridTearWeight;

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _OverlayColor;
            half _OverlayAlpha;
            half _OverlayMode;
            half _OverlayGroup;
            half _OverlayBlend;
            half _SrcBlend;
            half _DstBlend;
            half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 texcoord   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv         : TEXCOORD0;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings HybridOverlayVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 HybridOverlayFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                half alpha = _OverlayMode < 0.5h ? tex.r : tex.a;
                half3 color = _OverlayMode < 0.5h ? _OverlayColor.rgb : tex.rgb * _OverlayColor.rgb;

                half group = 1.0h;
                if (_OverlayGroup > 0.5h && _OverlayGroup < 1.5h)
                    group = _HybridBlushWeight;
                else if (_OverlayGroup > 1.5h)
                    group = _HybridTearWeight;

                alpha = saturate(alpha * _OverlayAlpha * group);
                if (_OverlayBlend > 0.5h)
                    return half4(lerp(1.0h, color, alpha), 1.0h);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
