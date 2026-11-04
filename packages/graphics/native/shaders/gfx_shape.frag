#version 450
// The coverage of a pixel as the software draws it (packages/graphics/native/fire_gfx_fx.h): the signed distance of its centre to the edge of the rounded
// rectangle, coverage = clamp(0.5 - d); an outline is the coverage of the outer box less that of the inner one.
layout(location = 0) in vec4 vColor;
layout(location = 1) flat in vec4 vBox;
layout(location = 2) flat in vec4 vParam;
layout(location = 0) out vec4 outColor;

float distanceTo(vec2 p, vec4 box, float r) {
    vec2 h = box.zw * 0.5;
    r = clamp(r, 0.0, min(h.x, h.y));
    vec2 q = abs(p - (box.xy + h)) - (h - r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void main() {
    vec2 p = gl_FragCoord.xy;
    float radius = min(vParam.x, min(vBox.z, vBox.w) * 0.5);
    float cover = clamp(0.5 - distanceTo(p, vBox, radius), 0.0, 1.0);
    if (vParam.y > 0.0) {
        vec4 inner = vec4(vBox.xy + vParam.y, vBox.zw - 2.0 * vParam.y);
        cover -= clamp(0.5 - distanceTo(p, inner, max(radius - vParam.y, 0.0)), 0.0, 1.0);
    }
    if (vParam.z > 0.5) {
        if (cover < 0.5) discard;
        outColor = vColor;
        return;
    }
    if (cover <= 0.0) discard;
    outColor = vec4(vColor.rgb, vColor.a * cover);
}
