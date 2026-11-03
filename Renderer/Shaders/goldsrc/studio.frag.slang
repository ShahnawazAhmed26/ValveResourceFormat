#version 460

#include "studio_common.slang"
#include "../common/view_constants.slang"
#include "../common/fog.slang"

in vec3 vFragPosition;
in vec2 vTexCoordOut;
in vec3 vLightOut;

out vec4 outputColor;

uniform sampler2D g_tColor;

void main()
{
    vec4 texel = texture(g_tColor, vTexCoordOut / vec2(textureSize(g_tColor, 0)));

    if (F_ALPHA_TEST != 0 && texel.a < 0.5)
    {
        discard;
    }

    // Blending happens in gamma space, so the light is applied there and the result converted for the linear pipeline
    vec3 shaded = GoldSrcTextureGamma(texel.rgb) * vLightOut;
    float alpha = 1.0;

    if (g_nRenderMode == RenderModeColor)
    {
        // Only the alpha of the texture is used, the color comes from the entity
        shaded = g_vRenderColor.rgb;
        alpha = texel.a * g_vRenderColor.a;
    }
    else if (g_nRenderMode != RenderModeAdditive && F_TRANSLUCENT != 0)
    {
        alpha = g_vRenderColor.a;
    }

    vec3 color = SrgbGammaToLinear(saturate(shaded));
    ApplyFog(color, vFragPosition);

    outputColor = vec4(color, alpha);
}
