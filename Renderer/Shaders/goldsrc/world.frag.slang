#version 460

#include "../common/utils.slang"
#include "../common/view_constants.slang"
#include "../common/fog.slang"
#include "gamma.slang"

in vec3 vFragPosition;
in vec2 vTexCoordOut;
in vec2 vLightmapUVOut;
flat in vec4 vStylesOut;

out vec4 outputColor;

uniform sampler2D g_tColor;
uniform sampler2D g_tLightmap0;
uniform sampler2D g_tLightmap1;
uniform sampler2D g_tLightmap2;
uniform sampler2D g_tLightmap3;
uniform sampler2D g_tLightStyles;

uniform int F_ALPHA_TEST;
uniform int F_TRANSLUCENT;
uniform int F_ADDITIVE_BLEND;
uniform int F_WATER;

// 0 multiplies the texture by the lightmap, 1 draws the texture as it is, 2 draws the render color alone
uniform int g_nSurfaceMode;
uniform vec2 g_vTextureSize = vec2(64.0);
// The render color of the entity, and its opacity
uniform vec4 g_vRenderColor = vec4(1.0);
// How fast the texture moves along its horizontal axis, in texels per second
uniform float g_flTextureScroll = 0.0;

const int NoLightStyle = 255;
const int FullbrightStyle = 254;

// The light is drawn at twice its strength, so a lightmap can brighten a texture to twice its color
const float LightmapOverbright = 2.0;

// Water textures are looked up in units of 64 texels, whatever size they have
const vec2 WaterTextureSize = vec2(64.0);
const float WaterWarpAmplitude = 8.0;
const float WaterWarpFrequency = 0.125;

float GetLightStyleScale(int style)
{
    return texelFetch(g_tLightStyles, ivec2(style, 0), 0).r;
}

vec3 GetLightmap()
{
    ivec4 styles = ivec4(vStylesOut * 255.0 + 0.5);

    if (styles.x == FullbrightStyle)
    {
        return vec3(1.0);
    }

    vec3 light = vec3(0.0);

    if (styles.x != NoLightStyle)
    {
        light += texture(g_tLightmap0, vLightmapUVOut).rgb * GetLightStyleScale(styles.x);
    }

    if (styles.y != NoLightStyle)
    {
        light += texture(g_tLightmap1, vLightmapUVOut).rgb * GetLightStyleScale(styles.y);
    }

    if (styles.z != NoLightStyle)
    {
        light += texture(g_tLightmap2, vLightmapUVOut).rgb * GetLightStyleScale(styles.z);
    }

    if (styles.w != NoLightStyle)
    {
        light += texture(g_tLightmap3, vLightmapUVOut).rgb * GetLightStyleScale(styles.w);
    }

    return GoldSrcLightGamma(light) * LightmapOverbright;
}

void main()
{
    vec2 texelCoord = vTexCoordOut;
    vec2 lookupSize = g_vTextureSize;

    texelCoord.x += g_flTextureScroll * g_flTime;

    if (F_WATER != 0)
    {
        // The sine is evaluated exactly instead of being quantized to 256 steps
        texelCoord += WaterWarpAmplitude * sin(texelCoord.yx * WaterWarpFrequency + g_flTime);
        lookupSize = WaterTextureSize;
    }

    vec4 baseColor = texture(g_tColor, texelCoord / lookupSize);
    vec3 color = GoldSrcTextureGamma(baseColor.rgb);
    float alpha = 1.0;

    if (g_nSurfaceMode == 0)
    {
        color *= GetLightmap();
    }
    else if (g_nSurfaceMode == 2)
    {
        color = vec3(1.0);
    }

    // The textures and lightmaps are in the gamma space of the frame buffer
    color = SrgbGammaToLinear(saturate(color));
    color *= g_vRenderColor.rgb;

    if (F_ALPHA_TEST != 0)
    {
        alpha = saturate((baseColor.a - 0.5) / max(fwidth(baseColor.a), 0.0001) + 0.5);

        if (alpha <= 0.0)
        {
            discard;
        }
    }

    if (F_TRANSLUCENT != 0)
    {
        alpha *= g_vRenderColor.a;
    }

    if (F_ADDITIVE_BLEND != 0)
    {
        color *= GetFogAttenuation(vFragPosition);
    }
    else
    {
        ApplyFog(color, vFragPosition);
    }

    outputColor = vec4(color, alpha);
}
