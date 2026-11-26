#ifndef AARIS_HERO_SURFACE_INCLUDED
#define AARIS_HERO_SURFACE_INCLUDED

#if defined(_AARIS_HERO_FACE)
// The face atlas reserves a bottom strip for the eyes. Its upper strip
// contains the scanned skin, with authored stubble and a short scalp pattern.
half3 AarisHeroFaceMasks(float2 uv, half3 albedo)
{
    half eye = step(uv.x, 0.29) * step(uv.y, 0.245);
    half face = step(0.25, uv.y);
    half luma = dot(albedo, half3(0.2126h, 0.7152h, 0.0722h));
    half facialHair = 1.0h - smoothstep(0.037h, 0.17h, luma);
    half hair = face * max(facialHair, smoothstep(0.88h, 0.94h, uv.y));
    return half3(face * (1.0h - hair), eye, hair);
}

void AarisHeroFaceSurface(half3 masks, inout SurfaceData surface)
{
    surface.smoothness = lerp(surface.smoothness, 0.34h, masks.x);
    surface.smoothness = lerp(surface.smoothness, 0.47h, masks.y);
    surface.smoothness = lerp(surface.smoothness, 0.31h, masks.z);
    // Retain pupil depth, and control the red already baked into the eye rims.
    half eyeRed = max(0.0h, surface.albedo.r - surface.albedo.g);
    surface.albedo.r -= eyeRed * (0.26h * masks.y);
}
#endif

#if defined(_AARIS_HERO_GEAR)
void AarisHeroGearSurface(float2 uv, inout SurfaceData surface)
{
    half2 normalXY = surface.normalTS.xy;
    half slope = saturate(dot(normalXY, normalXY) * 1.17h);
    half fabric = saturate((0.40h - surface.smoothness) * 3.6h)
        * (1.0h - saturate(surface.metallic * 1.8h));
    // Use the authored fold normal as a cavity proxy. Direct diffuse and the
    // native shadows still carry the large folds, seams and plates.
    half cavity = 1.0h - slope * (0.16h + 0.14h * fabric);
    surface.occlusion *= cavity;
    surface.albedo *= lerp(0.91h, 1.0h, cavity);

    // A derivative-filtered weave. It fades before the UV frequency aliases,
    // uses no additional texture fetch, and leaves the metal and plates smooth.
    float2 warp = uv * float2(168.0, 194.0);
    float2 footprint = fwidth(warp);
    half resolved = 1.0h - smoothstep(0.20, 0.70, max(footprint.x, footprint.y));
    half2 thread = abs(frac(warp) * 2.0 - 1.0) - 0.5;
    surface.normalTS.xy += half2(thread.y, thread.x) * (0.12h * fabric * resolved);
}
#endif

half4 AarisHeroPBR(InputData input, SurfaceData surface, half3 masks)
{
    half4 result = UniversalFragmentPBR(input, surface);
#if !defined(DEBUG_DISPLAY)
    // The standard pass already owns the diffuse/specular shadow map sample.
    // The hero's small fill terms use its light direction and a bounded
    // luminance proxy from shaded output, with no second shadow lookup.
    Light sun = GetMainLight();
    half3 normal = input.normalWS;
    half3 view = input.viewDirectionWS;
    half facingSun = dot(normal, sun.direction);
    half luma = dot(result.rgb, half3(0.2126h, 0.7152h, 0.0722h));
    half visibleLight = saturate(0.14h + luma * 2.75h);
    half headroom = saturate(1.0h - max(result.r, max(result.g, result.b)) * 0.75h);
    half edge = 1.0h - saturate(dot(normal, view));
    half rim = edge * edge * edge * saturate(0.08h + facingSun * 1.2h);
    half3 diffuseFill = surface.albedo * (0.055h + 0.075h * max(normal.y, 0.0h));
    half3 direct = sun.color * (rim * 0.082h) * (surface.albedo + 0.12h);
#if defined(_AARIS_HERO_FACE)
    half wrap = saturate(0.30h + facingSun * 0.90h) - saturate(facingSun) * 0.55h;
    half poreWarmth = masks.x * wrap * 0.27h;
    direct += sun.color * surface.albedo * half3(1.0h, 0.37h, 0.17h) * poreWarmth;

    half3 halfDirection = SafeNormalize(view + sun.direction);
    half reflection = saturate(dot(normal, halfDirection));
    half broad = reflection * reflection;
    broad *= broad;   // x^4
    broad *= broad;   // x^8
    half tight = broad * broad;
    tight *= tight;   // x^32
    tight *= tight;   // x^64
    half eyeGlint = masks.y * tight * saturate(facingSun * 3.0h) * 0.24h;
    direct += sun.color * eyeGlint;

    // Root-to-tip response for the hair/stubble atlas, using the tangent
    // reconstructed by the existing normal-mapped URP interpolator.
#if defined(_NORMALMAP)
    half sideAngle = dot(input.tangentToWorld[0], halfDirection);
    half strand = saturate(1.0h - sideAngle * sideAngle * 1.75h);
    strand *= strand;
    strand *= strand;
    direct += sun.color * (0.043h * masks.z * strand * broad);
#endif
#endif
    result.rgb += (diffuseFill + direct * visibleLight) * surface.occlusion * headroom;
#endif
    return result;
}
#endif
