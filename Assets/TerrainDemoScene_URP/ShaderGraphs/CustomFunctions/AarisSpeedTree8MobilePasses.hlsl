// Derived from Unity URP 17.3 SpeedTree8, copyright (c) Unity Technologies ApS.
// Unity Companion License: https://unity.com/legal/licenses/unity-companion-license
// Aaris modifications: mobile foliage illumination, root contact and Fast wind.
#ifndef UNIVERSAL_SPEEDTREE8_PASSES_INCLUDED
#define UNIVERSAL_SPEEDTREE8_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
#include "Packages/com.unity.shadergraph/ShaderGraphLibrary/Nature/SpeedTreeCommon.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Nature/SpeedTreeUtility.hlsl"
#include "AarisFoliageCanopy.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct SpeedTreeVertexInput
{
    float4 vertex       : POSITION;
    float3 normal       : NORMAL;
    float4 tangent      : TANGENT;
    float4 texcoord     : TEXCOORD0;
    float4 texcoord1    : TEXCOORD1;
    float4 texcoord2    : TEXCOORD2;
    float4 texcoord3    : TEXCOORD3;
    float4 color        : COLOR;

    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SpeedTreeVertexOutput
{
    half2 uv                        : TEXCOORD0;
    half4 color                     : TEXCOORD1;

    half4 fogFactorAndVertexLight   : TEXCOORD2;    // x: fogFactor, yzw: vertex light

    #ifdef EFFECT_BUMP
        half4 normalWS              : TEXCOORD3;    // xyz: normal, w: viewDir.x
        half4 tangentWS             : TEXCOORD4;    // xyz: tangent, w: viewDir.y
        half4 bitangentWS           : TEXCOORD5;    // xyz: bitangent, w: viewDir.z
    #else
        half3 normalWS              : TEXCOORD3;
        half3 viewDirWS             : TEXCOORD4;
    #endif

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        float4 shadowCoord          : TEXCOORD6;
    #endif

    float3 positionWS               : TEXCOORD7;
    DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 8);
    #ifdef USE_APV_PROBE_OCCLUSION
        float4 probeOcclusion       : TEXCOORD9;
    #endif
    float4 clipPos                  : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct SpeedTreeVertexDepthOutput
{
    half2 uv                        : TEXCOORD0;
    half4 color                     : TEXCOORD1;
    half3 viewDirWS                 : TEXCOORD2;
    float4 clipPos                  : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct SpeedTreeVertexDepthNormalOutput
{
    half2 uv                        : TEXCOORD0;
    half4 color                     : TEXCOORD1;

    #ifdef EFFECT_BUMP
        half4 normalWS              : TEXCOORD2;    // xyz: normal, w: viewDir.x
        half4 tangentWS             : TEXCOORD3;    // xyz: tangent, w: viewDir.y
        half4 bitangentWS           : TEXCOORD4;    // xyz: bitangent, w: viewDir.z
    #else
        half3 normalWS              : TEXCOORD2;
        half3 viewDirWS             : TEXCOORD3;
    #endif

    float4 clipPos                  : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

struct SpeedTreeDepthNormalFragmentInput
{
    SpeedTreeVertexDepthNormalOutput interpolated;
#ifdef EFFECT_BACKSIDE_NORMALS
    FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC;
#endif
};

struct SpeedTreeFragmentInput
{
    SpeedTreeVertexOutput interpolated;
#ifdef EFFECT_BACKSIDE_NORMALS
    FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC;
#endif
};

#if defined(ENABLE_WIND) && !defined(_WINDQUALITY_NONE) && !defined(EFFECT_BILLBOARD)
#include "AarisGrassPhase.hlsl"

// Match the Fast/Fastest foliage and bark poses at every 3D LOD. The
// native wind data supplies magnitude and inverse height, not the clock.
// A bounded quadratic shear replaces GlobalWind's pow/length/normalization
// on these mobile variants. Lower trunks and roots do not slide.
float3 AarisCoherentTreeSway(float3 positionOS, float3 windOS, float gust, float fade)
{
    float inverseHeight = _ST_WindGlobal.z;
    UNITY_BRANCH if (inverseHeight <= 0.00001 || fade <= 0.0)
        return positionOS;

    float heightWeight = saturate((positionOS.y * inverseHeight - 0.10) * 1.111111);
    heightWeight *= heightWeight;
    float adherence = clamp(_ST_WindBranchAdherences.x, -0.009, 0.009);
    float travel = min(abs(_ST_WindGlobal.y) * inverseHeight, 0.025);
    float sway = (adherence + travel * (0.20 + 0.80 * gust)) *
        (heightWeight * fade / inverseHeight);
    positionOS.xz += windOS.xz * sway;
    return positionOS;
}

#if defined(_WINDQUALITY_FAST)
void AarisFastLeafWind(inout SpeedTreeVertexInput input, inout float3 positionOS,
    float3 anchorOS, float3 windOS, bool leafTwo, float distanceSq, float gust,
    float timeSeconds, float fieldPhase)
{
    const float fullFlutterDistanceSq = 35.0 * 35.0;
    const float endFlutterDistanceSq = 75.0 * 75.0;
    UNITY_BRANCH if (distanceSq >= endFlutterDistanceSq)
        return;

    float fade = AarisWindDistanceFade(distanceSq, fullFlutterDistanceSq, endFlutterDistanceSq);
    // Each authored card retains its hinge and phase. Never use a native
    // per-tree ripple clock for the custom flutter: it may change with LOD.
    float rippleAmplitude = leafTwo ? _ST_WindLeaf2Ripple.y : _ST_WindLeaf1Ripple.y;
    // Reuse the already-computed landscape phase for stable per-tree
    // variation. Identical cloned meshes no longer flutter in lockstep.
    // Use the existing gust to gently vary the flutter speed, without another
    // wave, texture read or time state. The derivative stays bounded even at 30fps.
    float leafPhase = timeSeconds * 0.72 + dot(anchorOS, float3(0.31, 0.57, 0.41))
        + fieldPhase * 1.618 + (leafTwo ? 0.37 : 0.0) + 0.16 * gust;
    float flutter = 2.0 * AarisSmoothWindWave(leafPhase) - 1.0;
    float bend = min(abs(rippleAmplitude) * 5.0, 0.18) * saturate(input.texcoord3.x) * fade;
    float angle = bend * gust * (0.23 + 0.77 * flutter);
    float3 rotation = float3(windOS.z, 0.0, -windOS.x) * angle;
    // Tiny signed torsion reuses the same wave: no second trig/noise sample.
    float twist = angle * (leafTwo ? -0.11 : 0.11);
    rotation += windOS * twist;

    // Small-angle hinge rotation: pin the anchor, and move the lighting
    // basis with the leaf. The bounded bend plus tiny torsion limits stretch.
    positionOS += cross(rotation, positionOS - anchorOS);
    input.normal += cross(rotation, input.normal);
    #if defined(EFFECT_BUMP)
        input.tangent.xyz += cross(rotation, input.tangent.xyz);
    #endif
}
#endif
#endif

void InitializeData(inout SpeedTreeVertexInput input, float lodValue)
{
#if !defined(EFFECT_BILLBOARD)
    #if defined(LOD_FADE_PERCENTAGE)
    UNITY_BRANCH if (unity_LODFade.w <= 1.0)
        input.vertex.xyz = lerp(input.vertex.xyz, input.texcoord2.xyz, lodValue);
    #endif

    // geometry type
    float geometryType = (int) (input.texcoord3.w + 0.25);
    bool leafTwo = false;
    if (geometryType > GEOM_TYPE_FACINGLEAF)
    {
        geometryType -= 2;
        leafTwo = true;
    }

    // leaf facing
    if (geometryType == GEOM_TYPE_FACINGLEAF)
    {
        float3 anchor = float3(input.texcoord1.zw, input.texcoord2.w);
        input.vertex.xyz = DoLeafFacing(input.vertex.xyz, anchor);
    }
#endif

    // wind
    #if defined(ENABLE_WIND) && !defined(_WINDQUALITY_NONE)
        float windEnabled = dot(_ST_WindVector.xyz, _ST_WindVector.xyz) > 0.0f ? 1.0f : 0.0f;
        if (windEnabled > 0)
        {
            float3 treePos = float3(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[1].w, UNITY_MATRIX_M[2].w);
            #if !defined(EFFECT_BILLBOARD) && !defined(_WINDQUALITY_BETTER) && !defined(_WINDQUALITY_BEST) && !defined(_WINDQUALITY_PALM)
                // Uniform per instance: beyond the smooth sway band there is
                // no vertex wind work at all. Leaf facing and mesh LOD were
                // already evaluated above, independently of the wind.
                float3 cameraOffset = treePos - GetCameraPositionWS();
                float distanceSq = dot(cameraOffset, cameraOffset);
                #if defined(SHADER_API_MOBILE)
                    const float fullSwayDistanceSq = 60.0 * 60.0;
                    const float endSwayDistanceSq = 85.0 * 85.0;
                #else
                    const float fullSwayDistanceSq = 85.0 * 85.0;
                    const float endSwayDistanceSq = 150.0 * 150.0;
                #endif
                UNITY_BRANCH if (distanceSq >= endSwayDistanceSq)
                    return;
                float timeSeconds = _Time.y;
                float fieldPhase = AarisLandscapePhase(treePos.xz);
                float gust = AarisLandscapeGust(fieldPhase, timeSeconds);
            #endif

            float3 rotatedWindVector = mul(_ST_WindVector.xyz, (float3x3)UNITY_MATRIX_M);
            float windLengthSq = dot(rotatedWindVector, rotatedWindVector);
            if (windLengthSq < 1e-10)
            {
                // sanity check that wind data is available
                return;
            }
            rotatedWindVector *= rsqrt(windLengthSq);

            float3 windyPosition = input.vertex.xyz;

            #ifndef EFFECT_BILLBOARD

                #if defined(_WINDQUALITY_FAST) || defined(_WINDQUALITY_BETTER) || defined(_WINDQUALITY_BEST)
                    if (geometryType > GEOM_TYPE_FROND)
                    {
                        float3 anchor = float3(input.texcoord1.zw, input.texcoord2.w);
                    #if defined(_WINDQUALITY_FAST)
                        AarisFastLeafWind(input, windyPosition, anchor, rotatedWindVector, leafTwo,
                            distanceSq, 0.40 + 0.60 * gust, timeSeconds, fieldPhase);
                    #else
                        #ifdef _WINDQUALITY_BEST
                            bool bBestWind = true;
                        #else
                            bool bBestWind = false;
                        #endif
                        float leafWindTrigOffset = anchor.x + anchor.y;
                        windyPosition = anchor + LeafWind(bBestWind, leafTwo, windyPosition - anchor,
                            input.normal, input.texcoord3.x, float3(0,0,0), input.texcoord3.y,
                            input.texcoord3.z, leafWindTrigOffset, rotatedWindVector);
                    #endif
                    }
                #endif

                // frond wind
                bool bPalmWind = false;
                #ifdef _WINDQUALITY_PALM
                    bPalmWind = true;
                    if (geometryType == GEOM_TYPE_FROND)
                    {
                        windyPosition = RippleFrond(windyPosition, input.normal, input.texcoord.x, input.texcoord.y, input.texcoord3.x, input.texcoord3.y, input.texcoord3.z);
                    }
                #endif

                // branch wind (applies to all 3D geometry)
                #if defined(_WINDQUALITY_BETTER) || defined(_WINDQUALITY_BEST) || defined(_WINDQUALITY_PALM)
                    float3 rotatedBranchAnchor = normalize(mul(_ST_WindBranchAnchor.xyz, (float3x3)UNITY_MATRIX_M)) * _ST_WindBranchAnchor.w;
                    windyPosition = BranchWind(bPalmWind, windyPosition, treePos, float4(input.texcoord.zw, 0, 0), rotatedWindVector, rotatedBranchAnchor);
                #endif

            #endif // !EFFECT_BILLBOARD

            // Fastest and Fast share a cheap continuous canopy pose, so
            // bark, cap and leaves stay together as their mesh LOD changes.
            #if !defined(EFFECT_BILLBOARD) && !defined(_WINDQUALITY_BETTER) && !defined(_WINDQUALITY_BEST) && !defined(_WINDQUALITY_PALM)
                // Fade to the static billboard before the mobile terrain's
                // usual 85m billboard boundary. Desktop retains a wider sway.
                float swayFade = AarisWindDistanceFade(distanceSq, fullSwayDistanceSq, endSwayDistanceSq);
                windyPosition = AarisCoherentTreeSway(windyPosition, rotatedWindVector, gust, swayFade);
            #else
                // Preserve Unity's authored high-quality and billboard path.
                float globalWindTime = _ST_WindGlobal.x;
                #if defined(EFFECT_BILLBOARD) && defined(UNITY_INSTANCING_ENABLED)
                    globalWindTime += UNITY_ACCESS_INSTANCED_PROP(STWind, _GlobalWindTime);
                #endif
                windyPosition = GlobalWind(windyPosition, treePos, true, rotatedWindVector, globalWindTime);
            #endif
            input.vertex.xyz = windyPosition;
        }
    #endif

    #if defined(EFFECT_BILLBOARD)
        float3 treePos = float3(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[1].w, UNITY_MATRIX_M[2].w);
        // crossfade faces
        bool topDown = (input.texcoord.z > 0.5);
        float3 viewDir = UNITY_MATRIX_IT_MV[2].xyz;
        float3 cameraDir = AarisUnitOr(mul((float3x3)UNITY_MATRIX_M, _WorldSpaceCameraPos - treePos),
            float3(0.0, 0.0, 1.0));
        float viewDot = max(dot(viewDir, input.normal), dot(cameraDir, input.normal));
        viewDot *= viewDot;
        viewDot *= viewDot;
        viewDot += topDown ? 0.38 : 0.18; // different scales for horz and vert billboards to fix transition zone

        // if invisible, avoid overdraw
        if (viewDot < 0.3333)
        {
            input.vertex.xyz = float3(0, 0, 0);
        }

        input.color = float4(1, 1, 1, clamp(viewDot, 0, 1));

        // adjust lighting on billboards to prevent seams between the different faces
        float3 billboardNormal;
        if (topDown)
        {
            billboardNormal = input.normal + cameraDir;
        }
        else
        {
            half3 binormal = cross(input.normal, input.tangent.xyz) * input.tangent.w;
            float3 right = cross(cameraDir, binormal);
            billboardNormal = cross(binormal, right);
        }
        input.normal = AarisUnitOr(billboardNormal, input.normal);
    #endif
}

// Keep leaf lighting tied to the authored SpeedTree anchor, not the
// fluttering vertex position. The same geometry type and anchor are present
// across the three native 3D LODs. Bark/caps and billboard normals are untouched.
#if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
void AarisTreeCanopyFrame(inout SpeedTreeVertexInput input)
{
    int geometry = (int)(input.texcoord3.w + 0.25);
    if (geometry > GEOM_TYPE_FACINGLEAF)
        geometry -= 2;
    if (geometry <= GEOM_TYPE_FROND)
        return;

    // Fastest is the shader's default wind variant (no declared wind-quality
    // keyword). It is bound only to the authored distant 3D leaf materials.
    // Keep a soft hemispherical frame there without rebuilding an ellipsoid.
    #if !defined(_WINDQUALITY_NONE) && !defined(_WINDQUALITY_FAST) && \
        !defined(_WINDQUALITY_BETTER) && !defined(_WINDQUALITY_BEST) && \
        !defined(_WINDQUALITY_PALM)
        input.normal = AarisUnitOr(lerp(input.normal, float3(0.0, 1.0, 0.0), 0.62),
            float3(0.0, 1.0, 0.0));
        #if defined(EFFECT_BUMP)
            input.tangent.xyz = AarisCanopyTangentOS(input.tangent.xyz, input.normal);
        #endif
        return;
    #endif

    float3 anchor = float3(input.texcoord1.zw, input.texcoord2.w);
    float3 gradient = float3(anchor.x, max(0.12, abs(anchor.y) * 0.08), anchor.z);
    #if defined(ENABLE_WIND) && !defined(_WINDQUALITY_NONE)
        // Native inverse mesh height is independent of instance scale/LOD.
        // A short, broad cypress gets a rounder crown than a tall pine.
        float inverseHeight = _ST_WindGlobal.z;
        if (inverseHeight > 0.00001)
            gradient.y = (anchor.y - 0.60 / inverseHeight) *
                (0.72 * saturate(7.0 * inverseHeight));
    #endif
    gradient.y = max(gradient.y, -0.55 * max(abs(gradient.x), abs(gradient.z)));
    input.normal = AarisVolumeNormalOS(input.normal, gradient, 0.72);
    #if defined(EFFECT_BUMP)
        input.tangent.xyz = AarisCanopyTangentOS(input.tangent.xyz, input.normal);
    #endif
}
#endif

void AarisInitializeLitGeometry(inout SpeedTreeVertexInput input)
{
    #if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD) && defined(_WINDQUALITY_FAST)
        // Fast flutter must rotate the final canopy frame with the card.
        AarisTreeCanopyFrame(input);
    #endif
    InitializeData(input, unity_LODFade.x);
    #if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD) && !defined(_WINDQUALITY_FAST)
        // Unity's higher wind tiers use the original normal to move vertices.
        AarisTreeCanopyFrame(input);
    #endif
}

SpeedTreeVertexOutput SpeedTree8Vert(SpeedTreeVertexInput input)
{
    SpeedTreeVertexOutput output = (SpeedTreeVertexOutput)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    #if !defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
        // Relative world height before wind, measured from the instance pivot.
        float rootHeight = max(TransformObjectToWorldDir(input.vertex.xyz, false).y, 0.0);
    #endif

    AarisInitializeLitGeometry(input);

    output.uv = input.texcoord.xy;
    output.color = input.color;

    // color already contains (ao, ao, ao, blend)
    // put hue variation amount in there
    #ifdef EFFECT_HUE_VARIATION
        float3 treePos = float3(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[1].w, UNITY_MATRIX_M[2].w);
        float seed = frac(dot(treePos.xz, float2(0.1031, 0.11369)));
        float hueVariationAmount = frac(seed * (seed + 17.17));
        output.color.g = saturate(hueVariationAmount * _HueVariationColor.a);
    #endif

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.vertex.xyz);
    // Per-leaf chroma variation is calculated once per vertex, not per pixel.
    // Reuse the blue color interpolator (reserved for root AO on bark).
    // SpeedTree leaf anchors are stable while the already-authored wind moves.
    #if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
        float2 treePivotXZ = float2(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[2].w);
        float leafSeed = dot(treePivotXZ, float2(0.1031, 0.11369));
        #if !defined(_WINDQUALITY_NONE) && !defined(_WINDQUALITY_FAST) && \
            !defined(_WINDQUALITY_BETTER) && !defined(_WINDQUALITY_BEST) && \
            !defined(_WINDQUALITY_PALM)
            // Distant 3D LOD: broad per-tree breakup only.
        #else
            // Near leaves add stable local variation without object-to-world
            // anchor transforms; forest-scale breakup still comes from pivot WS.
            float3 leafAnchorOS = float3(input.texcoord1.z, input.texcoord1.w, input.texcoord2.w);
            leafSeed += dot(leafAnchorOS, float3(0.173, 0.117, 0.137));
        #endif
        leafSeed = frac(leafSeed);
        float leafTone = frac(leafSeed * (leafSeed + 17.17));
        // Blend fine leaf breakup with a broad world-space field. Both are
        // packed into the existing blue interpolator: no new sampler/varying.
        float macroTone = AarisForestMacroTone(treePivotXZ);
        output.color.b = (half)saturate(leafTone * 0.70 + macroTone * 0.30);
    #endif
    #if !defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
        // Reuse unused blue channel of existing vertex color interpolator.
        half contact = (half)saturate(1.0 - rootHeight * 0.76923077);
        // Match the smooth grounded contact curve used by the authored bark graph.
        output.color.b = contact * contact * (3.0h - 2.0h * contact);
    #endif
    half3 normalWS = TransformObjectToWorldNormal(input.normal);

    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalWS);
    half fogFactor = 0.0;
    #if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
    #endif
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);

    float3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);

    #ifdef EFFECT_BUMP
        real sign = input.tangent.w * GetOddNegativeScale();
        output.normalWS.xyz = normalWS;
        output.tangentWS.xyz = TransformObjectToWorldDir(input.tangent.xyz);
        output.bitangentWS.xyz = cross(output.normalWS.xyz, output.tangentWS.xyz) * sign;

        // View dir packed in w.
        output.normalWS.w = viewDirWS.x;
        output.tangentWS.w = viewDirWS.y;
        output.bitangentWS.w = viewDirWS.z;
    #else
        output.normalWS = normalWS;
        output.viewDirWS = viewDirWS;
    #endif

    output.positionWS = vertexInput.positionWS;

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        output.shadowCoord = GetShadowCoord(vertexInput);
    #endif

    output.clipPos = vertexInput.positionCS;

    OUTPUT_SH4(vertexInput.positionWS, normalWS, viewDirWS, output.vertexSH, output.probeOcclusion);

    return output;
}

