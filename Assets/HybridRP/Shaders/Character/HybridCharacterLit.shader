Shader "HybridRP/Character/Lit"
{
    // Skin and cloth. Ramp diffuse from _RD (A = shadow-area mask), GGX specular,
    // shadow-colour tint, Fresnel rim; URP main/additional lights, CSM, APV, reflection
    // probes and fog. _HybridShadingMode = 1 gives the PBR comparison path.
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)
        _BumpMap ("Normal Map (XY only)", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0,2)) = 1
        _PMap ("P Mask (R metal / B AO / A smooth)", 2D) = "white" {}
        _UsePMap ("Use P Mask", Range(0,1)) = 0
        _Metallic ("Metallic Scale", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.35
        _SmoothnessScale ("Smoothness Scale", Range(0,1)) = 1
        _OcclusionStrength ("Occlusion Strength", Range(0,1)) = 1

        [Header(Ramp)]
        _RampMap ("Ramp (RGB + A shadow mask)", 2D) = "white" {}
        _RampWeight ("Ramp Weight", Range(0,1)) = 1
        [Toggle(_RAMP_ON)] _RampOn ("Ramp Diffuse", Float) = 1

        [Header(Shadow Color)]
        _ShadowColor ("Shadow Color", Color) = (0.70,0.78,0.95,1)
        _ShadowColorStrength ("Shadow Color Strength", Range(0,1)) = 1
        [Toggle(_SHADOWCOLOR_ON)] _ShadowColorOn ("Shadow Color", Float) = 1

        [Header(Specular)]
        _SpecColor ("Specular Color", Color) = (1,1,1,1)
        _SpecIntensity ("Specular Intensity", Range(0,2)) = 1
        [Toggle(_SPEC_ON)] _SpecOn ("GGX Specular", Float) = 1

        [Header(Rim)]
        _RimColor ("Rim Color", Color) = (1,1,1,1)
        _RimPower ("Rim Power", Range(0.5,8)) = 4
        _RimIntensity ("Rim Intensity", Range(0,2)) = 0.3
        [Toggle(_RIM_ON)] _RimOn ("Rim Light", Float) = 1

        [Header(Rendering)]
        _SSAOStrength ("SSAO Strength", Range(0,1)) = 0.5
        _DirectLightingIntensity ("Direct Lighting Intensity", Range(0,2)) = 1
        _GIIntensity ("GI Intensity", Range(0,2)) = 1
        _SelfShadowOffset ("Self-Shadow Smoothing Offset (m)", Range(0,0.3)) = 0.12
        _MetalDiffuse ("Metal Keeps Diffuse", Range(0,1)) = 0
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
            #pragma vertex HybridLitVertex
            #pragma fragment HybridLitFragment

            #pragma shader_feature_local_fragment _RAMP_ON
            #pragma shader_feature_local_fragment _SHADOWCOLOR_ON
            #pragma shader_feature_local_fragment _SPEC_ON
            #pragma shader_feature_local_fragment _RIM_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile _ USE_APV_PROBE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Assets/HybridRP/Shaders/Include/HybridLitPass.hlsl"
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

            #include "Assets/HybridRP/Shaders/Include/HybridLitInput.hlsl"
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

            #include "Assets/HybridRP/Shaders/Include/HybridLitInput.hlsl"
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

            #include "Assets/HybridRP/Shaders/Include/HybridLitInput.hlsl"
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
            #include "Assets/HybridRP/Shaders/Include/HybridLitInput.hlsl"
            #include "Assets/HybridRP/Shaders/Include/HybridCharacterDepthPasses.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
