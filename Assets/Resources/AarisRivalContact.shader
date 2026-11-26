Shader "Aaris/Rival Soft Contact"
{
    Properties
    {
        _Opacity("Contact opacity", Range(0,0.4)) = 0.20
        _ReceiverDepthBias("Raised ground overlay tolerance (m)", Range(0,0.16)) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-5" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend DstColor Zero
            ZWrite Off
            Cull Off
            Offset -1, -1
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half _Opacity;
                float _ReceiverDepthBias;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; half2 uv : TEXCOORD0; half fog : TEXCOORD1; half strength : TEXCOORD2; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                // Exact tyre hits are already on the road. A receiver bias
                // would project their darkening through the tyre or a curb.
                float4x4 objectToWorld = GetObjectToWorldMatrix();
                float receiver = objectToWorld[1][1];
                bool tyre = receiver < 0.0;
                bool needsBias = !tyre || receiver < -1.1;
                if (needsBias && _ReceiverDepthBias > 0.0 && unity_OrthoParams.w < 0.5)
                {
                    // Evaluate depth at a slightly raised receiver plane along
                    // the same eye ray. Preserve the actual ground projection.
                    float3 toEye = GetCameraPositionWS() - positionWS;
                    float3 normalWS = TransformObjectToWorldNormal(float3(0, tyre ? -1 : 1, 0));
                    float fraction = _ReceiverDepthBias / max(dot(normalWS, toEye), 0.5);
                    float4 biasedCS = TransformWorldToHClip(positionWS + toEye * fraction);
                    output.positionCS.z = biasedCS.z * (output.positionCS.w / biasedCS.w);
                }
                output.uv = input.uv * 2.0h - 1.0h;
                output.fog = ComputeFogFactor(output.positionCS.z);
                output.strength = tyre ? min(0.44h, _Opacity * 1.55h) : _Opacity;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half falloff = saturate(1.0h - dot(input.uv, input.uv));
                half shade = 1.0h - falloff * falloff * input.strength;
                return half4(MixFogColor(half3(shade, shade, shade), half3(1,1,1), input.fog), 1.0h);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