SpeedTreeVertexDepthOutput SpeedTree8VertDepth(SpeedTreeVertexInput input)
{
    SpeedTreeVertexDepthOutput output = (SpeedTreeVertexDepthOutput)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // handle speedtree wind and lod
    InitializeData(input, unity_LODFade.x);
    output.uv = input.texcoord.xy;
    output.color = input.color;

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.vertex.xyz);

    output.viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);

#ifdef SHADOW_CASTER
    half3 normalWS = TransformObjectToWorldNormal(input.normal);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - vertexInput.positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(vertexInput.positionWS, normalWS, lightDirectionWS));
    output.clipPos = positionCS;
#else
    output.clipPos = vertexInput.positionCS;
#endif

    return output;
}

void InitializeInputData(SpeedTreeFragmentInput input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

    inputData.positionWS = input.interpolated.positionWS.xyz;
    inputData.positionCS = input.interpolated.clipPos;

#ifdef EFFECT_BUMP
    inputData.normalWS = TransformTangentToWorld(normalTS, half3x3(input.interpolated.tangentWS.xyz, input.interpolated.bitangentWS.xyz, input.interpolated.normalWS.xyz));
    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
    inputData.viewDirectionWS = half3(input.interpolated.normalWS.w, input.interpolated.tangentWS.w, input.interpolated.bitangentWS.w);
#else
    inputData.normalWS = NormalizeNormalPerPixel(input.interpolated.normalWS * normalTS.z);
    inputData.viewDirectionWS = input.interpolated.viewDirWS;
#endif

    inputData.viewDirectionWS = SafeNormalize(inputData.viewDirectionWS);

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        inputData.shadowCoord = input.interpolated.shadowCoord;
    #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
        inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
    #else
        inputData.shadowCoord = float4(0, 0, 0, 0);
    #endif

    inputData.fogCoord = InitializeInputDataFog(float4(input.interpolated.positionWS, 1.0), input.interpolated.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.interpolated.fogFactorAndVertexLight.yzw;
    inputData.shadowMask = half4(1, 1, 1, 1);
#if !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(input.interpolated.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        inputData.positionCS.xy,
        input.interpolated.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI = SAMPLE_GI(NOT_USED, input.interpolated.vertexSH, inputData.normalWS);
#endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.interpolated.clipPos);

    #if defined(DEBUG_DISPLAY) && !defined(LIGHTMAP_ON)
    inputData.vertexSH = input.interpolated.vertexSH;
    #if defined(USE_APV_PROBE_OCCLUSION)
    inputData.probeOcclusion = input.interpolated.probeOcclusion;
    #endif
    #endif

    #if defined(_NORMALMAP)
    inputData.tangentToWorld = half3x3(input.interpolated.tangentWS.xyz, input.interpolated.bitangentWS.xyz, input.interpolated.normalWS.xyz);
    #endif
}

#if defined(EFFECT_SUBSURFACE)
// One bounded sun-angle lobe for every leaf LOD. Wind quality is vertex-only,
// so fragment lighting must not branch on it. Reuse N.L for the golden edge.
half3 AarisLeafSun(half3 normalWS, half3 viewDirectionWS, Light light, half occlusion)
{
    half ndl = dot(normalWS, light.direction);
    half back = saturate(0.5h - 0.5h * ndl);
    half back2 = back * back;
    half rim = 1.0h - saturate(abs(ndl));
    rim *= rim;
    // Gate fake thin-leaf glow by sun-behind-view using the existing view vector.
    half viewBack = saturate(-dot(viewDirectionWS, light.direction));
    half viewLobe = 0.78h + 0.22h * viewBack * viewBack;
    half transmit = 0.34h * back2 * viewLobe + 0.06h * rim * (0.35h + 0.65h * back);
    half visibility = saturate(light.distanceAttenuation) * light.shadowAttenuation;
    half3 warmSunTint = half3(1.04h, 1.0h, 0.86h);
    return light.color * warmSunTint * (transmit * visibility * (0.6h + 0.4h * occlusion));
}

half3 AarisLeafSky(half3 normalWS, half3 bakedGI, half occlusion)
{
    half up = saturate(normalWS.y * 0.5h + 0.5h);
    half3 hemiTint = lerp(half3(1.035h, 0.99h, 0.92h),
                          half3(0.93h, 1.0h, 1.07h), up);
    half hemiStrength = 0.17h + 0.07h * up;
    return bakedGI * hemiTint * (_SubsurfaceIndirect * hemiStrength * occlusion);
}

#if !defined(GBUFFER)
// Match URP 17.3's PBR light accumulation. The leaf lobe shares the main
// Light already carrying the shadow, AO and cookie used by the lit surface.
half4 AarisForwardLeafPBR(InputData inputData, SurfaceData surfaceData)
{
    #if defined(_SPECULARHIGHLIGHTS_OFF)
        const bool specularHighlightsOff = true;
    #else
        const bool specularHighlightsOff = false;
    #endif

    BRDFData brdfData;
    InitializeBRDFData(surfaceData, brdfData);

    #if defined(DEBUG_DISPLAY)
        half4 debugColor;
        if (CanDebugOverrideOutputColor(inputData, surfaceData, brdfData, debugColor))
            return debugColor;
    #endif

    BRDFData clearCoat = CreateClearCoatBRDFData(surfaceData, brdfData);
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
    uint meshRenderingLayers = GetMeshRenderingLayer();
    Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

    LightingData lighting = CreateLightingData(inputData, surfaceData);
    lighting.giColor = GlobalIllumination(brdfData, clearCoat, surfaceData.clearCoatMask,
        inputData.bakedGI, aoFactor.indirectAmbientOcclusion, inputData.positionWS,
        inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);

    half3 leafColor = surfaceData.albedo * _SubsurfaceColor.rgb;
    lighting.giColor += leafColor * AarisLeafSky(inputData.normalWS, inputData.bakedGI, aoFactor.indirectAmbientOcclusion);
    #if defined(_LIGHT_LAYERS)
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
    #endif
    {
        lighting.mainLightColor = LightingPhysicallyBased(brdfData, clearCoat, mainLight,
            inputData.normalWS, inputData.viewDirectionWS, surfaceData.clearCoatMask, specularHighlightsOff);
        lighting.mainLightColor += leafColor * AarisLeafSun(inputData.normalWS, inputData.viewDirectionWS, mainLight, surfaceData.occlusion);
    }

    #if defined(_ADDITIONAL_LIGHTS)
        #if USE_CLUSTER_LIGHT_LOOP
        [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
        {
            CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
            Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);
            #if defined(_LIGHT_LAYERS)
            if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
            #endif
                lighting.additionalLightsColor += LightingPhysicallyBased(brdfData, clearCoat, light,
                    inputData.normalWS, inputData.viewDirectionWS, surfaceData.clearCoatMask, specularHighlightsOff);
        }
        #endif

        uint pixelLightCount = GetAdditionalLightsCount();
        LIGHT_LOOP_BEGIN(pixelLightCount)
            Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);
            #if defined(_LIGHT_LAYERS)
            if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
            #endif
                lighting.additionalLightsColor += LightingPhysicallyBased(brdfData, clearCoat, light,
                    inputData.normalWS, inputData.viewDirectionWS, surfaceData.clearCoatMask, specularHighlightsOff);
        LIGHT_LOOP_END
    #endif

    #if defined(_ADDITIONAL_LIGHTS_VERTEX)
        lighting.vertexLightingColor += inputData.vertexLighting * brdfData.diffuse;
    #endif

    half4 color = CalculateFinalColor(lighting, surfaceData.alpha);
    #if REAL_IS_HALF
        color = min(color, HALF_MAX);
    #endif
    return color;
}
#endif
#endif

