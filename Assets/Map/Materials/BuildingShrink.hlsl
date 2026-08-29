// BuildingShrink.hlsl
// URP Lit Shader Graph custom function. Squashes buildings inside a cone from the player
// toward the camera. Works with "Merge Objects" because the per-building anchor travels in a
// UV channel (one anchor value repeated on every vertex of a building), so even in a merged
// tile mesh each building's vertices share one anchor -> uniform shrink, no tilt.
//
// The anchor is the building's OBJECT-SPACE centroid (written by BuildingAnchorMeshModifier
// into UV channel 1 / TEXCOORD1). We transform it to world space with the SAME object-to-world
// path as a vertex, so anchor-world and vertex-world agree regardless of the object matrix.
//
// Cone globals are pushed each frame from BuildingShrinkManager.cs via Shader.SetGlobalX.

#ifndef BUILDING_SHRINK_INCLUDED
#define BUILDING_SHRINK_INCLUDED

float4 _PlayerXZ;       // (player.x, player.z, 0, 0)
float4 _ConeDirXZ;      // normalized player->camera direction in XZ (x, z, 0, 0)
float4 _ConeSideXZ;     // perpendicular of ConeDir in XZ (x, z, 0, 0)
float  _MaxDistance;
float  _TanHalfAngle;
float  _MinHalfWidth;
float  _EdgeFalloff;       // softness of the cone's SIDE (lateral) edge.
float  _ForwardFade;       // softness of the FORWARD entry + MaxDistance edges. Small = snappy
                           // (buildings shrink fast once inside the cone). 0 = instant forward.
float  _MinHeightFactor;
float  _FadeStart;      // shrink value (0..1) at which alpha STARTS fading. Below this = opaque.
float  _MinAlpha;       // alpha at full shrink (shrink = 1). 0 = fully invisible.
float  _TintStrength;   // per-building color variation amount (0 = none). Small (~0.05-0.2).
float4 _TintColorA;     // POSITIVE end of the tint axis (e.g. warm/yellow).
float4 _TintColorB;     // NEGATIVE end of the tint axis (e.g. cold/blue).

float BShrink_Smooth01(float t)
{
    t = saturate(t);
    return t * t * (3.0 - 2.0 * t);
}

// 0..1 shrink for a building whose anchor is at worldXZ. This is a single SWEPT-SPHERE field:
// a ball of radius MinHalfWidth around the player (shrinks buildings in ALL directions, incl.
// behind), smoothly widening into a cone toward the camera up to MaxDistance. One continuous
// shape — like a spherecast from the player toward the camera — so there's nothing to blend.
//
//   - Behind the player (forward <= 0): distance to the PLAYER point -> the ball cap.
//   - Ahead (forward > 0): perpendicular distance to the cone AXIS, with the allowed radius
//     widening as max(MinHalfWidth, forward * tanHalf).
//   EdgeFalloff softens the outer surface; ForwardFade softens the far (MaxDistance) cap.
float BShrink_ConeShrink(float2 worldXZ)
{
    float2 rel = worldXZ - _PlayerXZ.xy;
    float forward = dot(rel, _ConeDirXZ.xy);
    float lateral = abs(dot(rel, _ConeSideXZ.xy));

    // "How far outside the shape" is measured as (distance - allowedRadius) for the relevant
    // region. <= 0 means inside.
    float outside;
    if (forward <= 0.0)
    {
        // Behind / beside the apex: use straight-line distance to the player (the ball cap).
        float dist = length(rel);
        outside = dist - _MinHalfWidth;
    }
    else
    {
        // Ahead: perpendicular distance to the axis vs. the widening cone radius.
        float allowed = max(_MinHalfWidth, forward * _TanHalfAngle);
        outside = lateral - allowed;
    }

    // Inside the surface -> full shrink; fade out across EdgeFalloff beyond it.
    float surface = BShrink_Smooth01((_EdgeFalloff - outside) / max(_EdgeFalloff, 1e-4));

    // Far cap: ease out approaching MaxDistance (only matters in the forward direction).
    float fwdFar;
    if (_ForwardFade > 1e-4)
        fwdFar = BShrink_Smooth01((_MaxDistance - forward) / _ForwardFade);
    else
        fwdFar = forward > _MaxDistance ? 0.0 : 1.0;

    return saturate(surface * fwdFar);
}

