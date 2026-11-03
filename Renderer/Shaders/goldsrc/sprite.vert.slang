#version 460

#include "common_vs.slang"
#include "../common/sprite_billboard.slang"

// The corner of the sprite along its right and up axes in world units
in vec2 vNORMAL;
in vec4 vCOLOR;

out vec3 vFragPosition;
out vec2 vTexCoordOut;
out vec4 vVertexColorOut;

// A glow is as big on the screen whatever the distance, and fades out when it is far
uniform int F_GLOW;

// 0 turns around the up axis to face the view direction, 1 does so toward the camera position, 2 is parallel to the view plane
uniform int g_nOrientation;

const float GlowSizePerDistance = 0.005;
const float GlowFadeDistanceSquared = 19000.0;
const float GlowMinimumFade = 0.05;

void main()
{
    vec3 center = vec4(vPOSITION, 1.0) * CalculateSkinTransform();
    vec2 corner = vNORMAL;
    vec4 vertexColor = vCOLOR;

    if (F_GLOW != 0)
    {
        vec3 toCenter = center - g_vCameraPositionWs;
        float distanceSquared = max(dot(toCenter, toCenter), 1.0);
        float fade = clamp(GlowFadeDistanceSquared / distanceSquared, GlowMinimumFade, 1.0);

        corner *= sqrt(distanceSquared) * GlowSizePerDistance;
        vertexColor.rgb *= fade;
    }

    vec3 viewForward = g_vCameraDirWs;
    vec3 right = cross(viewForward, g_vCameraUpDirWs);
    vec3 up = g_vCameraUpDirWs;

    if (g_nOrientation == 0)
    {
        up = vec3(0.0, 0.0, 1.0);
        right = GetUprightBillboardRight(viewForward);
    }
    else if (g_nOrientation == 1)
    {
        up = vec3(0.0, 0.0, 1.0);
        right = GetUprightBillboardRight(center - g_vCameraPositionWs);
    }

    vec3 position = center + (corner.x * right) + (corner.y * up);

    vFragPosition = position;
    gl_Position = g_matWorldToProjection * vec4(position, 1.0);

    vTexCoordOut = vTEXCOORD;
    vVertexColorOut = vertexColor;
}
