#ifndef IG_COMMON_INCLUDED
#define IG_COMMON_INCLUDED

// Shared atmosphere. The surface shader, the edge pass and the sky all call these two functions,
// so geometry, outlines and sky fade into exactly the same color.
// The values are pushed as global shader properties by IGAtmosphere.cs.
float4 _IG_FogHorizon;
float4 _IG_FogZenith;
float4 _IG_FogNadir;
float4 _IG_FogParams; // x: density (1/m), y: start distance (m), z: end distance (m, fully fogged), w: gradient sharpness

// Sky / fog color for a view direction: horizon -> zenith above, horizon -> nadir below.
half3 IG_FogColor(float3 dirWS)
{
    float k = max(_IG_FogParams.w, 0.01);
    half3 up = lerp(_IG_FogHorizon.rgb, _IG_FogZenith.rgb, pow(saturate(dirWS.y), k));
    half3 down = lerp(_IG_FogHorizon.rgb, _IG_FogNadir.rgb, pow(saturate(-dirWS.y), k));
    return dirWS.y >= 0.0 ? up : down;
}

// 0 = clear, 1 = fully fogged.
float IG_FogFactor(float distance)
{
    float d = max(distance - _IG_FogParams.y, 0.0) * _IG_FogParams.x;
    float fog = 1.0 - exp(-d * d); // squared exponential: crisp near the camera, thick in the distance
    // Hard guarantee: at the end distance everything is fog. The world repeater relies on this
    // to skip every copy of the level that lies farther away.
    float hardEnd = smoothstep(_IG_FogParams.z * 0.75, _IG_FogParams.z, distance);
    return saturate(max(fog, hardEnd));
}

#endif