#ifdef GBUFFER
GBufferFragOutput SpeedTree8Frag(SpeedTreeFragmentInput input)
#else
half4 SpeedTree8Frag(SpeedTreeFragmentInput input) : SV_Target
#endif
{
    UNITY_SETUP_INSTANCE_ID(input.interpolated);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input.interpolated);

    half2 uv = input.interpolated.uv;
    half4 diffuse = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_MainTex, sampler_MainTex)) * _Color;

    half alpha = diffuse.a * input.interpolated.color.a;
    alpha = AlphaDiscard(alpha, 0.3333);

    #ifdef LOD_FADE_CROSSFADE
        LODFadeCrossFade(input.interpolated.clipPos);
    #endif

    half3 albedo = diffuse.rgb;
    half3 emission = 0;
    half metallic = 0;
    half smoothness = 0;
    half occlusion = 0;
    half3 specular = 0;

    // hue variation
    #ifdef EFFECT_HUE_VARIATION
        half3 shiftedColor = lerp(albedo, _HueVariationColor.rgb, input.interpolated.color.g);

        // preserve vibrance
        half maxBase = max(albedo.r, max(albedo.g, albedo.b));
        half newMaxBase = max(shiftedColor.r, max(shiftedColor.g, shiftedColor.b));
        // Near-black cutout texels can otherwise produce 0 / 0 (NaN).
        // Preserve the authored hue response with one cheap safe denominator;
        // no new samples, interpolators, shader variants or passes.
        maxBase /= max(newMaxBase, 0.0001h);
        maxBase = maxBase * 0.5h + 0.5h;
        shiftedColor.rgb *= maxBase;

        albedo = saturate(shiftedColor);
    #endif

    // Fine, deterministic leaf-to-leaf color variance. No new material,
    // texture fetch, shader variant, interpolator or draw call.
    #if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
        half leafDetail = input.interpolated.color.b - 0.5h;
        // More visible but restrained golden-green and deep-green variation,
        // still using the same packed vertex channel and exactly zero samplers.
        albedo *= half3(1.0h + leafDetail * 0.085h,
                        1.0h + leafDetail * 0.075h,
                        1.0h - leafDetail * 0.050h);
    #endif

    #if !defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
        half rootContact = saturate(input.interpolated.color.b);
    #endif

    // normal
    #ifdef EFFECT_BUMP
        half3 normalTs = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap));
        #if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
            normalTs.xy *= 0.74h; // Preserve leaf texture detail without plastic card highlights.
        #elif !defined(EFFECT_BILLBOARD)
            // Sharpen bark away from soil while softening micro-relief exactly
            // where fake root AO/earth tint visually anchors the trunk.
            normalTs.xy *= 1.20h - 0.12h * rootContact;
        #endif
    #else
        half3 normalTs = half3(0, 0, 1);
    #endif

    // flip normal on backsides
    #if defined(EFFECT_BACKSIDE_NORMALS) && (!defined(EFFECT_SUBSURFACE) || defined(EFFECT_BILLBOARD))
        normalTs.z = IS_FRONT_VFACE(input.facing, normalTs.z, -normalTs.z);
    #endif

    // adjust billboard normals to improve GI and matching
    #ifdef EFFECT_BILLBOARD
        normalTs.z *= 0.5;
        normalTs = normalize(normalTs);
    #endif

    // extra
    #ifdef EFFECT_EXTRA_TEX
        half4 extra = tex2D(_ExtraTex, uv);
        smoothness = extra.r;
        metallic = extra.g;
        occlusion = extra.b * input.interpolated.color.r;
    #else
        smoothness = _Glossiness;
        metallic = _Metallic;
        occlusion = input.interpolated.color.r;
    #endif

    #if !defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
        // Warm, earthy bark at the ground interface; preserves original bark texture.
        albedo *= lerp(half3(1.0h, 1.0h, 1.0h), half3(0.84h, 0.80h, 0.73h), rootContact);
        occlusion *= 1.0h - 0.20h * rootContact;
        smoothness *= 1.0h - 0.16h * rootContact; // Soil contact reads dry, not varnished.
    #endif
    #if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
        // Leaves are dielectric. Keep wet/waxy highlights bounded by the
        // existing roughness texture instead of reflecting like bright plastic.
        metallic = 0.0h;
        smoothness = min(smoothness, 0.48h);
    #endif
    occlusion = saturate(occlusion);
    InputData inputData;
    InitializeInputData(input, normalTs, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, input.interpolated.uv);

