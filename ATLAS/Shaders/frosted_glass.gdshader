// ATLAS frosted glass panel (Godot 4, canvas_item).
//
// Setup: put this ShaderMaterial on a ColorRect that sits behind the panel content
// (AtlasUI does this automatically and feeds `rect_size` on resize).
//
// Blur: `hint_screen_texture` with `filter_linear_mipmap` gives the shader a mip chain of the
// frame behind the panel. Sampling a higher LOD already blurs; the 8 extra taps around the
// pixel (offset by `blur_radius`) smooth the blocky look of low mips. Declaring the sampler
// alone does NOT blur anything - the textureLod + taps below are what create the effect.
//
// Requirements: Forward+ or Mobile renderer (screen texture), and the panel must be drawn on top
// of the 3D scene (a CanvasLayer UI is). Nothing else needs configuring in the project.
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear_mipmap;

uniform vec2 rect_size = vec2(300.0, 200.0);
uniform float corner_radius = 14.0;
uniform float border_width = 1.2;
uniform float blur_lod : hint_range(0.0, 6.0) = 2.6;
uniform float blur_radius = 2.5;
uniform vec4 tint : source_color = vec4(0.035, 0.075, 0.18, 0.80);
uniform vec4 border_color : source_color = vec4(0.22, 0.45, 0.95, 0.55);
uniform float top_highlight : hint_range(0.0, 0.5) = 0.10;

const vec2 TAPS[8] = vec2[8](
	vec2(1.0, 0.0), vec2(-1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, -1.0),
	vec2(0.7071, 0.7071), vec2(-0.7071, 0.7071), vec2(0.7071, -0.7071), vec2(-0.7071, -0.7071)
);

float sd_round_rect(vec2 p, vec2 half_size, float r) {
	vec2 q = abs(p) - half_size + vec2(r);
	return length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - r;
}

void fragment() {
	vec2 p = (UV - vec2(0.5)) * rect_size;
	float d = sd_round_rect(p, rect_size * 0.5, corner_radius);

	// shape mask with ~1px antialiasing
	float mask = 1.0 - smoothstep(-1.0, 0.0, d);

	// multi-tap blur of what is behind the panel
	vec2 px = SCREEN_PIXEL_SIZE * blur_radius;
	vec3 acc = textureLod(screen_tex, SCREEN_UV, blur_lod).rgb * 0.36;
	for (int i = 0; i < 8; i++) {
		acc += textureLod(screen_tex, SCREEN_UV + TAPS[i] * px, blur_lod).rgb * 0.08;
	}

	vec3 col = mix(acc, tint.rgb, tint.a);

	// soft highlight fading from the top edge
	col += vec3(top_highlight) * (1.0 - UV.y) * (1.0 - UV.y);

	// translucent border hugging the inside of the shape
	float edge = 1.0 - smoothstep(0.0, border_width, -d);
	edge *= step(d, 0.0);
	col = mix(col, border_color.rgb, edge * border_color.a);

	COLOR = vec4(col, mask);
}
