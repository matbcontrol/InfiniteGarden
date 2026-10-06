Shader "InfiniteGarden/Sky"
{
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "IGCommon.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 directionWS : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.directionWS = input.positionOS.xyz; // the skybox mesh is centred on the camera
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // The sky is simply "fog at infinite distance", so far geometry melts into it seamlessly.
                return half4(IG_FogColor(normalize(input.directionWS)), 1.0);
            }
            ENDHLSL
        }
    }
}