#if defined(GBUFFER)
    // Reuse the deferred GI light for sun transmission, too.
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    // in LitForwardPass GlobalIllumination (and temporarily LightingPhysicallyBased) are called inside UniversalFragmentPBR
    // in Deferred rendering we store the sum of these values (and of emission as well) in the GBuffer
    BRDFData brdfData;
    InitializeBRDFData(albedo, metallic, specular, smoothness, alpha, brdfData);

    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
    #if defined(EFFECT_SUBSURFACE)
        emission = albedo * _SubsurfaceColor.rgb *
            (AarisLeafSun(inputData.normalWS, inputData.viewDirectionWS, mainLight, occlusion) + AarisLeafSky(inputData.normalWS, inputData.bakedGI, occlusion));
    #endif
    half3 color = GlobalIllumination(brdfData, (BRDFData)0, 0, inputData.bakedGI, occlusion, inputData.positionWS,
                                     inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);

    return PackGBuffersBRDFData(brdfData, inputData, smoothness, emission + color, occlusion);

#else
    SurfaceData surfaceData;

    surfaceData.albedo = albedo;
    surfaceData.specular = specular;
    surfaceData.metallic = metallic;
    surfaceData.smoothness = smoothness;
    surfaceData.normalTS = normalTs;
    surfaceData.emission = emission;
    surfaceData.occlusion = occlusion;
    surfaceData.alpha = alpha;
    surfaceData.clearCoatMask = 0;
    surfaceData.clearCoatSmoothness = 1;

