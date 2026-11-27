#ifndef AARIS_HERO_SURFACE_INCLUDED
#define AARIS_HERO_SURFACE_INCLUDED

#if defined(_AARIS_HERO_FACE)
// The shared eye atlas island sits below the skin, at the left edge.
// Restrict the dark-pixel sheen to the beard area plus the authored scalp.
half3 AarisHeroFaceMasks(float2 uv, half3 albedo)
{
    half eye = (1.0h - smoothstep(0.279, 0.291, uv.x))
        * (1.0h - smoothstep(0.232, 0.247, uv.y));
    half skinIsland = smoothstep(0.245, 0.264, uv.y);
    half beardArea = smoothstep(0.485, 0.56, uv.y)
        * (1.0h - smoothstep(0.675, 0.731, uv.y))
        * (1.0h - smoothstep(0.145, 0.219, abs(uv.x - 0.50)));
    half luma = dot(albedo, half3(0.2126h, 0.7152h, 0.0722h));
    half stubble = beardArea * (1.0h - smoothstep(0.041h, 0.21h, luma));
    half hair = max(stubble * 0.72h, smoothstep(0.834, 0.917, uv.y));
    return half3(skinIsland * (1.0h - hair), eye, hair);
}

void AarisHeroFaceSurface(half3 masks, inout SurfaceData surface)
{
    // The existing opaque albedo sample also holds the authored cavity in A.
    // Keep direct light readable and retain the shader's opaque output alpha.
    half cavity = lerp(1.0h, max(surface.alpha, 0.70h), masks.x);
    surface.alpha = 1.0h;
    surface.occlusion *= cavity;
    surface.albedo *= lerp(0.84h, 1.0h, cavity);
    surface.smoothness = lerp(surface.smoothness, 0.28h, masks.x);
    surface.smoothness = lerp(surface.smoothness, 0.54h, masks.y);
    surface.smoothness = lerp(surface.smoothness, 0.26h, masks.z);
    // Retain pupil depth, and control the red already baked into the eye rims.
    half eyeRed = max(0.0h, surface.albedo.r - surface.albedo.g);
    surface.albedo.r -= eyeRed * (0.26h * masks.y);
}
#endif

#if defined(_AARIS_HERO_GEAR)
void AarisHeroGearSurface(float2 uv, float eyeDistanceSquared, inout SurfaceData surface)
{
    half2 normalXY = surface.normalTS.xy;
    half slope = saturate(dot(normalXY, normalXY) * 1.17h);
    half fabric = saturate((0.40h - surface.smoothness) * 3.6h)
        * (1.0h - saturate(surface.metallic * 1.8h));
    // Use the authored fold normal as a cavity proxy. Direct diffuse and the
    // native shadows still carry the large folds, seams and plates.
    half cavity = 1.0h - slope * (0.16h + 0.17h * fabric);
    surface.occlusion *= cavity;
    surface.albedo *= lerp(0.91h, 1.0h, cavity);

    // Keep weave for close-ups. UV filtering handles grazing angles,
    // and eye depth fades it before the third-person camera sees a pattern.
    float2 warp = uv * float2(168.0, 194.0);
    float2 footprint = fwidth(warp);
    half resolved = 1.0h - smoothstep(0.12, 0.35, max(footprint.x, footprint.y));
    half2 thread = abs(frac(warp) * 2.0 - 1.0) - 0.5;
    half nearWeave = 1.0h - smoothstep(1.21, 4.20, eyeDistanceSquared);
    surface.normalTS.xy += half2(thread.y, thread.x) * (0.048h * fabric * resolved * nearWeave);
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
    half rim = edge * edge * edge * saturate(0.05h + facingSun * 1.15h);
    half3 diffuseFill = surface.albedo * (0.043h + 0.056h * max(normal.y, 0.0h));
    half3 direct = sun.color * (rim * 0.082h) * (surface.albedo + 0.09h);
#if defined(_AARIS_HERO_FACE)
    half wrapped = saturate((facingSun + 0.30h) * 0.76923h);
    half terminator = max(0.0h, wrapped - saturate(facingSun));
    half skinWarmth = masks.x * terminator * 0.42h;
    direct += sun.color * surface.albedo * half3(1.0h, 0.57h, 0.39h) * skinWarmth;

    half3 halfDirection = SafeNormalize(view + sun.direction);
    half reflection = saturate(dot(normal, halfDirection));
    half broad = reflection * reflection;
    broad *= broad;   // x^4
    broad *= broad;   // x^8
    half shoulder = broad * broad;
    shoulder *= shoulder; // x^32
    half tight = shoulder * shoulder; // x^64
    half eyeGlint = masks.y * (tight * 0.36h + shoulder * 0.09h)
        * saturate(facingSun * 3.0h);
    direct += sun.color * eyeGlint;

    // Root-to-tip response for the hair/stubble atlas, using the tangent
    // reconstructed by the existing normal-mapped URP interpolator.
#if defined(_NORMALMAP)
    half sideAngle = dot(input.tangentToWorld[0], halfDirection);
    half strand = saturate(1.0h - sideAngle * sideAngle * 1.75h);
    strand *= strand;
    strand *= strand;
    direct += sun.color * (0.051h * masks.z * strand * broad);
#endif
#endif
    result.rgb += (diffuseFill + direct * visibleLight) * surface.occlusion * headroom;
#endif
    return result;
}
#endif
