#version 460

#ifndef VRF_SOURCE1_EYES_COMMON_VS_INCLUDE
#define VRF_SOURCE1_EYES_COMMON_VS_INCLUDE

#include "common_vs.slang"

// The eyeball in the space of the vertices, which its bone carries into the world
uniform int g_nEyeBoneIndex;
uniform vec3 g_vEyeBindOrigin;
uniform vec3 g_vEyeBindUp = vec3(0.0, 0.0, 1.0);
uniform vec3 g_vEyeBindForward = vec3(1.0, 0.0, 0.0);
// How far the eye looks to the side of its target, and the size of the iris relative to the eyeball
uniform float g_flEyeZOffset;
uniform float g_flEyeIrisScale = 1.0;

flat out vec3 vEyeOrigin;
flat out vec4 vIrisProjectionU;
flat out vec4 vIrisProjectionV;

mat3x4 GetEyeToWorldMatrix()
{
#if (D_SKINNING == 0)
    return CalculateObjectToWorldMatrix();
#else
    return ConcatTransforms(GetMatrix(uint(g_nEyeBoneIndex), GetObjectBoneOffset()), CalculateObjectToWorldMatrix());
#endif
}

// Places the eye in the world and aims it forward. The iris texture is projected on the plane the eye faces,
// as the dot product of the projection vectors with the world position and 1.
void CalculateSource1EyeProjection()
{
    mat3x4 eyeToWorld = GetEyeToWorldMatrix();

    vec3 origin = vec4(g_vEyeBindOrigin, 1.0) * eyeToWorld;
    vec3 up = normalize(TransformDirectionToWorld(eyeToWorld, g_vEyeBindUp));
    vec3 forward = normalize(TransformDirectionToWorld(eyeToWorld, g_vEyeBindForward));

    vec3 right = normalize(cross(forward, up));
    forward = normalize(forward + right * (g_flEyeZOffset * 2.0));
    right = normalize(cross(forward, up));
    up = normalize(cross(right, forward));

    vec3 projectionU = right * -g_flEyeIrisScale;
    vec3 projectionV = up * -g_flEyeIrisScale;

    vEyeOrigin = origin;
    vIrisProjectionU = vec4(projectionU, 0.5 - dot(origin, projectionU));
    vIrisProjectionV = vec4(projectionV, 0.5 - dot(origin, projectionV));
}

#endif
