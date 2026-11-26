// com.unity.render-pipelines.universal copyright (c) 2020 Unity Technologies ApS
// Derived URP 17.3 declarations licensed under Unity Companion License for Unity-dependent projects.
// https://unity.com/legal/licenses/unity-companion-license
// URP 17.3 stock Lit fragment flow; only the subject BRDF contribution differs.
#ifndef AARIS_SUBJECT_FORWARD_INCLUDED
#define AARIS_SUBJECT_FORWARD_INCLUDED
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
#if defined(_AARIS_AIRFRAME)
    // The transport mesh is authored in metres. Paint moves with the airframe.
    // Derivative-width boundaries soften subpixel stripes at flight distance.
    float3 airframe = TransformWorldToObject(input.positionWS);
    float span = abs(airframe.x);
    float belt = airframe.y + 0.23 - 0.065 * airframe.z;
    float edge = max(fwidth(belt), 0.012);
    half waist = (1.0 - smoothstep(0.24 - edge, 0.24 + edge, abs(belt)))
        * (1.0 - smoothstep(1.30, 1.48, span));
    float wingEdge = max(fwidth(span), 0.025);
    half tips = smoothstep(9.55 - wingEdge, 9.55 + wingEdge, span);
    half pinstripe = (1.0 - smoothstep(0.065, 0.065 + edge, abs(belt - 0.32)))
        * (1.0 - smoothstep(1.30, 1.48, span));
    half wingBand = 1.0 - smoothstep(0.12, 0.12 + wingEdge, abs(span - 9.10));
    // Broad tail and nose blocks remain recognizable when the belt is subpixel.
    float station = airframe.z - 0.55 * airframe.y;
    float stationEdge = max(fwidth(station), 0.03);
    half centreline = 1.0 - smoothstep(1.45, 1.75, span);
    half tail = 1.0 - smoothstep(-6.2 - stationEdge, -6.2 + stationEdge, station);
    half radome = smoothstep(8.6 - stationEdge, 8.6 + stationEdge, station);
    half aftBand = 1.0 - smoothstep(0.18, 0.18 + stationEdge, abs(station + 5.65));
    half enamel = max(max(waist, tips), max(radome, tail) * centreline);
    half accent = max(max(pinstripe, wingBand), aftBand * centreline);
    surfaceData.albedo = lerp(surfaceData.albedo, half3(0.022h, 0.057h, 0.084h), enamel);
    surfaceData.albedo = lerp(surfaceData.albedo, half3(0.73h, 0.32h, 0.063h), accent);
    // The serialized daylight colour is used even when runtime-only
    // emission keywords are stripped from a mobile player build.
    surfaceData.emission = _EmissionColor.rgb * (1.0h - 0.82h * max(enamel, accent));
#endif

    half4 color = AarisSubjectPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));

    outColor = color;

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
