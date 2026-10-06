Shader "InfiniteGarden/Surface"
{
    Properties
    {
        [Header(Color per face direction)]
        _ColorPosY ("Floor (+Y)", Color) = (0.96, 0.94, 0.90, 1)
        _ColorNegY ("Ceiling (-Y)", Color) = (0.72, 0.76, 0.82, 1)
        _ColorPosX ("Wall +X", Color) = (0.95, 0.83, 0.77, 1)
        _ColorNegX ("Wall -X", Color) = (0.88, 0.76, 0.76, 1)
        _ColorPosZ ("Wall +Z", Color) = (0.79, 0.87, 0.92, 1)
        _ColorNegZ ("Wall -Z", Color) = (0.78, 0.86, 0.81, 1)
        [Header(Lighting)]
        _Ambient ("Ambient", Range(0, 1)) = 0.65
        _Wrap ("Light Wrap", Range(0, 1)) = 0.4
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Every per-material property lives in this buffer: required for the SRP Batcher.
        CBUFFER_START(UnityPerMaterial)
            half4 _ColorPosY;
            half4 _ColorNegY;
            half4 _ColorPosX;
            half4 _ColorNegX;
            half4 _ColorPosZ;
            half4 _ColorNegZ;
            half _Ambient;
            half _Wrap;
            half _ShadowStrength;
        CBUFFER_END
        ENDHLSL

        // Main color pass
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "IGCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            // Manifold Garden colors a surface by the direction it faces (= which gravity it is a floor for).
            // Squared normal components sum to 1, so tilted faces blend smoothly between the colors.
            half3 OrientationColor(float3 n)
            {
                float3 w = n * n;
                half3 cx = n.x >= 0.0 ? _ColorPosX.rgb : _ColorNegX.rgb;
                half3 cy = n.y >= 0.0 ? _ColorPosY.rgb : _ColorNegY.rgb;
                half3 cz = n.z >= 0.0 ? _ColorPosZ.rgb : _ColorNegZ.rgb;
                return cx * w.x + cy * w.y + cz * w.z;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 normalWS = normalize(input.normalWS);
                half3 albedo = OrientationColor(normalWS);

                // Soft "wrapped" diffuse from the sun + realtime shadows (faded out at the shadow distance).
                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS), input.positionWS, half4(1, 1, 1, 1));
                half diffuse = saturate((dot(normalWS, sun.direction) + _Wrap) / (1.0 + _Wrap));
                half shadow = lerp(1.0, sun.shadowAttenuation, _ShadowStrength);

                // Screen-space ambient occlusion from the URP SSAO renderer feature (if it is enabled).
                half ao = 1.0;
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(input.positionCS)).indirectAmbientOcclusion;
                #endif

                half3 color = albedo * (_Ambient * ao + (1.0 - _Ambient) * diffuse * shadow * sun.color);

                // Fog toward the sky gradient.
                float3 toPixel = input.positionWS - GetCameraPositionWS();
                float distance = length(toPixel);
                color = lerp(color, IG_FogColor(toPixel / max(distance, 1e-4)), IG_FogFactor(distance));
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // Shadow map rendering
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Set by URP for the light currently rendering its shadow map.
            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                output.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS)));
                return output;
            }

            half4 frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        // Depth prepass (used by URP when only the depth texture is needed)
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half frag(Varyings input) : SV_Target { return input.positionCS.z; }
            ENDHLSL
        }

        // Depth + normals prepass: the edge pass and SSAO read this texture
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    return half4(PackFloat2To888(saturate(octNormalWS * 0.5 + 0.5)), 0.0);
                #else
                    return half4(normalWS, 0.0);
                #endif
            }
            ENDHLSL
        }
    }
}
