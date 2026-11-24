#ifndef AARIS_GRASS_INTERACTION_INCLUDED
#define AARIS_GRASS_INTERACTION_INCLUDED

// One global interactor for the on-foot player or active vehicle.
// Shader Graph calls this in the vertex stage only: no physics, colliders,
// per-blade scripts, extra texture samples or rendering passes.
float4 _AarisGrassInteractor;  // World xyz, interaction radius in metres.

void AarisGrassInteraction_float(float3 WindPositionOS, float3 BasePositionOS, out float3 Out)
{
    Out = WindPositionOS;
#ifndef SHADERGRAPH_PREVIEW
    float radius = _AarisGrassInteractor.w;
    if (radius > 0.0)
    {
        // The Terrain detail mesh pivot is positioned by its instanced object matrix.
        float3 pivot = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
        float2 delta = pivot.xz - _AarisGrassInteractor.xz;
        float d2 = dot(delta, delta);
        float r2 = radius * radius;
        if (d2 < r2 && abs(pivot.y - _AarisGrassInteractor.y) < 1.8)
        {
            float falloff = saturate(1.0 - d2 / r2);
            float height = saturate(BasePositionOS.y);
            float bend = falloff * falloff * height * 0.45;
            float2 dir = delta * rsqrt(max(d2, 0.01));
            Out += TransformWorldToObjectDir(
                float3(dir.x * bend, -0.15 * bend, dir.y * bend), false);
        }
    }
#endif
}
#endif