// ---------------------------------------------------------------------------
// Custom Function entry point (Vertex stage).
//
// Inputs from the graph:
//   ObjectPosition : vertex position in OBJECT space (Position node, Object space).
//   AnchorUV       : the per-building anchor read from UV channel 1 (UV node, channel UV1).
//                    .x = anchor object-space X, .y = anchor object-space Z.
//
// Output:
//   OutPosition : modified OBJECT-space position -> feed into Vertex Position.
//
// We build the anchor as an object-space point (x, 0, z), transform it to world with
// TransformObjectToWorld (same as a vertex), take its XZ, and run the cone test. Then we scale
// the vertex's object-space Y toward the base (object y = 0).
// ---------------------------------------------------------------------------
void BuildingShrink_float(float3 ObjectPosition, float2 AnchorUV, out float3 OutPosition, out float OutShrink)
{
    // Anchor as an object-space position, then to world (matches how vertices are placed).
    float3 anchorObj = float3(AnchorUV.x, 0.0, AnchorUV.y);
    float3 anchorWorld = TransformObjectToWorld(anchorObj);
    float2 anchorXZ = anchorWorld.xz;

    float shrink = BShrink_ConeShrink(anchorXZ);          // same for all vertices of a building
    float yFactor = lerp(1.0, _MinHeightFactor, shrink);  // 1 = full, MinHeightFactor = squashed

    OutPosition = float3(ObjectPosition.x, ObjectPosition.y * yFactor, ObjectPosition.z);
    OutShrink = shrink;                                   // carry to fragment for alpha fade
}

// ---------------------------------------------------------------------------
// Fragment-stage helper: per-building color variation along ONE axis (no mixing), done by
// INTERPOLATING the actual base color toward a chosen end color. Correct for ANY base color
// (warm, cool, not-white) — unlike Add (sums channels and shifts hue, e.g. orange+blue->grey)
// or Multiply (can only darken; can't make a warm base warmer).
//
// A single random value per building places it on a line:  ColorB  <—  base  —>  ColorA.
//   random > 0 : blend base toward ColorA (e.g. warm)
//   random < 0 : blend base toward ColorB (e.g. cold)
//   random ~ 0 : stays the base color
// EITHER warm OR cold per building, never a mix.
//
// Wire: BaseColor in (your material base color), TintSeed from a UV node on channel UV2 (.x).
// Output OutColor goes straight into Base Color (REPLACES it — do NOT Add).
// ---------------------------------------------------------------------------
void BuildingShrinkTint_float(float3 BaseColor, float2 TintSeed, out float3 OutColor)
{
    float r = TintSeed.x; // single axis, [-1, 1]

    float3 endCol = (r >= 0.0) ? _TintColorA.rgb : _TintColorB.rgb;

    // Blend the real base toward the end color by |r| * strength.
    float amount = saturate(abs(r) * _TintStrength);
    OutColor = lerp(BaseColor, endCol, amount);
}

// ---------------------------------------------------------------------------
// Fragment-stage helper: DITHERED ALPHA CLIP. Maps the interpolated shrink (0..1) to an alpha,
// then converts that alpha into a screen-space dither threshold so the material can stay in the
// OPAQUE queue (writes depth, sorts correctly) while still *looking* like it fades. This avoids
// the transparent-sorting mess you get blending thousands of overlapping merged buildings.
//
// Output OutAlpha is meant to feed the master stack's **Alpha** with **Alpha Clipping ON** and
// the **Clip Threshold = 0.5** (or wire OutClipThreshold for a fixed 0.5 compare). Pixels with
// OutAlpha below the dither pattern get clipped. Buildings stay opaque until _FadeStart, then
// dissolve toward _MinAlpha at full shrink.
//
// ScreenPos: wire a Screen Position node in **Raw** mode here (clip-space, real .w).
// ---------------------------------------------------------------------------

// Cheap hash -> [0,1), used for a blue-noise-ish dither. Unlike an ordered Bayer matrix (which
// shows a regular cross-hatch the eye picks out), this randomized threshold reads as a fine
// even grain with no visible repeating structure.
float BShrink_Hash(float2 p)
{
    // Interleaved gradient noise (Jimenez) — cheap, screen-space, looks like blue noise.
    return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

void BuildingShrinkAlpha_float(float Shrink, float4 ScreenPos, out float OutAlpha)
{
    // Map shrink -> coverage (1 = fully present, _MinAlpha = faded) with the FadeStart ramp.
    float t = saturate((Shrink - _FadeStart) / max(1.0 - _FadeStart, 1e-4));
    t = t * t * (3.0 - 2.0 * t); // smoothstep
    float coverage = lerp(1.0, _MinAlpha, t);

    // Screen-space pixel coordinate.
    float2 px = ScreenPos.xy / max(ScreenPos.w, 1e-4) * _ScreenParams.xy;

    // Blue-noise-style threshold (no repeating grid pattern).
    float threshold = BShrink_Hash(px);

    OutAlpha = coverage > threshold ? 1.0 : 0.0;
}

#endif // BUILDING_SHRINK_INCLUDED
