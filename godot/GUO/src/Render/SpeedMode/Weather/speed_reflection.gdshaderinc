// SPDX-License-Identifier: BSD-2-Clause
// ADR-0048 amendment: four nearest screen-column taps, no extra geometry.
// Approved geometric plane consistency (ADR-0048, 2026-10-29); no base-Z input.
vec4 speed_reflection(sampler2D surfaces, sampler2D colors, vec2 uv,
        vec2 size, vec3 receiver, vec2 origin, float art_scale, float seconds) {
    float grid = max(art_scale, 0.125);
    float wave = floor(sin(dot(receiver.xy + origin, vec2(1.7, 2.3)) + seconds * 1.4) * 1.5 + 0.5);
    for (int k = 1; k <= 4; k++) {
        float distance_art = float(k) * 16.0;
        vec2 source_uv = uv + vec2(wave, -distance_art) * grid / size;
        if (any(lessThan(source_uv, vec2(0.0))) || any(greaterThanEqual(source_uv, vec2(1.0)))) continue;
        vec4 source = texture(surfaces, source_uv);
        int kind = int(round(source.a)) & 15;
        if (kind != 2 && kind != 3 && kind != 4) continue;
        float height = source.z - receiver.z;
        if (height <= 0.0) continue;
        // Surface XY uses the same origin at both pixels; it cancels here.
        vec2 delta = (source.xy - receiver.xy) * 22.0;
        // Upright and wall samples store Z at 4 native pixels per UO unit, the
        // same scale as the surface pass. A tap d art-pixels up matches height
        // d/4. Tolerance 1/8 Z is half of one native art pixel at that scale.
        if (abs(height - distance_art / 4.0) > 0.125) continue;
        if (abs(delta.x - delta.y - wave) > 1.0) continue;
        vec3 tint = texture(colors, source_uv).rgb * vec3(0.42, 0.49, 0.55);
        return vec4(tint, 1.0 - distance_art / 80.0);
    }
    return vec4(0.0);
}
