#version 460

#include "unlitgeneric_features.slang"
#include "common_ps.slang"
#include "common_envmap_ps.slang"
#include "common_rendermodes_surface_ps.slang"
#include "../common/lighting_constants.slang"
#include "../common/depth.slang"

in vec3 vFragPosition;
in vec2 vTexCoordOut;

uniform sampler2D g_tNormal; // Sampler(UserConfig)
uniform sampler2D g_tSceneColor; // reserved

uniform int F_BUMPMAP;
uniform int F_REFRACT;
uniform int F_NO_FRESNEL;
uniform vec3 g_vWaterFogColor = vec3(0.1, 0.2, 0.25);
uniform vec3 g_vRefractTint = vec3(1.0);
uniform vec4 g_vReflectTint = vec4(1.0);
uniform float g_flRefractAmount = 0.0;
uniform float g_flWaterFogStart = 0.0;
uniform float g_flWaterFogEnd = 0.0;
uniform float g_flCheapWaterStartDistance = 500.0;
uniform float g_flCheapWaterEndDistance = 1000.0;

// How far the scene at a screen position is behind the water surface along the view ray, negative when it is in front
float GetDistanceBehindSurface(vec2 screenCoord, vec3 cameraRay)
{
    float sceneDepth = textureLod(g_tSceneDepth, screenCoord, 0.0).x;
    vec3 scenePosition = GetWorldPositionFromDepthValue(sceneDepth, cameraRay);

    return distance(scenePosition, g_vCameraPositionWs) - length(cameraRay);
}

// How much of the water fog is between the surface and what is seen through it, from 0 to 1
float GetWaterFogFactor(float distanceInWater)
{
    return saturate((distanceInWater - g_flWaterFogStart) / max(g_flWaterFogEnd - g_flWaterFogStart, 1.0));
}

// The render modes of the surface, which has no color texture so the water fog color stands for the albedo
void ApplyWaterRenderModes(vec3 geometricNormal, vec3 normal, vec3 tangentNormal, vec3 eyeDirection)
{
    if (g_iRenderMode == 0)
    {
        return;
    }

    vec3 tangent = GetSource1TangentFrame(geometricNormal, vec4(0.0), vFragPosition, vTexCoordOut)[0];

    ApplySource1RenderModes(outputColor, g_vWaterFogColor, vec4(1.0), normal, eyeDirection, g_tNormal, vTexCoordOut);
    ApplySource1NormalRenderModes(outputColor, geometricNormal, tangent, tangentNormal, normal);
    ApplySource1CubemapRenderMode(outputColor, normal, vFragPosition);
}

void main()
{
    vec3 geometricNormal = normalize(cross(dFdx(vFragPosition), dFdy(vFragPosition)));
    vec3 cameraRay = vFragPosition - g_vCameraPositionWs;
    vec3 eyeDirection = normalize(-cameraRay);

    // Seen from below, the surface faces the camera
    geometricNormal *= sign(dot(geometricNormal, eyeDirection));

    vec3 tangentNormal = vec3(0.0, 0.0, 1.0);
    float normalAlpha = 1.0;
    vec3 normal = geometricNormal;
    vec3 unnormalizedNormal = geometricNormal;

    if (F_BUMPMAP != 0)
    {
        vec4 normalTexel = texture(g_tNormal, vTexCoordOut);
        tangentNormal = normalTexel.xyz * 2.0 - 1.0;
        normalAlpha = normalTexel.a;
        mat3 tangentFrame = GetSource1TangentFrame(geometricNormal, vec4(0.0), vFragPosition, vTexCoordOut);
        unnormalizedNormal = tangentFrame * tangentNormal;
        normal = normalize(unnormalizedNormal);
    }

    if (F_REFRACT == 0)
    {
        // The cheap water takes the bump map as it is, without normalizing it
        float facing = dot(unnormalizedNormal, eyeDirection);
        vec3 reflectionDirection = 2.0 * facing * unnormalizedNormal - dot(unnormalizedNormal, unnormalizedNormal) * eyeDirection;
        vec3 cheapReflection;

        if (F_ENVMAP != 0)
        {
            cheapReflection = SampleSource1Cube(reflectionDirection);
        }
        else
        {
            cheapReflection = max(GetSkyAmbientLighting(reflectionDirection), vec3(0.0));
        }

        // Without a fresnel term the reflection takes the blend factor of the material, kept in the alpha of the tint
        float cheapFresnel = g_vReflectTint.w;

        if (F_NO_FRESNEL == 0)
        {
            cheapFresnel = pow(1.0 - saturate(facing), 5.0);
        }

        vec3 cheapColor = cheapReflection * g_vReflectTint.rgb;
        float cheapAlpha = 1.0;

        if (F_TRANSLUCENT == 0)
        {
            // The opaque variant mixes the reflection with the water color
            cheapColor = mix(g_vWaterFogColor, cheapColor, cheapFresnel);
        }
        else
        {
            // The blended variant lays the reflection over the scene, more of it with distance
            float distanceReflection = saturate((length(cameraRay) - g_flCheapWaterStartDistance) / max(g_flCheapWaterEndDistance - g_flCheapWaterStartDistance, 1.0));
            cheapAlpha = saturate(cheapFresnel + distanceReflection);
        }

        ApplyFog(cheapColor, vFragPosition);
        outputColor = vec4(cheapColor, cheapAlpha);
        ApplyWaterRenderModes(geometricNormal, normal, tangentNormal, eyeDirection);
        return;
    }

    vec3 reflection;

    if (F_ENVMAP != 0)
    {
        reflection = SampleSource1Cube(reflect(-eyeDirection, normal));
    }
    else
    {
        reflection = max(GetSkyAmbientLighting(reflect(-eyeDirection, normal)), vec3(0.0));
    }

    reflection *= g_vReflectTint.rgb;

    float facing = saturate(dot(normal, eyeDirection));
    float fresnel = pow(1.0 - facing, 5.0);
    float distanceReflection = saturate((length(cameraRay) - g_flCheapWaterStartDistance) / max(g_flCheapWaterEndDistance - g_flCheapWaterStartDistance, 1.0));
    float reflectionAmount = saturate(fresnel + distanceReflection);

    vec2 screenCoord = gl_FragCoord.xy / vec2(textureSize(g_tSceneColor, 0));
    float fogFactor = GetWaterFogFactor(GetDistanceBehindSurface(screenCoord, cameraRay));

    // The refraction is offset by the ripples, less where the water is shallow. The screen coordinate runs upward.
    vec2 refractOffset = vec2(tangentNormal.x, -tangentNormal.y) * normalAlpha * g_flRefractAmount * fogFactor;
    vec2 refractCoord = saturate(screenCoord + refractOffset);

    // An offset that lands on something above the water would show it through the surface
    if (GetDistanceBehindSurface(refractCoord, cameraRay) < 0.0)
    {
        refractCoord = screenCoord;
    }

    vec3 refraction = textureLod(g_tSceneColor, refractCoord, 0.0).rgb * g_vRefractTint;
    refraction = mix(refraction, g_vWaterFogColor, saturate(fogFactor - 0.05));

    // Shallow water fades into the shore without a reflection
    reflectionAmount *= saturate((fogFactor - 0.05) * 20.0);

    vec3 color = mix(refraction, reflection, reflectionAmount);
    ApplyFog(color, vFragPosition);
    outputColor = vec4(color, 1.0);
    ApplyWaterRenderModes(geometricNormal, normal, tangentNormal, eyeDirection);
}
