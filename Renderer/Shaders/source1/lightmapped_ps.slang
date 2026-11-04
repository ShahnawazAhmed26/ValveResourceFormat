#version 460

#include "common_ps.slang"
#include "common_envmap_ps.slang"
#include "common_rendermodes_lighting_ps.slang"

in vec3 vFragPosition;
in vec3 vNormalOut;
in vec4 vTangentOut;
in vec2 vTexCoordOut;
in vec2 vBumpTexCoordOut;
in vec2 vEnvMapMaskTexCoordOut;
in vec2 vBlendMaskTexCoordOut;
in vec2 vLightmapUVOut;
in vec4 vVertexColorOut;
in float vBlendOut;

uniform sampler2D g_tColor; // SrgbRead(true) Sampler(UserConfig) RepresentativeTexture Default(White)
uniform sampler2D g_tColor2; // SrgbRead(true) Sampler(UserConfig) Default(White)
uniform sampler2D g_tDetail; // Sampler(UserConfig) Default(White)
uniform sampler2D g_tSelfIllumMask; // Sampler(UserConfig) Default(White)
uniform sampler2D g_tBlendModulate; // Sampler(UserConfig) Default(White)
uniform sampler2D g_tNormal; // Sampler(UserConfig)
uniform sampler2D g_tNormal2; // Sampler(UserConfig)
uniform sampler2D g_tLightWarp; // Sampler(Clamp) Default(White)
uniform sampler2D g_tLightmap; // Default(White)
uniform sampler2D g_tLightmapBump1; // Default(White)
uniform sampler2D g_tLightmapBump2; // Default(White)
uniform sampler2D g_tLightmapBump3; // Default(White)

uniform int F_BLEND_MODULATE;
// The blend of the two layers comes from the blend modulate texture alone, not from the vertices
uniform int F_MASKED_BLENDING;
// A layer whose normal map alpha masks the environment map can switch the reflection off
uniform int F_BASE_TEXTURE_NO_ENVMAP;
uniform int F_BASE_TEXTURE2_NO_ENVMAP;
uniform int F_BUMPMAP;
uniform int F_SSBUMP;
uniform int F_BUMPED_LIGHTMAP;
uniform int F_LIGHTWARP;
// The tint multiplies the light, in the color space it was written in
uniform vec3 g_vColorTint = vec3(1.0);
uniform float g_flLightmapScale = 1.0;
// When not zero, the base texture is projected along the three world axes instead of using the texture coordinates
uniform float g_flSeamlessScale = 0.0;

// The detail blend mode of a detail texture that holds the weights of the bumped lightmaps
#define SOURCE1_DETAIL_BLEND_BUMP_WEIGHTS 10

// The base textures and the bump map share the projection
vec4 SampleSource1SurfaceTexture(sampler2D surfaceTexture, vec2 texCoord)
{
    if (g_flSeamlessScale == 0.0)
    {
        return texture(surfaceTexture, texCoord);
    }

    vec3 position = vFragPosition * g_flSeamlessScale;
    vec3 normal = normalize(vNormalOut);
    vec3 weights = normal * normal;

    return texture(surfaceTexture, position.xz) * weights.y
        + texture(surfaceTexture, position.zy) * weights.x
        + texture(surfaceTexture, position.xy) * weights.z;
}

// Cubic B-spline filtering of a lightmap page from four bilinear taps, to hide the luxel steps of the coarse lightmaps.
// It reads up to two luxels beyond the sampled one, which stays inside the repeated border of a face.
vec3 SampleSource1Lightmap(sampler2D page, vec2 uv)
{
    vec2 size = vec2(textureSize(page, 0));
    vec2 texel = uv * size - 0.5;
    vec2 base = floor(texel);
    vec2 f = texel - base;
    vec2 f2 = f * f;
    vec2 f3 = f2 * f;

    vec2 w0 = (-f3 + 3.0 * f2 - 3.0 * f + 1.0) / 6.0;
    vec2 w1 = (3.0 * f3 - 6.0 * f2 + 4.0) / 6.0;
    vec2 w2 = (-3.0 * f3 + 3.0 * f2 + 3.0 * f + 1.0) / 6.0;
    vec2 w3 = f3 / 6.0;

    vec2 weight0 = w0 + w1;
    vec2 weight1 = w2 + w3;

    vec2 uv0 = (base - 0.5 + w1 / weight0) / size;
    vec2 uv1 = (base + 1.5 + w3 / weight1) / size;

    vec3 light = textureLod(page, uv0, 0.0).rgb * weight0.x * weight0.y;
    light += textureLod(page, vec2(uv1.x, uv0.y), 0.0).rgb * weight1.x * weight0.y;
    light += textureLod(page, vec2(uv0.x, uv1.y), 0.0).rgb * weight0.x * weight1.y;
    light += textureLod(page, vec2(uv1.x, uv1.y), 0.0).rgb * weight1.x * weight1.y;

    return light;
}

