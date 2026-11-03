#version 460

#ifndef VRF_GOLDSRC_GAMMA_INCLUDE
#define VRF_GOLDSRC_GAMMA_INCLUDE

#include "../common/utils.slang"

// Shading happens in gamma space and the result is drawn as it is. The curves below use these exponents,
// and no extra brightness curve is applied.
uniform float g_flTexGamma = 2.0;
uniform float g_flGamma = 2.2;
uniform float g_flLightGamma = 2.5;

// The curve of the palette of every texture
vec3 GoldSrcTextureGamma(vec3 color)
{
    return pow(color, vec3(g_flTexGamma / g_flGamma));
}

// The curve of light before it multiplies a texture
vec3 GoldSrcLightGamma(vec3 light)
{
    return pow(saturate(light), vec3(g_flLightGamma / g_flGamma));
}

#endif
