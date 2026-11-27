#ifndef AARIS_GRASS_INTERACTION_INCLUDED
#define AARIS_GRASS_INTERACTION_INCLUDED

// Both contacts run in the vertex stage and use CPU-normalized footprints.
float4 _AarisGrassInteractor;  // On-foot world xyz, inverse radius squared.
float4 _AarisGrassVehicle;     // Ground centre xyz, inverse broad radius squared.
float4 _AarisGrassVehicleShape; // Forward xz, inverse squared width and length extents.

#ifndef SHADERGRAPH_PREVIEW
float AarisGrassHeightFade(float heightDelta)
{
    float vertical = saturate((1.8 - heightDelta) * 1.25);
    return vertical * vertical * (3.0 - 2.0 * vertical);
}

void AarisFootGrassContact(float3 pivot, float3 BasePositionOS, float GroundHeight,
    float4 Profile, inout float3 Out, inout float3 BendSlopeOS)
{
    // Normalize the interaction radius once on CPU. Every detail vertex then
    // uses one multiply instead of rebuilding r^2 and dividing by it.
    float inverseRadiusSq = _AarisGrassInteractor.w;
    if (inverseRadiusSq > 0.0)
    {
        // All vertices of this instance share the same ground pivot height.
        // Reject different floors/slopes before computing per-leaf world XZ.
        float heightDelta = abs(pivot.y - _AarisGrassInteractor.y);
        UNITY_BRANCH if (heightDelta >= 1.8)
            return;
        float2 delta = pivot.xz - _AarisGrassInteractor.xz;
        // The large red shrub is wider than the player's contact radius.
        // Local leaf positions make its outer canopy rustle, not only its pivot.
        UNITY_BRANCH if (Profile.x > 0.001 && GroundHeight > 1.5)
        {
            // Red_Bush terrain mesh and its standalone LOD0/1/2 meshes were
            // measured in Unity. These deliberately padded object-space bounds
            // enclose every rest vertex of all four meshes, even after rotation,
            // tilt and nonuniform scale (via the object-to-world matrix rows).
            // Only this authored 2.13m profile may use the early-out; future
            // unrelated tall plants retain the exact original per-leaf path.
            UNITY_BRANCH if (abs(GroundHeight - 2.13) < 0.002)
            {
                const float3 maxAbsRedBushOS = float3(2.30, 2.20, 2.50);
                float2 spanXZ = float2(
                    dot(abs(UNITY_MATRIX_M[0].xyz), maxAbsRedBushOS),
                    dot(abs(UNITY_MATRIX_M[2].xyz), maxAbsRedBushOS));
                // Squared distance from the interactor to the closest point of
                // the conservative world-XZ AABB. This is tighter than expanding
                // the box by the radius and avoids a per-vertex reciprocal sqrt.
                float2 outside = max(abs(delta) - spanXZ, 0.0);
                // A red leaf can only be reached if that closest AABB point is
                // inside the interaction circle. The test is uniform for each
                // instanced bush, so distant 8,887-vertex crowns skip the later
                // per-leaf world transform without changing reachable contacts.
                UNITY_BRANCH if (dot(outside, outside) * inverseRadiusSq >= 1.0)
                    return;
            }
            delta = TransformObjectToWorld(BasePositionOS).xz - _AarisGrassInteractor.xz;
        }
        float d2 = dot(delta, delta);
        float radial = d2 * inverseRadiusSq;
        if (radial < 1.0)
        {
            // Fade the response on slopes and during a jump, including at
            // the boundary that rejects foliage on a different floor.
            float vertical = AarisGrassHeightFade(heightDelta);
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
}

void AarisVehicleGrassContact(float3 pivot, float3 rest, float groundHeight, float4 profile,
    inout float3 position, inout float3 bendSlope)
{
    float heightDelta = abs(pivot.y - _AarisGrassVehicle.y);
    UNITY_BRANCH if (heightDelta >= 1.8)
        return;

    float2 delta = pivot.xz - _AarisGrassVehicle.xz;
    bool shrub = profile.x > 0.001;
    // Shrub cards may reach beyond their pivot. Only transform nearby cards.
    float reachSq = shrub ? 4.0 : 1.0;
    UNITY_BRANCH if (dot(delta, delta) * _AarisGrassVehicle.w >= reachSq)
        return;
    UNITY_BRANCH if (shrub)
        delta = TransformObjectToWorld(rest).xz - _AarisGrassVehicle.xz;

    float2 forward = _AarisGrassVehicleShape.xy;
    float2 right = float2(forward.y, -forward.x);
    float2 local = float2(dot(delta, right), dot(delta, forward));
    float2 normalizedSq = local * local * _AarisGrassVehicleShape.zw;
    float outside = max(normalizedSq.x, normalizedSq.y);
    UNITY_BRANCH if (outside >= 1.0)
        return;

    float edge = saturate((outside - 0.33) * (1.0 / 0.67));
    float pressure = (1.0 - edge * edge * (3.0 - 2.0 * edge)) * AarisGrassHeightFade(heightDelta);
    float inverseHeight = rcp(max(groundHeight, 0.05));
    float height = saturate(rest.y * inverseHeight);
    float2 direction = forward * 0.32 + right * clamp(local.x, -0.8, 0.8) * 0.32;
    float3 lean = TransformWorldToObjectDir(float3(direction.x, 0.0, direction.y) * pressure, false);
    position += lean * (height * height);
    // Compress height linearly so full pressure pins the base and keeps the
    // canopy above the soil. The smooth footprint restores its rest height.
    position.y = lerp(position.y, rest.y * 0.28, pressure);
    UNITY_BRANCH if (shrub && rest.y <= groundHeight)
    {
        bendSlope += lean * (2.0 * height * inverseHeight);
        bendSlope.y -= 0.72 * pressure;
        bendSlope = clamp(bendSlope, float3(-0.65, -0.78, -0.65), 0.65);
    }
}
#endif

void AarisGrassInteraction_float(float3 WindPositionOS, float3 BasePositionOS,
    float GroundHeight, float4 Profile, out float3 Out, out float3 BendSlopeOS)
{
    Out = WindPositionOS;
    BendSlopeOS = 0.0;
#ifndef SHADERGRAPH_PREVIEW
    // Every vertex in a terrain instance shares the matrix translation.
    float3 pivot = float3(UNITY_MATRIX_M[0].w, UNITY_MATRIX_M[1].w, UNITY_MATRIX_M[2].w);
    AarisFootGrassContact(pivot, BasePositionOS, GroundHeight, Profile, Out, BendSlopeOS);
    UNITY_BRANCH if (_AarisGrassVehicle.w > 0.0)
        AarisVehicleGrassContact(pivot, BasePositionOS, GroundHeight, Profile, Out, BendSlopeOS);
#endif
}
#endif