#if defined(DEBUG_DISPLAY)
    inputData.uv = uv;
#endif

    #if defined(EFFECT_SUBSURFACE)
        half4 color = AarisForwardLeafPBR(inputData, surfaceData);
    #else
        half4 color = UniversalFragmentPBR(inputData, surfaceData);
    #endif

    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, _Surface);

    return color;

#endif
}

half4 SpeedTree8FragDepth(SpeedTreeVertexDepthOutput input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half2 uv = input.uv;
    half4 diffuse = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_MainTex, sampler_MainTex)) * _Color;

    half alpha = diffuse.a * input.color.a;
    AlphaDiscard(alpha, 0.3333);

    #ifdef LOD_FADE_CROSSFADE
        LODFadeCrossFade(input.clipPos);
    #endif

    #if defined(SCENESELECTIONPASS)
        // We use depth prepass for scene selection in the editor, this code allow to output the outline correctly
        return half4(_ObjectId, _PassValue, 1.0, 1.0);
    #else
        return half4(input.clipPos.z, 0, 0, 0);
    #endif
}

SpeedTreeVertexDepthNormalOutput SpeedTree8VertDepthNormal(SpeedTreeVertexInput input)
{
    SpeedTreeVertexDepthNormalOutput output = (SpeedTreeVertexDepthNormalOutput)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    AarisInitializeLitGeometry(input);
    output.uv = input.texcoord.xy;
    output.color = input.color;

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.vertex.xyz);
    half3 normalWS = TransformObjectToWorldNormal(input.normal);
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
    #ifdef EFFECT_BUMP
        real sign = input.tangent.w * GetOddNegativeScale();
        output.normalWS.xyz = normalWS;
        output.tangentWS.xyz = TransformObjectToWorldDir(input.tangent.xyz);
        output.bitangentWS.xyz = cross(output.normalWS.xyz, output.tangentWS.xyz) * sign;

        // View dir packed in w.
        output.normalWS.w = viewDirWS.x;
        output.tangentWS.w = viewDirWS.y;
        output.bitangentWS.w = viewDirWS.z;
    #else
        output.normalWS = normalWS;
        output.viewDirWS = viewDirWS;
    #endif

    output.clipPos = vertexInput.positionCS;
    return output;
}

