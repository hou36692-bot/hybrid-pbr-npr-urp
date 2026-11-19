Shader "Hidden/HybridRP/Outline"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "OutlineComposite"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment OutlineFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            TEXTURE2D_X(_HybridOutlineMaskTexture);
            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineParams; // UV radius at 1080p, relative depth, 1-dot normal, normal strength
                float4 _OutlineTexelSize;
                float4 _OutlineSurface; // surface-tint blend, darkness, saturation power
                half4 _OutlineColor;
                float _OutlineStrength;
                float _OutlineDebug;
            CBUFFER_END

            // A darker, more saturated shade of the surface the line sits on: dividing by the peak
            // channel gives the hue, raising it to a power pulls the minor channels down (warm brown
            // on skin, red-brown on lips, near-black on dark hair, grey on white cloth). The clamp
            // keeps a specular pixel under the line from turning the line bright.
            half3 SurfaceLineColor(half3 surface)
            {
                surface = min(surface, 2.0h);
                half peak = max(Max3(surface.r, surface.g, surface.b), 1e-4h);
                half3 hue = surface / peak;
                return surface * PositivePow(hue, _OutlineSurface.z) * _OutlineSurface.y;
            }

            float EyeDepth(float2 uv)
            {
                float raw = SampleSceneDepth(uv);
                return IsPerspectiveProjection() ? LinearEyeDepth(raw, _ZBufferParams) : LinearDepthToEyeDepth(raw);
            }

            float Coverage(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_HybridOutlineMaskTexture, sampler_PointClamp, uv).r;
            }

            float PairEdge(float2 uv, float2 offset, float depth, float3 normal)
            {
                float2 a = clamp(uv + offset, _OutlineTexelSize.xy * 0.5, 1.0 - _OutlineTexelSize.xy * 0.5);
                float2 b = clamp(uv - offset, _OutlineTexelSize.xy * 0.5, 1.0 - _OutlineTexelSize.xy * 0.5);
                float ca = Coverage(a), cb = Coverage(b);
                float da = EyeDepth(a), db = EyeDepth(b);
                // Draw only inside the character. A nearer non-character neighbour is an
                // occluder, not a silhouette: never paint a black border around its edge.
                float silhouette = max((1.0 - ca) * step(depth * (1.0 + _OutlineParams.y), da),
                                       (1.0 - cb) * step(depth * (1.0 + _OutlineParams.y), db));
                float internalDepth = abs(da + db - 2.0 * depth) / max(depth, 0.001);
                // Opposite samples cancel first-order depth slope on smooth slanted surfaces.
                internalDepth = smoothstep(_OutlineParams.y, _OutlineParams.y * 2.0, internalDepth) * ca * cb;
                float normalDelta = max((1.0 - saturate(dot(normal, SampleSceneNormals(a)))) * ca,
                                        (1.0 - saturate(dot(normal, SampleSceneNormals(b)))) * cb);
                float normalEdge = smoothstep(_OutlineParams.z, _OutlineParams.z + 0.15, normalDelta);
                return max(silhouette, max(internalDepth, normalEdge * _OutlineParams.w));
            }

            half4 OutlineFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                if (Coverage(uv) < 0.5)
                    return _OutlineDebug > 0.5 ? half4(0, 0, 0, 1) : source;

                float depth = EyeDepth(uv);
                float3 normal = SampleSceneNormals(uv);
                float2 radius = max(_OutlineTexelSize.xy * 0.5,
                                   _OutlineParams.x * float2(_OutlineTexelSize.w / _OutlineTexelSize.z, 1.0));
                float edge = PairEdge(uv, float2(radius.x, 0), depth, normal);
                edge = max(edge, PairEdge(uv, float2(0, radius.y), depth, normal));
                edge = max(edge, PairEdge(uv, radius * 0.707107, depth, normal));
                edge = max(edge, PairEdge(uv, float2(radius.x, -radius.y) * 0.707107, depth, normal));
                if (_OutlineDebug > 0.5)
                    return half4(edge.xxx, 1);
                half3 lineColor = lerp(_OutlineColor.rgb, SurfaceLineColor(source.rgb), saturate(_OutlineSurface.x));
                return half4(lerp(source.rgb, lineColor, edge * _OutlineStrength), source.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
