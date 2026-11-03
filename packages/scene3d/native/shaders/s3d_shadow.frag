#version 450
// fire scene3d, hardware backend: the distance to the light of the nearest caster (the depth test keeps the nearest).
layout(location = 0) in float vDepth;
layout(location = 0) out float outDepth;
void main() { outDepth = vDepth; }