half4 SpeedTree8FragDepthNormal(SpeedTreeDepthNormalFragmentInput input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input.interpolated);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input.interpolated);

    half2 uv = input.interpolated.uv;
    half4 diffuse = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_MainTex, sampler_MainTex)) * _Color;

    half alpha = diffuse.a * input.interpolated.color.a;
    AlphaDiscard(alpha, 0.3333);

    #ifdef LOD_FADE_CROSSFADE
        LODFadeCrossFade(input.interpolated.clipPos);
    #endif

    // normal
    #if defined(EFFECT_BUMP)
        half3 normalTs = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap));
        #if defined(EFFECT_SUBSURFACE) && !defined(EFFECT_BILLBOARD)
            normalTs.xy *= 0.74h;
        #elif !defined(EFFECT_BILLBOARD)
            half rootContact = saturate(input.interpolated.color.b);
            normalTs.xy *= 1.20h - 0.12h * rootContact;
        #endif
    #else
        half3 normalTs = half3(0, 0, 1);
    #endif

    // Same two-sided canopy basis as ForwardLit; non-leaf cards still flip.
    #if defined(EFFECT_BACKSIDE_NORMALS) && (!defined(EFFECT_SUBSURFACE) || defined(EFFECT_BILLBOARD))
        normalTs.z = IS_FRONT_VFACE(input.facing, normalTs.z, -normalTs.z);
    #endif

    // adjust billboard normals to improve GI and matching
    #if defined(EFFECT_BILLBOARD)
        normalTs.z *= 0.5;
        normalTs = normalize(normalTs);
    #endif

    #if defined(EFFECT_BUMP)
        float3 normalWS = TransformTangentToWorld(normalTs, half3x3(input.interpolated.tangentWS.xyz, input.interpolated.bitangentWS.xyz, input.interpolated.normalWS.xyz));
        return half4(NormalizeNormalPerPixel(normalWS), 0.0h);
    #else
        return half4(NormalizeNormalPerPixel(input.interpolated.normalWS * normalTs.z), 0.0h);
    #endif
}

#endif
