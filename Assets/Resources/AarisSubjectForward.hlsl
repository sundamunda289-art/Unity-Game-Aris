// com.unity.render-pipelines.universal copyright (c) 2020 Unity Technologies ApS
// Derived URP 17.3 declarations licensed under Unity Companion License for Unity-dependent projects.
// https://unity.com/legal/licenses/unity-companion-license
// URP 17.3 stock Lit fragment flow; only the subject BRDF contribution differs.
#ifndef AARIS_SUBJECT_FORWARD_INCLUDED
#define AARIS_SUBJECT_FORWARD_INCLUDED
#if defined(_AARIS_HERO_FACE) || defined(_AARIS_HERO_GEAR)
#include "AarisHeroSurface.hlsl"
#endif
void AarisLitPassFragment(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_PARALLAXMAP)
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS = input.viewDirTS;
#else
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
#endif
    ApplyPerPixelDisplacement(viewDirTS, input.uv);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(input.uv, surfaceData);

#if defined(_AARIS_HERO_FACE)
    float2 atlasUV = UNDO_TRANSFORM_TEX(input.uv, _BaseMap);
    half3 heroMasks = AarisHeroFaceMasks(atlasUV, surfaceData.albedo);
    AarisHeroFaceSurface(heroMasks, surfaceData);
#elif defined(_AARIS_HERO_GEAR)
    // Reuse the world position already carried by the Lit pass.
    float3 eyeDelta = input.positionWS - GetCameraPositionWS();
    AarisHeroGearSurface(input.uv, dot(eyeDelta, eyeDelta), surfaceData);
#endif

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    InitializeBakedGIData(input, inputData);
#if defined(_AARIS_GT_TREAD)
    // The GT OBJ has no UVs, but its roll axis is authored on local X.
    // Angular tread follows the real spinning mesh. Derivatives fade the
    // grooves when they become subpixel, without a texture or extra pass.
    float3 tyre = TransformWorldToObject(inputData.positionWS);
    float treadPhase = atan2(tyre.y, tyre.z) * (38.0 / 6.2831853) + abs(tyre.x) * 4.8;
    float treadWidth = max(fwidth(treadPhase), 0.015);
    half treadCut = (1.0h - smoothstep(0.10, 0.10 + treadWidth, abs(frac(treadPhase) - 0.5)))
        * (1.0h - smoothstep(0.16, 0.48, treadWidth));
    float longitudinalPhase = abs(tyre.x) * 16.0;
    float grooveWidth = max(fwidth(longitudinalPhase), 0.015);
    half longitudinal = (1.0h - smoothstep(0.075, 0.075 + grooveWidth,
        abs(frac(longitudinalPhase) - 0.5))) * (1.0h - smoothstep(0.18, 0.46, grooveWidth));
    half3 axle = TransformObjectToWorldDir(float3(1.0, 0.0, 0.0));
    half crown = 1.0h - smoothstep(0.46h, 0.86h, abs(dot(axle, inputData.normalWS)));
    half exposed = max(treadCut * 0.75h, longitudinal) * crown;
    surfaceData.albedo *= 1.0h - 0.36h * exposed;
    surfaceData.smoothness *= 1.0h - 0.44h * exposed;
#endif
#if defined(_AARIS_AIRFRAME)
    // The transport mesh is authored in metres. Paint moves with the airframe.
    // Derivative-width boundaries soften subpixel stripes at flight distance.
    float3 airframe = TransformWorldToObject(input.positionWS);
    float span = abs(airframe.x);
    float station = airframe.z - 0.55 * airframe.y;
    float stationEdge = max(fwidth(station), 0.03);
    half centreline = 1.0 - smoothstep(1.45, 1.75, span);

    // The transport's sidewall sits below the wing-root datum. Place the
    // stripe along that wall and stop it before the radome and tail blocks.
    float belt = airframe.y + 1.07 - 0.032 * airframe.z;
    float edge = max(fwidth(belt), 0.014);
    half sidewall = centreline
        * smoothstep(-5.85 - stationEdge, -5.85 + stationEdge, station)
        * (1.0 - smoothstep(7.8 - stationEdge, 7.8 + stationEdge, station));
    half waist = (1.0 - smoothstep(0.24 - edge, 0.24 + edge, abs(belt))) * sidewall;
    half pinstripe = (1.0 - smoothstep(0.048, 0.048 + edge, abs(belt - 0.31))) * sidewall;

    float wingEdge = max(fwidth(span), 0.025);
    half tips = smoothstep(9.55 - wingEdge, 9.55 + wingEdge, span);
    half wingBand = 1.0 - smoothstep(0.12, 0.12 + wingEdge, abs(span - 9.10));
    half tail = 1.0 - smoothstep(-6.2 - stationEdge, -6.2 + stationEdge, station);
    half radome = smoothstep(8.6 - stationEdge, 8.6 + stationEdge, station);
    half aftBand = 1.0 - smoothstep(0.18, 0.18 + stationEdge, abs(station + 5.65));
    half enamel = max(max(waist, tips), max(radome, tail) * centreline);
    // Retain the upper aft trim where a fly-by mostly sees the wing and tail.
    float upperBelt = airframe.y - 0.09 - 0.065 * airframe.z;
    half upperTrim = (1.0 - smoothstep(0.065, 0.065 + edge, abs(upperBelt)))
        * (1.0 - smoothstep(1.30, 1.48, span))
        * (1.0 - smoothstep(1.8, 3.0, station));
    half accent = max(max(pinstripe, upperTrim), max(wingBand, aftBand * centreline));
    half belly = (1.0 - smoothstep(-0.08 - edge, -0.08 + edge, belt)) * centreline;
    float jointDistance = min(abs(station - 5.9), abs(station + 2.8));
    half panelJoint = (1.0 - smoothstep(0.024, 0.024 + stationEdge, jointDistance))
        * saturate(1.0 - 3.5 * stationEdge) * centreline;
    surfaceData.albedo *= 1.0h - 0.36h * belly;
    surfaceData.albedo = lerp(surfaceData.albedo, half3(0.026h, 0.066h, 0.095h), enamel);
    surfaceData.albedo = lerp(surfaceData.albedo, half3(0.62h, 0.30h, 0.075h), accent);
    surfaceData.albedo *= 1.0h - 0.27h * panelJoint;
    surfaceData.smoothness = lerp(surfaceData.smoothness, 0.72h, enamel * 0.74h);
    // The serialized daylight colour is used even when runtime-only
    // emission keywords are stripped from a mobile player build.
    half painted = max(enamel, accent);
    surfaceData.emission = _EmissionColor.rgb * (1.0h - max(0.28h * belly, 0.82h * painted));
#endif

#if defined(_AARIS_HERO_FACE)
    half4 color = AarisHeroPBR(inputData, surfaceData, heroMasks);
#elif defined(_AARIS_HERO_GEAR)
    half4 color = AarisHeroPBR(inputData, surfaceData, half3(0.0h, 0.0h, 0.0h));
#elif defined(_AARIS_GT_TREAD)
    half4 color = UniversalFragmentPBR(inputData, surfaceData);
#else
    half4 color = AarisSubjectPBR(inputData, surfaceData);
#endif
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));

    outColor = color;

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
