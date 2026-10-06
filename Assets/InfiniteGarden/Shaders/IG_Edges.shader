Shader "InfiniteGarden/Edges"
{
    Properties
    {
        _EdgeColor ("Edge Color (A = strength)", Color) = (0.13, 0.14, 0.19, 0.9)
        _EdgeWidth ("Edge Width (pixels)", Range(0.5, 3)) = 1.4
        _NormalThreshold ("Normal Threshold (cos of angle)", Range(0, 1)) = 0.8
        _DepthThreshold ("Depth Threshold (relative)", Range(0.001, 0.2)) = 0.03
        _TapCount ("Taps: 4, 8 or 20", Float) = 20
        _FadeStart ("Edge Fade Start (m)", Float) = 120
        _FadeEnd ("Edge Fade End (m)", Float) = 250
        _DebugEdges ("Debug: show edge mask", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "IG Edges"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "IGCommon.hlsl"

            half4 _EdgeColor;
            float _EdgeWidth;
            float _NormalThreshold;
            float _DepthThreshold;
            float _TapCount;
            float _FadeStart;
            float _FadeEnd;
            float _DebugEdges;

            // Neighbours to compare against, sorted by distance. 4 = cross, 8 = + diagonals, 20 = + ring of radius 2.
            static const int2 kTaps[20] =
            {
                int2( 1, 0), int2(-1, 0), int2( 0, 1), int2( 0,-1),
                int2( 1, 1), int2(-1, 1), int2( 1,-1), int2(-1,-1),
                int2( 2, 0), int2(-2, 0), int2( 0, 2), int2( 0,-2),
                int2( 2, 1), int2(-2, 1), int2( 2,-1), int2(-2,-1),
                int2( 1, 2), int2(-1, 2), int2( 1,-2), int2(-1,-2)
            };

            bool IsSky(float rawDepth)
            {
            #if UNITY_REVERSED_Z
                return rawDepth <= 1e-6;
            #else
                return rawDepth >= 1.0 - 1e-6;
            #endif
            }

            half4 Frag(Varyings input) : SV_Target
            {
                int2 size = int2(_ScaledScreenParams.xy);
                int2 p = int2(input.positionCS.xy);
                half3 sceneColor = LOAD_TEXTURE2D_X(_BlitTexture, p).rgb;

                float raw0 = LoadSceneDepth(p);
                bool sky0 = IsSky(raw0);
                float depth0 = LinearEyeDepth(raw0, _ZBufferParams);
                float3 normal0 = LoadSceneNormals(p);

                // World position and view direction of this pixel (for fog and the grazing-angle fix).
                float2 uv = (float2(p) + 0.5) / float2(size);
                float3 positionWS = ComputeWorldSpacePosition(uv, raw0, UNITY_MATRIX_I_VP);
                float3 toPixel = positionWS - GetCameraPositionWS();
                float distance0 = length(toPixel);
                float3 viewDirWS = toPixel / max(distance0, 1e-4);
                float distancePerDepth = distance0 / max(depth0, 1e-4);

                // Surfaces seen at a grazing angle change depth quickly from pixel to pixel:
                // loosen the depth test there, or every floor in the distance turns into stripes.
                float facing = sky0 ? 1.0 : max(abs(dot(normal0, viewDirWS)), 0.08);
                float depthTolerance = _DepthThreshold / facing;

                // Edge = the pixel and a neighbour belong to different surfaces.
                // The edge lies halfway between them, so 0.5 * |offset| is the distance from this pixel to the edge.
                // Keeping the smallest one gives a distance field to the nearest edge (the Manifold Garden trick):
                // drawing an anti-aliased iso-line of it gives smooth lines of any width without MSAA.
                float distanceToEdge = 100.0;
                float lineDepth = depth0;
                int taps = clamp((int)_TapCount, 1, 20);

                [loop]
                for (int k = 0; k < taps; k++)
                {
                    int2 q = clamp(p + kTaps[k], int2(0, 0), size - 1);
                    float raw1 = LoadSceneDepth(q);
                    bool sky1 = IsSky(raw1);
                    if (sky0 && sky1)
                        continue;

                    float depth1 = LinearEyeDepth(raw1, _ZBufferParams);
                    bool isEdge = sky0 != sky1; // silhouette against the sky
                    if (!isEdge)
                    {
                        float3 normal1 = LoadSceneNormals(q);
                        isEdge = dot(normal0, normal1) < _NormalThreshold
                              || abs(depth1 - depth0) > depthTolerance * min(depth0, depth1);
                    }

                    float d = 0.5 * length(float2(kTaps[k]));
                    if (isEdge && d < distanceToEdge)
                    {
                        distanceToEdge = d;
                        lineDepth = sky0 ? depth1 : (sky1 ? depth0 : min(depth0, depth1));
                    }
                }

                float coverage = saturate(0.5 * _EdgeWidth + 0.5 - distanceToEdge);

                // Far lines become sub-pixel noise: fade them out, and fog them like the geometry.
                float lineDistance = lineDepth * distancePerDepth;
                float fade = 1.0 - smoothstep(_FadeStart, _FadeEnd, lineDistance);
                half3 edgeColor = lerp(_EdgeColor.rgb, IG_FogColor(viewDirWS), IG_FogFactor(lineDistance));

                half3 result = lerp(sceneColor, edgeColor, coverage * fade * _EdgeColor.a);
                if (_DebugEdges > 0.5)
                    result = lerp(half3(1.0, 1.0, 1.0), half3(0.05, 0.05, 0.08), coverage * fade);
                return half4(result, 1.0);
            }
            ENDHLSL
        }
    }
}
