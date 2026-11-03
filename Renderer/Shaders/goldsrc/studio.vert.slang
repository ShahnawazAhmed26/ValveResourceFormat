#version 460

#include "studio_common.slang"
#include "../common/view_constants.slang"
#include "../common/animation.slang"

in vec3 vPOSITION;
in vec3 vNORMAL;
in vec2 vTEXCOORD;

out vec3 vFragPosition;
out vec2 vTexCoordOut;
out vec3 vLightOut;

// The vertices of a model are in the space of their bone. The normal can be on another bone than the position,
// which the second index of the vertex names.
mat3x4 GetNormalSkinMatrix()
{
#if (D_SKINNING > 0)
    if (IS_ANIMATED)
    {
        return GetMatrix(vBLENDINDICES.y, GetObjectBoneOffset());
    }
#endif

    return mat3x4(1.0);
}

void main()
{
    mat3x4 positionToWorld = CalculateSkinTransform();
    mat3x4 normalToWorld = ConcatTransforms(GetNormalSkinMatrix(), CalculateObjectToWorldMatrix());

    vFragPosition = vec4(vPOSITION, 1.0) * positionToWorld;
    vec3 normal = TransformDirectionToWorld(normalToWorld, vNORMAL);
    gl_Position = g_matWorldToProjection * vec4(vFragPosition, 1.0);

    vTexCoordOut = vTEXCOORD;

    if (F_CHROME != 0)
    {
        // The reflection is looked up by where the normal points as seen from the camera, with an up and a right
        // that depend on the direction from the camera to the bone of the normal
        vec3 boneOrigin = vec4(0.0, 0.0, 0.0, 1.0) * normalToWorld;
        vec3 toBone = normalize(boneOrigin - g_vCameraPositionWs);
        vec3 viewRight = cross(g_vCameraDirWs, g_vCameraUpDirWs);
        vec3 chromeUp = normalize(cross(toBone, viewRight));
        vec3 chromeRight = normalize(cross(chromeUp, toBone));
        vTexCoordOut = vec2(dot(normal, chromeRight) + 1.0, dot(normal, chromeUp) + 1.0) * ChromeTexelScale;
    }

    // Additive models take their brightness from the opacity and are not lit
    vLightOut = g_nRenderMode == RenderModeAdditive ? vec3(g_vRenderColor.a) : GetStudioLight(normal);
}
