#ifndef AARIS_SUBJECT_LIGHTING_INCLUDED
#define AARIS_SUBJECT_LIGHTING_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
// One bounded contribution BEFORE fog. Existing BRDF, normals, shadows and
// GI stay intact. No additional texture sample, light, probe or color pass.
half4 AarisSubjectPBR(InputData inputData, SurfaceData surfaceData)
{
    half4 result = UniversalFragmentPBR(inputData, surfaceData);
#if !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(DEBUG_DISPLAY)
    half rim = 1.0h - saturate(dot(inputData.normalWS, inputData.viewDirectionWS));
    rim *= rim;
    half metal = saturate(surfaceData.metallic);
    half smoothness = saturate(surfaceData.smoothness);
    half headroom = saturate(1.0h - max(result.r, max(result.g, result.b)) * 0.65h);
    half3 fill = surfaceData.albedo * (0.065h + 0.26h * rim) * (1.0h - 0.45h * metal);
#if !defined(_ENVIRONMENTREFLECTIONS_OFF) && !defined(_SPECULARHIGHLIGHTS_OFF)
    half3 reflected = reflect(-inputData.viewDirectionWS, inputData.normalWS);
    half skyHeight = saturate(reflected.y * 0.5h + 0.5h);
    half3 sky = lerp(half3(0.24h,0.25h,0.23h), half3(0.58h,0.72h,0.88h), skyHeight);
    half horizon = saturate(1.0h - abs(reflected.y - 0.25h) * 2.7h);
    horizon *= horizon;
    half3 f0 = lerp(half3(0.04h,0.04h,0.04h), surfaceData.albedo, metal);
    // Smooth dielectric surfaces retain an independent Fresnel edge;
    // a dark painted gun/vehicle no longer loses all silhouette in shade.
    half gloss = smoothness * smoothness;
    half coat = saturate(_ClearCoatMask * _ClearCoatSmoothness);
    half skyLobe = gloss * (0.035h + 0.19h * horizon + 0.12h * rim);
    half coatLobe = coat * (0.045h + 0.16h * horizon);
    fill += sky * (f0 * (0.35h + 0.50h * horizon) * smoothness
        + skyLobe + coatLobe + 0.035h * rim);
#endif
    result.rgb += fill * headroom * surfaceData.occlusion;
#endif
    return result;
}
#endif
