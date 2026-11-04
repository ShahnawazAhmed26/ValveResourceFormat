#version 460

#include "unlitgeneric_features.slang"
#include "common_ps.slang"

in vec3 vFragPosition;
in vec2 vTexCoordOut;

// Read as stored: the blend multiplies the framebuffer by this gamma space value twice, so 0.5 leaves it as it is
uniform sampler2D g_tColor; // Sampler(UserConfig) RepresentativeTexture Default(White)

void main()
{
    vec4 baseColor = texture(g_tColor, vTexCoordOut);

    if (baseColor.a <= 0.0)
    {
        discard;
    }

    // Fog fades the decal to the gray that leaves the framebuffer as it is, raised to a power so that a dark fog does not leave the decal visible
    float fogAmount = 1.0 - GetFogAttenuation(vFragPosition);
    vec3 color = mix(baseColor.rgb, vec3(0.5), pow(saturate(fogAmount), 0.4));

    outputColor = vec4(color, 1.0);
}
