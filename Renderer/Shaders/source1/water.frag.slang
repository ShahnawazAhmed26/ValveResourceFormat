#version 460

#include "unlitgeneric_features.slang"
#include "common_ps.slang"
#include "common_envmap_ps.slang"
#include "../common/lighting_constants.slang"
#include "../common/depth.slang"

in vec3 vFragPosition;
in vec2 vTexCoordOut;

uniform sampler2D g_tNormal; // Sampler(UserConfig)
uniform sampler2D g_tSceneColor; // reserved

uniform int F_BUMPMAP;
uniform int F_REFRACT;
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

        float cheapFresnel = pow(1.0 - saturate(facing), 5.0);
        float reflectionAmount = 0.8 * cheapFresnel + 0.2;

        vec3 cheapColor = mix(g_vWaterFogColor, cheapReflection * g_vReflectTint.rgb, reflectionAmount);
        ApplyFog(cheapColor, vFragPosition);
        outputColor = vec4(cheapColor, g_vReflectTint.w);
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
}
