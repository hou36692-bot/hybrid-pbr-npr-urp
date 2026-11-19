Shader "HybridRP/Character/Eye"
{
    // Iris / sclera / 目HL. No parallax (out of scope). The eye is an art-directed material:
    // ambient is boosted so the iris stays readable in shadow, direct light is soft and
    // the highlight mesh is emissive.
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)
        _EmissionColor ("Emission Color", Color) = (1,1,1,1)
        _EmissionStrength ("Emission Strength", Range(0,4)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.7
        _SpecIntensity ("Specular Intensity", Range(0,2)) = 0.4
        _AmbientStrength ("Ambient Strength", Range(0,2)) = 0.8
        _Saturation ("Saturation", Range(0,1.5)) = 1
        _IrisShadeColor ("Iris Upper Shade (white = off)", Color) = (1,1,1,1)
        _IrisShadeStart ("Iris Shade Start (UV v)", Range(0,1)) = 0.5
        _IrisShadeEnd ("Iris Shade End (UV v)", Range(0,1)) = 0.85
        _SSAOStrength ("SSAO Strength", Range(0,1)) = 0.2
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex HybridEyeVertex
            #pragma fragment HybridEyeFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile _ USE_APV_PROBE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Assets/HybridRP/Shaders/Include/HybridEyePass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex HybridShadowVertex
            #pragma fragment HybridShadowFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Assets/HybridRP/Shaders/Include/HybridEyeInput.hlsl"
            #include "Assets/HybridRP/Shaders/Include/HybridCharacterDepthPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex HybridDepthOnlyVertex
            #pragma fragment HybridDepthOnlyFragment
            #pragma multi_compile_instancing

            #include "Assets/HybridRP/Shaders/Include/HybridEyeInput.hlsl"
            #include "Assets/HybridRP/Shaders/Include/HybridCharacterDepthPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask RGB

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex HybridDepthNormalsVertex
            #pragma fragment HybridDepthNormalsFragment
            #pragma multi_compile_instancing

            #include "Assets/HybridRP/Shaders/Include/HybridEyeInput.hlsl"
            #include "Assets/HybridRP/Shaders/Include/HybridCharacterDepthPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "HybridOutlineMask"
            Tags { "LightMode" = "HybridOutlineMask" }
            Cull [_Cull]
            ZWrite Off
            ZTest Always
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex HybridDepthOnlyVertex
            #pragma fragment HybridOutlineMaskFragment
            #pragma multi_compile_instancing
            #include "Assets/HybridRP/Shaders/Include/HybridEyeInput.hlsl"
            #include "Assets/HybridRP/Shaders/Include/HybridCharacterDepthPasses.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
