#version 460

#include "common_vs.slang"

// Lightmap coordinates in luxels of the atlas, and in the color the light style of each lightmap copy of the face
in vec2 vLightmapUV;
in vec4 vCOLOR;

out vec3 vFragPosition;
out vec2 vTexCoordOut;
out vec2 vLightmapUVOut;
flat out vec4 vStylesOut;

void main()
{
    mat3x4 objectToWorld = CalculateSkinTransform();

    vFragPosition = vec4(vPOSITION, 1.0) * objectToWorld;
    gl_Position = g_matWorldToProjection * vec4(vFragPosition, 1.0);

    vTexCoordOut = vTEXCOORD;
    vLightmapUVOut = vLightmapUV;
    vStylesOut = vCOLOR;
}
