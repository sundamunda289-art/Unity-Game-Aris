#ifndef AARIS_GRASS_INTERACTION_INCLUDED
#define AARIS_GRASS_INTERACTION_INCLUDED

// One global interactor for the on-foot player or active vehicle.
// Shader Graph calls this in the vertex stage only: no physics, colliders,
// per-blade scripts, extra texture samples or rendering passes.
float4 _AarisGrassInteractor;  // World xyz, inverse interaction radius squared.

void AarisGrassInteraction_float(float3 WindPositionOS, float3 BasePositionOS,
    float GroundHeight, float4 Profile, out float3 Out, out float3 BendSlopeOS)
{
    Out = WindPositionOS;
    BendSlopeOS = 0.0;
#ifndef SHADERGRAPH_PREVIEW
    // Normalize the interaction radius once on CPU. Every detail vertex then
    // uses one multiply instead of rebuilding r^2 and dividing by it.
    float inverseRadiusSq = _AarisGrassInteractor.w;
    if (inverseRadiusSq > 0.0)
    {
        // The Terrain detail pivot is exactly the translation column of the
        // current instanced object matrix. Reading it directly avoids a full
        // object-to-world matrix multiply for every grass/bush vertex.
        float3 pivot = float3(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[1].w, UNITY_MATRIX_M[2].w);
        float2 delta = pivot.xz - _AarisGrassInteractor.xz;
        float d2 = dot(delta, delta);
        float radial = d2 * inverseRadiusSq;
        float heightDelta = abs(pivot.y - _AarisGrassInteractor.y);

        // The large red shrub is wider than the player's contact radius, so
        // nearby outer leaves use their real world position. Do not pay that
        // matrix transform on every vertex of every distant shrub: normalized
        // radial is uniform for the whole instance, and 9 == (3 radii)^2 is a
        // deliberately conservative coarse zone around the active interactor.
        UNITY_BRANCH if (Profile.x > 0.001 && GroundHeight > 1.5 &&
                         heightDelta < 1.8 && radial < 9.0)
        {
            delta = TransformObjectToWorld(BasePositionOS).xz - _AarisGrassInteractor.xz;
            d2 = dot(delta, delta);
            radial = d2 * inverseRadiusSq;
        }
        if (radial < 1.0 && heightDelta < 1.8)
        {
            // Fade the response on slopes and during a jump, including at
            // the boundary that rejects foliage on a different floor.
            float vertical = saturate((1.8 - heightDelta) * 1.25);
            // Cubic vertical easing has zero slope at both ends, so crossing
            // the full-contact height while running downhill or landing cannot
            // abruptly change blade velocity. Keep the radial falloff squared.
            vertical = vertical * vertical * (3.0 - 2.0 * vertical);
            float falloff = 1.0 - radial;
            float height = saturate(BasePositionOS.y);
            float bendScale = 1.0;
            float inverseHeight = 1.0;
            UNITY_BRANCH if (Profile.x > 0.001)
            {
                inverseHeight = rcp(max(GroundHeight, 0.05));
                height = saturate(BasePositionOS.y * inverseHeight);
                bendScale = min(1.0, max(GroundHeight, 0.25));
            }
            float bend = falloff * falloff * vertical * 0.45 * bendScale;
            // A regularized length has no sharp derivative at the old
            // 0.10m cutoff. Passing directly through a pivot remains finite.
            float2 dir = delta * rsqrt(d2 + 0.0144);
            float3 offsetOS = TransformWorldToObjectDir(
                float3(dir.x * bend, -0.15 * bend, dir.y * bend), false);
            // Keep roots pinned. The local Y derivative lets lighting bend with
            // the card, while depth-only passes only need the position.
            Out += offsetOS * (height * height);
            if (Profile.x > 0.001 && BasePositionOS.y <= GroundHeight)
                BendSlopeOS = clamp(offsetOS * (2.0 * height * inverseHeight), -0.65, 0.65);
        }
    }
#endif
}
#endif
