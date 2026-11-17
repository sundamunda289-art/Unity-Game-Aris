// Linux OpenGL Core preview only. Android retains the native URP Terrain/Lit shader.
Shader "Hidden/Aaris/GlcoreTerrainPreview"
{
    Properties
    {
        [HideInInspector] _Control ("Terrain control", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Terrain layer 0", 2D) = "grey" {}
        [HideInInspector] _Splat1 ("Terrain layer 1", 2D) = "grey" {}
        [HideInInspector] _Splat2 ("Terrain layer 2", 2D) = "grey" {}
        [HideInInspector] _Splat3 ("Terrain layer 3", 2D) = "grey" {}
    }
    SubShader
    {
        Tags { "Queue"="Geometry-100" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "TerrainCompatible"="True" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            Cull Back
            HLSLPROGRAM
            #pragma only_renderers glcore
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Control); SAMPLER(sampler_Control);
            TEXTURE2D(_Splat0); SAMPLER(sampler_Splat0);
            TEXTURE2D(_Splat1); SAMPLER(sampler_Splat1);
            TEXTURE2D(_Splat2); SAMPLER(sampler_Splat2);
            TEXTURE2D(_Splat3); SAMPLER(sampler_Splat3);

            CBUFFER_START(UnityPerMaterial)
                float4 _Splat0_ST;
                float4 _Splat1_ST;
                float4 _Splat2_ST;
                float4 _Splat3_ST;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                half fogFactor : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 worldPosition = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(worldPosition);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float4 weights = max(SAMPLE_TEXTURE2D(_Control, sampler_Control, input.uv), 0);
                weights /= max(dot(weights, float4(1, 1, 1, 1)), 0.0001);
                float3 albedo =
                    weights.r * SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, input.uv * _Splat0_ST.xy + _Splat0_ST.zw).rgb +
                    weights.g * SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, input.uv * _Splat1_ST.xy + _Splat1_ST.zw).rgb +
                    weights.b * SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, input.uv * _Splat2_ST.xy + _Splat2_ST.zw).rgb +
                    weights.a * SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, input.uv * _Splat3_ST.xy + _Splat3_ST.zw).rgb;
                float3 normalWS = normalize(input.normalWS);
                Light sun = GetMainLight();
                float3 lighting = max(SampleSH(normalWS) +
                    sun.color * saturate(dot(normalWS, sun.direction)) * sun.distanceAttenuation,
                    float3(0.24, 0.24, 0.24));
                float3 color = MixFog(albedo * lighting, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
