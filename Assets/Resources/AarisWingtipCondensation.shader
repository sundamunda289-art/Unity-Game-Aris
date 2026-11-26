Shader "Aaris/URP Wingtip Condensation"
{
    Properties
    {
        _BaseColor("Condensation", Color) = (0.91, 0.95, 1, 0.14)
        _TimeOffset("Wake time offset", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent+4" }
        Pass
        {
            Name "WingtipWake"
            Tags { "LightMode"="UniversalForward" }
            Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WakeVertex
            #pragma fragment WakeFragment
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _TimeOffset;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half fade : TEXCOORD1;
                half fog : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings WakeVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 world = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(world);
                output.uv = input.uv;
                // Effects fade gently before they become subpixel. The body
                // keeps its full detailed art and ordinary camera visibility.
                output.fade = saturate((650.0 - distance(world, GetCameraPositionWS())) / 250.0);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }
            half4 WakeFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half edge = saturate(1.0 - abs(input.uv.x));
                half lengthFade = smoothstep(0.0, 0.07, input.uv.y) *
                    (1.0 - smoothstep(0.15, 1.0, input.uv.y));
                half ripple = 0.92h + 0.08h * sin(input.uv.y * 24.0 - (_Time.y + _TimeOffset) * 4.0);
                half alpha = _BaseColor.a * edge * edge * lengthFade * ripple * input.fade;
                Light sun = GetMainLight();
                half3 ambient = max(SampleSH(half3(0, 1, 0)), half3(0.06h, 0.06h, 0.06h));
                half3 illumination = ambient + sun.color * sun.distanceAttenuation *
                    (0.25h + 0.65h * saturate(sun.direction.y));
                return half4(MixFog(_BaseColor.rgb * illumination, input.fog), saturate(alpha));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
