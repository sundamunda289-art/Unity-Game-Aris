Shader "Aaris/URP Coastal Water"
{
    Properties
    {
        _BaseColor("Water", Color) = (0.03, 0.22, 0.28, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0.82
        [NoScaleOffset] _RippleNormal("Ripple normal", 2D) = "bump" {}
        _RippleStrength("Ripple strength", Range(0, 1)) = 0.38
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry-10" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_RippleNormal);
        SAMPLER(sampler_RippleNormal);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _Smoothness;
            float _RippleStrength;
        CBUFFER_END
        struct WaterAttributes { float4 positionOS : POSITION; };
        struct WaterVaryings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half fogFactor : TEXCOORD1;
            half3 normalWS : TEXCOORD2;
        };
        // Shared displacement keeps the depth and color passes in exactly the same place.
        float3 WaterPositionWS(float3 positionOS)
        {
            float3 p = TransformObjectToWorld(positionOS);
            p.y += 0.10 * sin(p.x * 0.045 + _Time.y * 0.6)
                 + 0.06 * sin(p.z * 0.063 - _Time.y * 0.45);
            return p;
        }
        half3 WaterNormalWS(float3 p)
        {
            return normalize(float3(-0.0045 * cos(p.x * 0.045 + _Time.y * 0.6), 1,
                -0.00378 * cos(p.z * 0.063 - _Time.y * 0.45)));
        }
        WaterVaryings WaterVertex(WaterAttributes input)
        {
            WaterVaryings output;
            output.positionWS = WaterPositionWS(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.fogFactor = ComputeFogFactor(output.positionCS.z);
            output.normalWS = WaterNormalWS(output.positionWS);
            return output;
        }
        ENDHLSL
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            Cull Back ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex WaterVertex
            #pragma fragment WaterDepthFragment
            half4 WaterDepthFragment(WaterVaryings input) : SV_TARGET { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Back ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #define _SPECULAR_SETUP 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceData.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            half4 WaterFragment(WaterVaryings input) : SV_TARGET
            {
                InputData lighting = (InputData)0;
                lighting.positionWS = input.positionWS;
                lighting.positionCS = input.positionCS;
                lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float3 rippleEyeOffset = GetCameraPositionWS() - input.positionWS;
                float rippleDistanceSquared = dot(rippleEyeOffset, rippleEyeOffset);
                half nearbyRipples = saturate((1444.0 - rippleDistanceSquared) / 1299.0);
                half rippleVisibility = smoothstep(0.09h, 0.48h, abs(lighting.viewDirectionWS.y)) * nearbyRipples;
                // A single scrolling, mipmapped normal works on both the
                // coarse ocean mesh and the nearly flat inland canal.
                float2 rippleUV = input.positionWS.xz * float2(0.071, 0.052)
                    + _Time.y * float2(0.014, -0.009);
                half2 ripple = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_RippleNormal, sampler_RippleNormal, rippleUV),
                    _RippleStrength * rippleVisibility).xy;
                lighting.normalWS = normalize(input.normalWS + half3(ripple.x, 0, ripple.y));
                lighting.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                lighting.fogCoord = input.fogFactor;
                lighting.bakedGI = max(SampleSH(lighting.normalWS), half3(0, 0, 0));
                lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.shadowMask = half4(1, 1, 1, 1);
                SurfaceData water = (SurfaceData)0;
                water.albedo = _BaseColor.rgb;
                water.specular = half3(0.02, 0.02, 0.02);
                water.smoothness = saturate(_Smoothness);
                water.normalTS = half3(0, 0, 1);
                water.occlusion = 1;
                water.alpha = 1;
                // Native URP BRDF supplies direct sun, ambient SH and sky reflections.
                half4 result = UniversalFragmentPBR(lighting, water);
                return half4(MixFog(result.rgb, input.fogFactor), 1);
            }
            ENDHLSL
        }
    }
}
