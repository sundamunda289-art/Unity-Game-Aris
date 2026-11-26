Shader "Aaris/URP Aircraft Propeller"
{
    Properties
    {
        _BaseColor("Exposure tint", Color) = (0.52, 0.59, 0.61, 0.13)
        _BladeColor("Blade and hub", Color) = (0.055, 0.08, 0.085, 1)
        _RPM("Propeller RPM", Range(0, 1600)) = 1000
        _TimeOffset("Exposure time offset", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent+5" }
        Pass
        {
            Name "AircraftExposure"
            Tags { "LightMode"="UniversalForward" }
            Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PropellerVertex
            #pragma fragment PropellerFragment
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _BladeColor;
                float _RPM;
                float _TimeOffset;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 radial : TEXCOORD0;
                float2 phase : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 radial : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half3 rightWS : TEXCOORD3;
                half3 upWS : TEXCOORD4;
                half2 rotation : TEXCOORD5;
                half fog : TEXCOORD6;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings PropellerVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.radial = input.radial;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.rightWS = TransformObjectToWorldDir(float3(1, 0, 0));
                output.upWS = TransformObjectToWorldDir(float3(0, 1, 0));
                // Time is supplied by Unity to the GPU. C# never spins a rotor,
                // writes a material parameter or rebuilds this static mesh.
                float phase = (_Time.y + _TimeOffset) * max(0, _RPM) *
                    (6.28318530718 / 60.0) * input.phase.x + input.phase.y;
                float sine, cosine;
                sincos(phase, sine, cosine);
                output.rotation = half2(sine, cosine);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }
            half4 PropellerFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float radius = length(input.radial);
                float2 radial = input.radial / max(radius, 0.001);
                float rotatedX = radial.x * input.rotation.y - radial.y * input.rotation.x;
                // cos(3 theta), without an atan2 or sampled rotor texture.
                half threeBlade = rotatedX * (4.0 * rotatedX * rotatedX - 3.0);
                half hub = 1.0 - smoothstep(0.15, 0.17, radius);
                half outer = 1.0 - smoothstep(0.90, 1.0, radius);
                half annulus = smoothstep(0.16, 0.23, radius) * outer;
                half exposure = smoothstep(250.0, 750.0, max(0, _RPM));
                half blade = smoothstep(0.92, 0.985, threeBlade) * annulus;
                // Exposure integration replaces high-frequency opaque blades.
                // The modest sector variation retains motion without toy-like
                // strobing or a solid opaque saucer at flight RPM.
                half disc = annulus * _BaseColor.a * (0.90h + 0.10h * threeBlade * threeBlade);
                half alpha = max(hub, lerp(blade * _BladeColor.a, disc, exposure));
                clip(alpha - 0.001h);
                half3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half3 normal = normalize(input.normalWS);
                normal *= dot(normal, view) < 0 ? -1.0h : 1.0h;
                half3 radialWS = input.rightWS * radial.x + input.upWS * radial.y;
                half3 brushedNormal = normalize(normal + radialWS * 0.18h);
                Light sun = GetMainLight();
                half3 halfDirection = SafeNormalize(sun.direction + view);
                half specular = saturate(dot(brushedNormal, halfDirection));
                specular *= specular; specular *= specular;
                specular *= specular; specular *= specular; specular *= specular;
                half facingSun = saturate(dot(normal, sun.direction));
                half3 sunColor = sun.color * sun.distanceAttenuation;
                half3 ambient = max(SampleSH(normal), half3(0.07h, 0.07h, 0.07h));
                half3 tint = lerp(_BladeColor.rgb, _BaseColor.rgb, exposure * (1.0h - hub));
                half3 color = tint * (ambient + sunColor * (0.25h + 0.55h * facingSun));
                color += sunColor * specular * (0.5h + 0.5h * threeBlade * threeBlade) * 0.7h;
                return half4(MixFog(color, input.fog), saturate(alpha));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