// The light on a bump mapped surface, from the three bumped lightmaps. tangentNormal is the normal of the bump map in
// tangent space, or for a self shadowed bump map the weights of the three lightmaps.
vec3 GetSource1BumpedLightmap(vec2 lightmapCoord, vec3 tangentNormal, vec4 detailColor)
{
    vec3 light1 = SampleSource1Lightmap(g_tLightmapBump1, lightmapCoord);
    vec3 light2 = SampleSource1Lightmap(g_tLightmapBump2, lightmapCoord);
    vec3 light3 = SampleSource1Lightmap(g_tLightmapBump3, lightmapCoord);

    if (F_SSBUMP != 0)
    {
        return tangentNormal.x * light1 + tangentNormal.y * light2 + tangentNormal.z * light3;
    }

    vec3 weights;
    weights.x = saturate(dot(tangentNormal, SOURCE1_BUMP_BASIS[0]));
    weights.y = saturate(dot(tangentNormal, SOURCE1_BUMP_BASIS[1]));
    weights.z = saturate(dot(tangentNormal, SOURCE1_BUMP_BASIS[2]));
    weights *= weights;

    if (F_DETAIL != 0 && g_nDetailBlendMode == SOURCE1_DETAIL_BLEND_BUMP_WEIGHTS)
    {
        weights *= 2.0 * detailColor.rgb;
    }

    float sum = max(weights.x + weights.y + weights.z, 0.0001);

    return (weights.x * light1 + weights.y * light2 + weights.z * light3) / sum;
}

// A self shadowed bump map holds weights, the others hold a signed normal
vec3 DecodeSource1BumpTexel(vec3 texel)
{
    if (F_SSBUMP != 0)
    {
        return texel;
    }

    return texel * 2.0 - 1.0;
}

// The world space normal of the bump map
vec3 GetSource1BumpedNormal(vec3 normal, vec3 tangentNormal)
{
    vec3 bumpNormal = tangentNormal;

    if (F_SSBUMP != 0)
    {
        bumpNormal = normalize(SOURCE1_BUMP_BASIS[0] * tangentNormal.x
            + SOURCE1_BUMP_BASIS[1] * tangentNormal.y
            + SOURCE1_BUMP_BASIS[2] * tangentNormal.z);
    }

    mat3 tangentFrame = GetSource1TangentFrame(normal, vTangentOut, vFragPosition, vBumpTexCoordOut);
    return normalize(tangentFrame * bumpNormal);
}

#if (SOURCE1_WORLD_TWO_TEXTURE_BLEND == 1)
// The detail alpha masks the base texture, which it multiplies
uniform int F_DETAIL_ALPHA_MASK_BASE_TEXTURE;

// The detail texture replaces the base color where its alpha is set. With the alpha mask it is instead multiplied by
// the doubled base color, which is only shown where the detail alpha is set.
vec3 BlendSource1WorldDetail(vec3 baseColor, vec4 detailColor)
{
    if (F_DETAIL == 0)
    {
        return baseColor;
    }

    if (F_DETAIL_ALPHA_MASK_BASE_TEXTURE != 0)
    {
        vec3 masked = saturate(saturate(baseColor * 2.0) * detailColor.a + (1.0 - detailColor.a));
        return masked * detailColor.rgb;
    }

    return mix(baseColor, detailColor.rgb, detailColor.a);
}
#endif

