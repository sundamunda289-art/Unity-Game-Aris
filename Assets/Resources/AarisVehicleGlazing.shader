Shader "Aaris/Automotive Glazing"
{
    Properties
    {
        [MainColor] _BaseColor("Tint and centre opacity", Color) = (0.085, 0.135, 0.18, 0.32)
        [HDR] _EmissionColor("Active lamp emission", Color) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "AutomotiveGlazing"
            Tags { "LightMode"="UniversalForwardOnly" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EmissionColor;
            CBUFFER_END

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
                half3 normalWS : TEXCOORD1;
                half fog : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fog = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half3 normal = normalize(input.normalWS);
                half3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half grazing = 1.0h - saturate(dot(normal, view));
                half fresnel = grazing * grazing;
                fresnel *= fresnel * grazing;
                // Sky and ground separate at the reflected horizon. The tint
                // leaves the authored seats visible through the central pane.
                half elevation = reflect(-view, normal).y;
                half sky = smoothstep(-0.12h, 0.42h, elevation);
                half horizon = saturate(1.0h - abs(elevation - 0.13h) * 4.0h);
                half3 reflected = lerp(half3(0.065h, 0.071h, 0.073h),
                    half3(0.40h, 0.56h, 0.70h), sky) + 0.09h * horizon;
                half3 rgb = lerp(_BaseColor.rgb, reflected, 0.31h + 0.69h * fresnel);
                half alpha = lerp(_BaseColor.a, 0.86h, fresnel);
                return half4(MixFog(rgb + _EmissionColor.rgb, input.fog) * alpha, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
