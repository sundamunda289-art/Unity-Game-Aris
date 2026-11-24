#ifndef AARIS_GRASS_INTERACTION_INCLUDED
#define AARIS_GRASS_INTERACTION_INCLUDED
// Uniform shared by all existing grass vertices; w is radius, zero disables.
float4 _AarisGrassInteract;
void AarisGrassInteract_float(float3 WindOS, float3 RestOS, float3 RestWS,
    out float3 BentOS)
{
    float radius = _AarisGrassInteract.w;
    float2 away = RestWS.xz - _AarisGrassInteract.xz;
    float distanceSquared = dot(away, away);
    float r = max(radius, 0.1);
    float falloff = saturate(1.0 - distanceSquared / (r * r));
    falloff = falloff * falloff * step(0.1, radius);
    float tip = saturate(RestOS.y * 1.4);
    float2 direction = away * rsqrt(max(distanceSquared, 0.09));
    float2 worldOffset = direction * (0.24 * tip * falloff);
    BentOS = WindOS + TransformWorldToObjectDir(float3(worldOffset.x, 0.0, worldOffset.y));
}
void AarisGrassInteract_half(half3 WindOS, half3 RestOS, half3 RestWS,
    out half3 BentOS)
{
    float3 offset;
    AarisGrassInteract_float(WindOS, RestOS, RestWS, offset);
    BentOS = (half3)offset;
}
#endif
