#version 460

#define F_VERTEX_COLOR 0
#define F_VERTEX_ALPHA 0
// D_BAKED_LIGHTING_FROM_VERTEX_STREAM takes the diffuse light baked per vertex in the second color stream, instead of the ambient cube and the sun
#include "../common/shared_combos.slang"

#define SOURCE1_DETAIL_SRGB_ALL_MODES 1
