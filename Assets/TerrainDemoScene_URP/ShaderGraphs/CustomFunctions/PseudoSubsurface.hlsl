#ifndef AARIS_PSEUDO_SUBSURFACE_INCLUDED
#define AARIS_PSEUDO_SUBSURFACE_INCLUDED

void AarisPackDetailGround_float(float3 TerrainColor, float3 RestPositionOS,
    float GroundHeight, out float4 Out)
{
    Out = float4(TerrainColor, saturate(RestPositionOS.y / max(GroundHeight, 0.05)));
}

// Carry rest height with the existing vertex-sampled soil colour. AO and
// root tint stay anchored while wind and contact move the drawn vertex.
void AarisDetailGround_half(half3 LeafColor, half4 TerrainColorAndHeight, half DistanceFade,
    half InstanceSeed, half4 Profile, out half3 Out, out half3 TransmissionColor, out half Height01)
{
    Height01 = TerrainColorAndHeight.w;
    half3 terrainColor = TerrainColorAndHeight.rgb;
    half fade = saturate(DistanceFade);
    half contact = saturate(1.0h - 2.0h * Height01);
    half rootBlend = 0.38h * contact * contact * (3.0h - 2.0h * contact);
    // Ordinary grass uses the neutral canopy profile. Keep that dominant
    // detail draw out of shrub-only chroma ALU; Profile is uniform per
    // instanced material, so this branch does not diverge within a draw.
    half3 localColor = LeafColor * Profile.w;
    UNITY_BRANCH if (Profile.x > 0.0h)
    {
        half variation = (saturate(InstanceSeed) - 0.5h) * saturate(Profile.x);
        localColor *= 1.0h + variation * half3(0.10h, 0.07h, -0.05h);
    }
    half3 rootedColor = lerp(localColor, terrainColor * half3(0.88h, 0.83h, 0.75h), rootBlend);
    Out = lerp(rootedColor, terrainColor, fade);
    // Suppress artificial emission near soil and far silhouettes. Existing
    // thickness, AO and normal textures continue to define the detail.
    half heightMask = Height01 * Height01 * (3.0h - 2.0h * Height01);
    TransmissionColor = Out * (0.20h + 0.80h * heightMask) *
        (1.0h - 0.75h * fade);
}

void AarisDetailKeepCanopyNormal_half(bool FrontFace, half4 Profile, out bool Out)
{
    // Canopy lighting uses a fixed outward frame on both sides of a card.
    Out = FrontFace || Profile.x > 0.001h;
}

// Bounded, warm transmission from the scene's existing directional light.
// URP Lit handles real direct-light shadows; this inexpensive approximate
// lobe must not imply an independent realtime shadow sample or self-glow.
void PseudoSubsurface_half(float3 WorldPosition, half3 WorldNormal, half3 SSRadius,
                           half ShadowResponse, out half3 ssAmount)
{
#ifdef SHADERGRAPH_PREVIEW
    half3 color = half3(0.0h, 0.0h, 0.0h);
    half3 ambient = half3(0.0h, 0.0h, 0.0h);
    half3 lightDir = normalize(half3(0.707h, 0.5h, 0.707h));
#else
    // Far detail cards keep normal URP Lit, but skip this additive illusion.
    // Squared distance avoids sqrt and the branch is spatially coherent.
    float3 cameraDelta = WorldPosition - GetCameraPositionWS();
    float distanceSq = dot(cameraDelta, cameraDelta);
    UNITY_BRANCH if (distanceSq >= 8100.0)
    {
        ssAmount = 0.0h;
        return;
    }
    // Full fake transmission through 45 m, smoothly gone by 90 m.
    half illusionFade = (half)saturate((8100.0 - distanceSq) * 0.00016460905);
    illusionFade = illusionFade * illusionFade * (3.0h - 2.0h * illusionFade);
    Light mainLight = GetMainLight();
    half3 color = mainLight.color * saturate(mainLight.distanceAttenuation);
    half3 lightDir = mainLight.direction;
    half3 ambient = max(half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w), 0.0h);
#endif
    half3 n = SafeNormalize(WorldNormal);
    // Authored SSS values range from 0.5 to 4, not just 0 to 1.
    half radius = saturate(0.25h * SSRadius.x);
    half ndl = dot(n, lightDir);
    half back = saturate((radius - ndl) / (1.0h + radius));
    half wrapped = back * back * (3.0h - 2.0h * back);
    half edge = 1.0h - saturate(abs(ndl));
    half rim = edge * edge;
    half up = saturate(n.y * 0.5h + 0.5h);
    // Hemisphere-only fill costs no extra light/shadow lookup.
    half skyFill = 0.035h + 0.020h * up;
    half3 hemiFill = lerp(half3(1.035h, 0.99h, 0.92h),
                          half3(0.94h, 1.0h, 1.06h), up);
    half transmission = (0.14h + 0.23h * radius) * wrapped + 0.10h * rim;
    half3 warmSun = half3(1.04h, 1.0h, 0.86h);
    half response = 1.0h - 0.20h * saturate(ShadowResponse);
    ssAmount = max(0.0h, color * response * transmission * warmSun
        + ambient * skyFill * hemiFill);
#ifndef SHADERGRAPH_PREVIEW
    ssAmount *= illusionFade;
#endif
}

#endif
