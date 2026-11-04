#version 460

#include "unlitgeneric_features.slang"
#include "common_ps.slang"
#include "common_envmap_ps.slang"
#include "common_model_lighting_ps.slang"

in vec3 vFragPosition;
in vec3 vNormalOut;
in vec4 vTangentOut;
in vec2 vTexCoordOut;
in vec4 vVertexColorOut;

#if (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    in vec3 vBakedLightOut;
#endif

uniform sampler2D g_tColor; // SrgbRead(true) Sampler(UserConfig) RepresentativeTexture Default(White)
uniform sampler2D g_tDetail; // Sampler(UserConfig) Default(White)
uniform sampler2D g_tSelfIllumMask; // Sampler(UserConfig) Default(White)
uniform sampler2D g_tNormal; // Sampler(UserConfig)

uniform int F_BUMPMAP;
uniform int F_BASEMAP_ALPHA_PHONG_MASK;
uniform int F_RIM_MASK;
uniform int F_INVERT_PHONG_MASK;
uniform int F_BLEND_TINT_BY_BASE_ALPHA;
uniform vec3 g_vColorTint = vec3(1.0);
// How much of the base color the tint of a blend tinted material leaves out, so the tint replaces it at 1
uniform float g_flBlendTintColorOverBase = 0.0;
uniform vec4 g_vPhongFresnelRanges = vec4(0.0, 0.5, 1.0, 0.0);
uniform vec3 g_vPhongTint = vec3(1.0);
uniform float g_flPhongExponent = 0.0;
// When not zero, the exponent texture scales this instead of covering 1 to 150, and it wins over g_flPhongExponent
uniform float g_flPhongExponentFactor = 0.0;
uniform float g_flPhongBoost = 1.0;
uniform float g_flRimLightBoost = 1.0;