void main()
{
    vec4 baseColor = SampleSource1SurfaceTexture(g_tColor, vTexCoordOut);

    // The opacity and the self illumination use the alpha of the first texture, the environment map mask the blended one
    float baseAlpha = baseColor.a;
    float blendedAlpha = baseAlpha;
    float blend = 0.0;

#if (SOURCE1_BLEND_BY_VERTEX_ALPHA == 1)
    blend = vBlendOut;

    if (F_MASKED_BLENDING != 0)
    {
        blend = 0.5;
    }

#if (SOURCE1_BASE_TEXTURE2 == 1)
    // The blend modulate texture only applies to a surface that does not glow
    if (F_BLEND_MODULATE != 0 && F_SELF_ILLUM == 0)
    {
        vec4 modulate = texture(g_tBlendModulate, vBlendMaskTexCoordOut);

        if (F_MASKED_BLENDING != 0)
        {
            blend = modulate.g;
        }
        else
        {
            float low = saturate(modulate.g - modulate.r);
            float high = saturate(modulate.g + modulate.r);
            blend = smoothstep(low, max(high, low + 0.0001), blend);
        }
    }
#endif
#endif

#if (SOURCE1_BASE_TEXTURE2 == 1)
    vec4 baseColor2 = SampleSource1SurfaceTexture(g_tColor2, vTexCoordOut);

    baseColor.rgb = mix(baseColor.rgb, baseColor2.rgb, blend);
    blendedAlpha = mix(baseAlpha, baseColor2.a, blend);
#endif

    vec4 detailColor = SampleSource1Detail(g_tDetail, vTexCoordOut);

#if (SOURCE1_WORLD_TWO_TEXTURE_BLEND == 1)
    vec3 albedo = BlendSource1WorldDetail(baseColor.rgb, detailColor) * vVertexColorOut.rgb;
#else
    vec3 albedo = ApplySource1Detail(baseColor, detailColor).rgb * vVertexColorOut.rgb;
#endif

    vec4 normalTexel = vec4(127.0 / 255.0, 127.0 / 255.0, 1.0, 1.0);
    vec3 tangentNormal = vec3(0.0, 0.0, 1.0);

    if (F_BUMPMAP != 0)
    {
        normalTexel = SampleSource1SurfaceTexture(g_tNormal, vBumpTexCoordOut);
        tangentNormal = DecodeSource1BumpTexel(normalTexel.xyz);

        if (F_BASE_TEXTURE_NO_ENVMAP != 0)
        {
            normalTexel.a = 0.0;
        }

#if (SOURCE1_BUMPMAP2 == 1)
        vec4 normalTexel2 = texture(g_tNormal2, vBumpTexCoordOut);

        if (F_BASE_TEXTURE2_NO_ENVMAP != 0)
        {
            normalTexel2.a = 0.0;
        }

        vec3 tangentNormal2 = DecodeSource1BumpTexel(normalTexel2.xyz);
        tangentNormal = mix(tangentNormal, tangentNormal2, blend);

        // Only a blend modulate texture blends the alpha of the normal maps too
        if (F_BLEND_MODULATE != 0)
        {
            normalTexel.a = mix(normalTexel.a, normalTexel2.a, blend);
        }
#endif
    }

    vec3 lightmap;

    if (F_BUMPMAP != 0 && F_BUMPED_LIGHTMAP != 0)
    {
        lightmap = GetSource1BumpedLightmap(vLightmapUVOut, tangentNormal, detailColor);
    }
    else
    {
        lightmap = SampleSource1Lightmap(g_tLightmap, vLightmapUVOut);
    }

    lightmap *= g_vColorTint * g_flLightmapScale;

    if (F_LIGHTWARP != 0 && F_DETAIL == 0 && g_flSeamlessScale == 0.0)
    {
        float warpCoord = 0.5 * length(lightmap);
        lightmap *= 2.0 * texture(g_tLightWarp, vec2(warpCoord, 0.0)).rgb;
    }

    vec3 color = albedo * lightmap;

    color = ApplySource1SelfIllum(color, albedo, baseAlpha, g_tSelfIllumMask, vTexCoordOut);

    // Only the additive detail blend mode adds to the lit color, mode 6 has no lightmapped variant
    if (F_DETAIL != 0 && g_nDetailBlendMode == 5)
    {
        color += g_flDetailBlendFactor * detailColor.rgb;
    }

    vec3 geometricNormal = GetSource1SurfaceNormal(vNormalOut, vFragPosition);
    vec3 normal = geometricNormal;
    vec3 eyeDirection = normalize(g_vCameraPositionWs - vFragPosition);
    vec3 specularLight = vec3(0.0);

    if (F_BUMPMAP != 0 && (F_ENVMAP != 0 || g_iRenderMode != 0))
    {
        normal = GetSource1BumpedNormal(geometricNormal, tangentNormal);
    }

    if (F_ENVMAP != 0)
    {
        float fresnel = pow(max(1.0 - dot(normal, eyeDirection), 0.0), 5.0);
        fresnel = fresnel * (1.0 - g_flFresnelReflection) + g_flFresnelReflection;

        vec3 envmapMask = GetSource1EnvmapMask(vEnvMapMaskTexCoordOut, blendedAlpha, normalTexel.a);
        specularLight = GetSource1EnvmapColor(normal, vFragPosition, envmapMask, g_vEnvMapTint) * fresnel;
        color += specularLight;
    }

    ApplyFog(color, vFragPosition);

    float alpha = GetSource1Opacity(baseAlpha, vVertexColorOut.a);
    outputColor = vec4(color, ApplySource1AlphaTest(alpha));

    if (g_iRenderMode != 0)
    {
        vec3 tangent = GetSource1TangentFrame(geometricNormal, vTangentOut, vFragPosition, vBumpTexCoordOut)[0];

        ApplySource1RenderModes(outputColor, albedo, vVertexColorOut, normal, eyeDirection, g_tColor, vTexCoordOut);
        ApplySource1NormalRenderModes(outputColor, geometricNormal, tangent, tangentNormal, normal);
        ApplySource1LightingRenderModes(outputColor, lightmap, specularLight);
        ApplySource1CubemapRenderMode(outputColor, normal, vFragPosition);
    }
}
