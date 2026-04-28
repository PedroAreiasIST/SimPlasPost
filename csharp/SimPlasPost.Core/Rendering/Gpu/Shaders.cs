namespace SimPlasPost.Core.Rendering.Gpu;

/// <summary>
/// Shader bodies, written in a profile-agnostic style. The host prepends a
/// version line (and, for GLES, a default float-precision qualifier) at
/// device-creation time — see <see cref="VeldridBackend.ShaderHeader"/>.
///
/// Coordinate convention: vertex positions are already in screen pixels
/// (origin top-left, y goes down) plus a depth value in eye-Z space.
/// The vertex shader maps them to clip space using a uniform viewport size.
/// </summary>
internal static class Shaders
{
    public const string HeaderDesktop = "#version 330 core\n";
    public const string HeaderGles    = "#version 300 es\nprecision highp float;\n";

    // Per-vertex color triangle (Gouraud) — used for the filled mesh.
    public const string MeshVert = @"
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
    float cx = (in_pos.x / w) * 2.0 - 1.0;
    float cy = 1.0 - (in_pos.y / h) * 2.0;
    float zSpan = max(zFar - zNear, 1e-6);
    float cz = ((in_pos.z - zNear) / zSpan) * 2.0 - 1.0;
    gl_Position = vec4(cx, cy, cz, 1.0);
    v_col = in_col;
}";

    public const string MeshFrag = @"
in vec4 v_col;
out vec4 frag;
void main() { frag = v_col; }";

    // Lines reuse the mesh shaders.
    public const string LineVert = MeshVert;
    public const string LineFrag = MeshFrag;

    // Depth pre-pass: writes eye-Z to a single-channel R32_Float color
    // attachment so the readback layout is well-defined regardless of the
    // driver's depth-format packing.
    public const string DepthVert = @"
layout(location = 0) in vec3 in_pos;
layout(location = 1) in vec4 in_col;
layout(std140) uniform Uniforms { vec4 viewport_depth; };
out float v_eyez;
void main()
{
    float w = viewport_depth.x;
    float h = viewport_depth.y;
    float zNear = viewport_depth.z;
    float zFar  = viewport_depth.w;
    float cx = (in_pos.x / w) * 2.0 - 1.0;
    float cy = 1.0 - (in_pos.y / h) * 2.0;
    float zSpan = max(zFar - zNear, 1e-6);
    float cz = ((in_pos.z - zNear) / zSpan) * 2.0 - 1.0;
    gl_Position = vec4(cx, cy, cz, 1.0);
    v_eyez = in_pos.z;
}";

    public const string DepthFrag = @"
in float v_eyez;
out float frag;
void main() { frag = v_eyez; }";
}
