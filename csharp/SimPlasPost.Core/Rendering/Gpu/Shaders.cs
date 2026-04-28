namespace SimPlasPost.Core.Rendering.Gpu;

/// <summary>
/// GLSL shader sources for the Veldrid OpenGL backend.
///
/// Coordinate convention: vertex positions are already in screen pixels
/// (origin top-left, y goes down) plus a depth value in eye-Z space.
/// The vertex shader maps them to clip space using a uniform viewport size.
/// </summary>
internal static class Shaders
{
    // Per-vertex color triangle (Gouraud) — used for the filled mesh.
    public const string MeshVert = @"#version 330 core
layout(location = 0) in vec3 in_pos;
layout(location = 1) in vec4 in_col;
layout(std140) uniform Uniforms { vec4 viewport_depth; };  // x=w, y=h, z=zNear, w=zFar
out vec4 v_col;
void main()
{
    float w = viewport_depth.x;
    float h = viewport_depth.y;
    float zNear = viewport_depth.z;
    float zFar  = viewport_depth.w;
    // Pixel coords -> clip space [-1,1].  Y axis is flipped (Avalonia top-left origin).
    float cx = (in_pos.x / w) * 2.0 - 1.0;
    float cy = 1.0 - (in_pos.y / h) * 2.0;
    // Depth is in eye-Z (camera forward); larger = further.  Map to NDC [-1,1].
    float zSpan = max(zFar - zNear, 1e-6);
    float cz = ((in_pos.z - zNear) / zSpan) * 2.0 - 1.0;
    gl_Position = vec4(cx, cy, cz, 1.0);
    v_col = in_col;
}";

    public const string MeshFrag = @"#version 330 core
in vec4 v_col;
out vec4 frag;
void main() { frag = v_col; }";

    // Solid-color line shader — used for face edges, feature edges, and contour
    // iso-lines. The color is a per-vertex attribute so the same pipeline serves
    // contour lines (per-segment color) and edges (uniform color baked per-vertex).
    public const string LineVert = MeshVert;
    public const string LineFrag = MeshFrag;

    // Depth-only pass: same vertex layout (position + dummy color), no fragment color.
    // Used for the offscreen depth pre-pass that drives hidden-line removal in the
    // PDF exporter.
    public const string DepthVert = MeshVert;
    public const string DepthFrag = @"#version 330 core
out vec4 frag;
void main() { frag = vec4(0.0); }";
}