void main()
{
    vec4 baseColor = texture(g_tColor, vTexCoordOut);
    vec4 detailColor = SampleSource1Detail(g_tDetail, vTexCoordOut);
    baseColor = ApplySource1Detail(baseColor, detailColor);

    vec3 normal = GetSource1SurfaceNormal(vNormalOut, vFragPosition);
    vec3 eyeDirection = normalize(g_vCameraPositionWs - vFragPosition);
    vec3 vertexNormal = normal;

    vec4 normalTexel = vec4(127.0 / 255.0, 127.0 / 255.0, 1.0, 1.0);

    // The base alpha as the phong mask replaces the normal map with a flat normal
    bool flatNormal = F_PHONG != 0 && F_BASEMAP_ALPHA_PHONG_MASK != 0;

    if (F_BUMPMAP != 0 && !flatNormal)
    {
        normalTexel = texture(g_tNormal, vTexCoordOut);
        mat3 tangentFrame = GetSource1TangentFrame(normal, vTangentOut, vFragPosition, vTexCoordOut);
        normal = normalize(tangentFrame * (normalTexel.xyz * 2.0 - 1.0));
    }

    float fresnelRanges = GetSource1FresnelRanges(normal, eyeDirection, g_vPhongFresnelRanges);

    vec4 exponentTexel = vec4(1.0);
    float specularMask = 1.0;
    float specularExponent = 1.0;
    vec3 specularTint = vec3(1.0);
    float rimMask = 1.0;

    if (F_PHONG != 0)
    {
        if (F_PHONG_EXPONENT_TEXTURE != 0)
        {
            exponentTexel = texture(g_tPhongExponent, vTexCoordOut);
        }

        specularMask = normalTexel.a;

        if (F_BASEMAP_ALPHA_PHONG_MASK != 0 || F_BUMPMAP == 0)
        {
            specularMask = baseColor.a;
        }

        if (g_flPhongExponentFactor != 0.0)
        {
            specularExponent = 1.0 + g_flPhongExponentFactor * exponentTexel.r;
        }
        else if (g_flPhongExponent > 0.0)
        {
            specularExponent = g_flPhongExponent;
        }
        else
        {
            specularExponent = 1.0 + 149.0 * exponentTexel.r;
        }

        if (g_vPhongTint.r >= 0.0)
        {
            specularTint = g_vPhongTint;
        }
        else
        {
            specularTint = mix(vec3(1.0), baseColor.rgb, exponentTexel.g);
        }

        if (F_RIM_MASK != 0)
        {
            rimMask = exponentTexel.a;
        }
    }

    vec3 specularLight = vec3(0.0);
    vec3 rimLight = vec3(0.0);

#if (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    vec3 diffuseLight = vBakedLightOut;
    vec3 unusedDiffuse = vec3(0.0);
    AddSource1SceneLights(unusedDiffuse, specularLight, rimLight, vFragPosition, normal, eyeDirection, specularExponent, fresnelRanges);
#else
    vec3 diffuseLight = GetSource1Ambient(normal);
    AddSource1SceneLights(diffuseLight, specularLight, rimLight, vFragPosition, normal, eyeDirection, specularExponent, fresnelRanges);
    diffuseLight = GetSource1FullbrightFallback(diffuseLight, normal);
#endif

    vec3 albedo = baseColor.rgb * g_vColorTint;

    if (F_BLEND_TINT_BY_BASE_ALPHA != 0)
    {
        // The base alpha picks where the tint applies, so it is not the opacity
        vec3 tinted = g_vColorTint * mix(baseColor.rgb, vec3(1.0), g_flBlendTintColorOverBase);
        albedo = mix(baseColor.rgb, tinted, baseColor.a);
    }

    albedo *= vVertexColorOut.rgb;
    vec3 color = albedo * diffuseLight;

    // The phong shader adds the detail after the self illumination, the others before
    if (F_PHONG == 0)
    {
        color = ApplySource1DetailPostLighting(color, detailColor);
    }

    if (F_SELF_ILLUM_FRESNEL != 0)
    {
        color = ApplySource1SelfIllumFresnel(color, albedo, baseColor.a, vertexNormal, eyeDirection);
    }
    else
    {
        color = ApplySource1SelfIllum(color, albedo, baseColor.a, g_tSelfIllumMask, vTexCoordOut);
    }

    color = ApplySource1SelfIllumFromMaskAlpha(color, albedo, g_tEnvMapMask, vTexCoordOut);

    if (F_PHONG != 0)
    {
        color = ApplySource1DetailPostLighting(color, detailColor);
    }

    float phongMask = specularMask;

    if (F_PHONG != 0)
    {
        if (F_PHONG_WARP == 0)
        {
            specularMask *= fresnelRanges;
        }

        specularLight *= specularMask * g_flPhongBoost;
    }

    if (F_RIM_LIGHT != 0)
    {
        float rimFresnel = saturate(1.0 - dot(normal, eyeDirection));
        rimFresnel *= rimFresnel;
        rimFresnel *= rimFresnel;

        float rimMultiply = rimMask * rimFresnel;
        rimLight *= rimMultiply;

        specularLight = max(specularLight, rimLight);
        specularLight += GetSource1Ambient(eyeDirection) * g_flRimLightBoost * saturate(rimMultiply * normal.z);
    }

    color += specularLight * specularTint;

    if (F_ENVMAP != 0 && F_PHONG != 0)
    {
        // The phong shader masks with the base alpha or the phong mask, and has no contrast or saturation
        float envmapMask = baseColor.a;

        if (F_NORMALMAP_ALPHA_ENVMAP_MASK != 0)
        {
            envmapMask = phongMask;
        }

        if (F_INVERT_PHONG_MASK != 0)
        {
            envmapMask = 1.0 - envmapMask;
        }

        float envmapFresnel = mix(1.0, fresnelRanges, g_flEnvMapFresnel);
        color += SampleSource1Envmap(normal, vFragPosition) * envmapMask * envmapFresnel * g_vEnvMapTint;
    }
    else if (F_ENVMAP != 0)
    {
        vec3 envmapMask = GetSource1EnvmapMask(vTexCoordOut, baseColor.a, normalTexel.a);
        color += GetSource1EnvmapColor(normal, vFragPosition, envmapMask, g_vEnvMapTint);
    }

    ApplyFog(color, vFragPosition);

    // The phong shader with the base alpha as the phong mask is opaque
    bool baseAlphaIsOpacity = !flatNormal && F_BLEND_TINT_BY_BASE_ALPHA == 0;
    float baseAlpha = baseAlphaIsOpacity ? baseColor.a : 1.0;
    float alpha = GetSource1Opacity(baseAlpha, vVertexColorOut.a);
    outputColor = vec4(color, ApplySource1AlphaTest(alpha));
}
