#version 460

#ifndef VRF_GOLDSRC_STUDIO_COMMON_INCLUDE
#define VRF_GOLDSRC_STUDIO_COMMON_INCLUDE

#include "../common/utils.slang"
#include "gamma.slang"

// Texture flags of the model, set per material
uniform int F_ALPHA_TEST;
uniform int F_TRANSLUCENT;
uniform int F_CHROME;
uniform int F_FLAT_SHADE;

const int RenderModeColor = 1;
const int RenderModeAdditive = 5;

// The render mode of the entity, its render color and its opacity in alpha. The color is in gamma space.
uniform int g_nRenderMode;
uniform vec4 g_vRenderColor = vec4(1.0);

// Light that comes from one direction, travelling along g_vLightDirection, on top of ambient light. Both are
// brightnesses from 0 to 1 in the space of the light gamma curve, and g_vLightColor tints them. The map
// sets them from the lightmap under the model; these are the values of a model that is shown alone.
uniform float g_flAmbientLight = 0.3;
uniform float g_flShadeLight = 0.6;
uniform vec3 g_vLightColor = vec3(1.0);
uniform vec3 g_vLightDirection = vec3(0.0, 0.0, -1.0);

// How far the light wraps around a sphere, where 1 is a plain dot product
uniform float g_flLambert = 1.4953241;

// Chrome textures are 64 texels wide in the space the reflection is looked up in
const float ChromeTexelScale = 32.0;

// A flat shaded surface takes this part of the shade light, whatever way it faces
const float FlatShadeFraction = 0.8;

vec3 GetStudioLight(vec3 normal)
{
    float shade = g_flShadeLight;

    if (F_FLAT_SHADE == 0)
    {
        float lightCos = min(dot(normal, g_vLightDirection), 1.0);
        float wrapped = (g_flLambert - 1.0 + lightCos) / g_flLambert;
        shade *= 1.0 - max(wrapped, 0.0);
    }
    else
    {
        shade *= FlatShadeFraction;
    }

    return GoldSrcLightGamma(vec3(g_flAmbientLight + shade)) * g_vLightColor;
}

#endif
