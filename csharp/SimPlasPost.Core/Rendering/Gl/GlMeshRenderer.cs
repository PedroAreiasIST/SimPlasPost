using System.Runtime.InteropServices;
using System.Text;

namespace SimPlasPost.Core.Rendering.Gl;

/// <summary>
/// Per-vertex layout: 12 bytes position (screen-pixel x/y, eye-Z) + 4 bytes
/// RGBA color (linear, 0..255 normalized to 0..1 by the GL pipeline).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GlVertex
{
    public float X, Y, Z;
    public byte R, G, B, A;
    public const int SizeInBytes = 16;

    public GlVertex(float x, float y, float z, byte r, byte g, byte b, byte a = 255)
    { X = x; Y = y; Z = z; R = r; G = g; B = b; A = a; }
}

/// <summary>
/// Raw-GL renderer for the SimPlasPost viewport. Owns:
///   • one shader program (smooth Gouraud — used for both filled triangles
///     and lines, since the line color is per-vertex)
///   • three vertex/index buffer pairs (mesh, edges, segments)
///
/// Coordinate convention: vertex positions are already in screen pixels
/// (origin top-left, y down) plus an eye-Z value. The vertex shader maps
/// pixels to clip space using a uniform viewport size and linearly maps
/// eye-Z to NDC depth using uniform near/far values.
///
/// All GL calls happen on the thread that constructed this renderer (the
/// Avalonia UI thread inside OnOpenGlRender). No cross-thread access.
/// </summary>
public sealed unsafe class GlMeshRenderer : IDisposable
{
    private const string MeshVertBody = @"
in vec3 in_pos;
in vec4 in_col;
uniform vec4 viewport_depth;  // x=w, y=h, z=zNear, w=zFar
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

    private const string MeshFragBody = @"
in vec4 v_col;
out vec4 frag;
void main() { frag = v_col; }";

    // GLSL 1.30 is the lowest version with `in`/`out`, supported on every
    // desktop GL 3.0+ context.  We deliberately don't use uniform blocks
    // (1.40+) so the shader compiles on pre-3.1 GLSL too, sidestepping
    // NVIDIA's notoriously buggy Cg-compiler path on compatibility profiles.
    private const string HeaderDesktop = "#version 130\n";
    private const string HeaderGles    = "#version 300 es\nprecision highp float;\n";

    private readonly GlBindings _gl;
    private readonly uint _program;
    private readonly int _locPos;
    private readonly int _locCol;
    private readonly int _locViewportDepth;

    private uint _meshVbo, _meshIbo;
    private uint _edgeVbo;
    private uint _segVbo;
    private int _meshVboSize, _meshIboSize, _edgeVboSize, _segVboSize;
    private int _meshIndexCount;
    private int _edgeVertexCount;
    private int _segVertexCount;

    public GlMeshRenderer(GlBindings gl, Action<string>? log = null)
    {
        _gl = gl;
        var header = gl.IsGles ? HeaderGles : HeaderDesktop;
        log?.Invoke($"GlMeshRenderer: header={header.Replace("\n", "\\n")}");

        var vs = CompileShader(GlBindings.GL_VERTEX_SHADER,   header + MeshVertBody, log, "vert");
        var fs = CompileShader(GlBindings.GL_FRAGMENT_SHADER, header + MeshFragBody, log, "frag");

        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, vs);
        _gl.AttachShader(_program, fs);
        // Bind attribute locations BEFORE linking so we don't need
        // layout(location=...) in the shader source.
        BindAttribLocation(_program, 0, "in_pos");
        BindAttribLocation(_program, 1, "in_col");
        _gl.LinkProgram(_program);

        int linked = 0;
        _gl.GetProgramiv(_program, GlBindings.GL_LINK_STATUS, &linked);
        if (linked == 0)
        {
            var info = _gl.GetProgramInfoLogString(_program);
            throw new InvalidOperationException("Program link failed: " + info);
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        _locPos = GetAttribLocation(_program, "in_pos");
        _locCol = GetAttribLocation(_program, "in_col");
        _locViewportDepth = GetUniformLocation(_program, "viewport_depth");
        log?.Invoke($"GlMeshRenderer: locPos={_locPos} locCol={_locCol} locViewportDepth={_locViewportDepth}");
    }

    private uint CompileShader(uint type, string src, Action<string>? log, string tag)
    {
        var id = _gl.CreateShader(type);
        var srcBytes = Encoding.UTF8.GetBytes(src);
        fixed (byte* ptrSrc = srcBytes)
        {
            IntPtr* strs = stackalloc IntPtr[1];
            int* lens = stackalloc int[1];
            strs[0] = (IntPtr)ptrSrc;
            lens[0] = srcBytes.Length;
            _gl.ShaderSource(id, 1, strs, lens);
        }
        _gl.CompileShader(id);
        int compiled = 0;
        _gl.GetShaderiv(id, GlBindings.GL_COMPILE_STATUS, &compiled);
        if (compiled == 0)
        {
            var info = _gl.GetShaderInfoLogString(id);
            throw new InvalidOperationException($"Shader compile failed ({tag}): {info}\n--- source ---\n{src}");
        }
        log?.Invoke($"GlMeshRenderer: shader {tag} compiled");
        return id;
    }

    private int GetAttribLocation(uint program, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return _gl.GetAttribLocation(program, p);
    }

    private int GetUniformLocation(uint program, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return _gl.GetUniformLocation(program, p);
    }

    private void BindAttribLocation(uint program, uint index, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) _gl.BindAttribLocation(program, index, p);
    }

    private uint EnsureBuffer(ref uint id)
    {
        if (id != 0) return id;
        uint b = 0;
        _gl.GenBuffers(1, &b);
        id = b;
        return id;
    }

    private void UploadBuffer<T>(ref uint id, ref int sizeBytes, uint target, T[] data) where T : unmanaged
    {
        EnsureBuffer(ref id);
        _gl.BindBuffer(target, id);
        int byteSize = data.Length * sizeof(T);
        fixed (T* p = data)
        {
            _gl.BufferData(target, (IntPtr)byteSize, (IntPtr)p, GlBindings.GL_DYNAMIC_DRAW);
        }
        sizeBytes = byteSize;
    }

    /// <summary>
    /// Upload triangulated mesh faces (already projected to screen) plus
    /// per-vertex colors. Faces are fan-triangulated.
    /// </summary>
    public void UploadMesh(
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, ReadOnlySpan<float> sz,
        ReadOnlySpan<byte> vR, ReadOnlySpan<byte> vG, ReadOnlySpan<byte> vB,
        int[] faceOffsets, int[] faceVertices)
    {
        int nVerts = sx.Length;
        var verts = new GlVertex[nVerts];
        for (int i = 0; i < nVerts; i++)
            verts[i] = new GlVertex(sx[i], sy[i], sz[i], vR[i], vG[i], vB[i]);

        int nFaces = faceOffsets.Length - 1;
        int triCount = 0;
        for (int f = 0; f < nFaces; f++)
        {
            int nv = faceOffsets[f + 1] - faceOffsets[f];
            if (nv >= 3) triCount += nv - 2;
        }
        var indices = new uint[triCount * 3];
        int wi = 0;
        for (int f = 0; f < nFaces; f++)
        {
            int s = faceOffsets[f], e = faceOffsets[f + 1], nv = e - s;
            if (nv < 3) continue;
            uint v0 = (uint)faceVertices[s];
            for (int t = 1; t < nv - 1; t++)
            {
                indices[wi++] = v0;
                indices[wi++] = (uint)faceVertices[s + t];
                indices[wi++] = (uint)faceVertices[s + t + 1];
            }
        }

        UploadBuffer(ref _meshVbo, ref _meshVboSize, GlBindings.GL_ARRAY_BUFFER, verts);
        UploadBuffer(ref _meshIbo, ref _meshIboSize, GlBindings.GL_ELEMENT_ARRAY_BUFFER, indices);
        _meshIndexCount = indices.Length;
    }

    public void UploadEdges(
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, ReadOnlySpan<float> sz,
        int[] faceOffsets, int[] faceVertices, bool[] drawEdges,
        byte er, byte eg, byte eb)
    {
        int nFaces = faceOffsets.Length - 1;
        int segCount = 0;
        for (int f = 0; f < nFaces; f++)
        {
            if (!drawEdges[f]) continue;
            int nv = faceOffsets[f + 1] - faceOffsets[f];
            if (nv >= 2) segCount += nv;
        }
        if (segCount == 0) { _edgeVertexCount = 0; return; }

        var verts = new GlVertex[segCount * 2];
        int wi = 0;
        for (int f = 0; f < nFaces; f++)
        {
            if (!drawEdges[f]) continue;
            int s = faceOffsets[f], e = faceOffsets[f + 1], nv = e - s;
            if (nv < 2) continue;
            for (int j = 0; j < nv; j++)
            {
                int a = faceVertices[s + j], b = faceVertices[s + (j + 1) % nv];
                verts[wi++] = new GlVertex(sx[a], sy[a], sz[a], er, eg, eb);
                verts[wi++] = new GlVertex(sx[b], sy[b], sz[b], er, eg, eb);
            }
        }
        UploadBuffer(ref _edgeVbo, ref _edgeVboSize, GlBindings.GL_ARRAY_BUFFER, verts);
        _edgeVertexCount = verts.Length;
    }

    /// <summary>
    /// Upload feature edges and iso-contour line segments. <paramref name="segments"/>
    /// is laid out as 6 floats per segment (ax,ay,az, bx,by,bz). If
    /// <paramref name="perSegmentColors"/> is null, <paramref name="defaultColor"/>
    /// is used for every segment.
    /// </summary>
    public void UploadSegments(ReadOnlySpan<float> segments, uint[]? perSegmentColors, uint defaultColor)
    {
        int nSegs = segments.Length / 6;
        if (nSegs == 0) { _segVertexCount = 0; return; }

        var verts = new GlVertex[nSegs * 2];
        for (int i = 0; i < nSegs; i++)
        {
            uint c = (perSegmentColors != null && i < perSegmentColors.Length) ? perSegmentColors[i] : defaultColor;
            byte r = (byte)((c >> 16) & 0xFF), g = (byte)((c >> 8) & 0xFF), b = (byte)(c & 0xFF);
            int p = i * 6;
            verts[i * 2 + 0] = new GlVertex(segments[p],     segments[p + 1], segments[p + 2], r, g, b);
            verts[i * 2 + 1] = new GlVertex(segments[p + 3], segments[p + 4], segments[p + 5], r, g, b);
        }
        UploadBuffer(ref _segVbo, ref _segVboSize, GlBindings.GL_ARRAY_BUFFER, verts);
        _segVertexCount = verts.Length;
    }

    /// <summary>
    /// Issue draw commands for one frame.  <paramref name="framebufferId"/> is the
    /// Avalonia-provided framebuffer to render into (passed to OnOpenGlRender);
    /// we bind it explicitly because Avalonia's UI compositor may have left a
    /// different framebuffer current after its previous pass.
    /// </summary>
    public void RenderFrame(uint framebufferId, int width, int height, float zNear, float zFar, bool drawFill, Action<string>? log = null)
    {
        _gl.BindFramebuffer(GlBindings.GL_FRAMEBUFFER, framebufferId);
        _gl.Viewport(0, 0, width, height);
        _gl.ClearColor(1, 1, 1, 1);
        _gl.Enable(GlBindings.GL_DEPTH_TEST);
        _gl.DepthFunc(GlBindings.GL_LEQUAL);
        _gl.Disable(GlBindings.GL_BLEND);
        _gl.Disable(GlBindings.GL_CULL_FACE);
        _gl.Clear(GlBindings.GL_COLOR_BUFFER_BIT | GlBindings.GL_DEPTH_BUFFER_BIT);
        var err = _gl.GetError();
        if (err != GlBindings.GL_NO_ERROR) log?.Invoke($"GL error after clear: 0x{err:X}");

        _gl.UseProgram(_program);
        if (_locViewportDepth >= 0)
            _gl.Uniform4f(_locViewportDepth, width, height, zNear, zFar);

        if (drawFill && _meshVbo != 0 && _meshIbo != 0 && _meshIndexCount > 0)
        {
            BindAttribs(_meshVbo);
            _gl.BindBuffer(GlBindings.GL_ELEMENT_ARRAY_BUFFER, _meshIbo);
            _gl.DrawElements(GlBindings.GL_TRIANGLES, _meshIndexCount, GlBindings.GL_UNSIGNED_INT, IntPtr.Zero);
            err = _gl.GetError();
            if (err != GlBindings.GL_NO_ERROR) log?.Invoke($"GL error after DrawElements (mesh, {_meshIndexCount} idx): 0x{err:X}");
        }

        if (_edgeVbo != 0 && _edgeVertexCount > 0)
        {
            BindAttribs(_edgeVbo);
            _gl.DrawArrays(GlBindings.GL_LINES, 0, _edgeVertexCount);
            err = _gl.GetError();
            if (err != GlBindings.GL_NO_ERROR) log?.Invoke($"GL error after DrawArrays (edges, {_edgeVertexCount}): 0x{err:X}");
        }

        if (_segVbo != 0 && _segVertexCount > 0)
        {
            BindAttribs(_segVbo);
            _gl.DrawArrays(GlBindings.GL_LINES, 0, _segVertexCount);
            err = _gl.GetError();
            if (err != GlBindings.GL_NO_ERROR) log?.Invoke($"GL error after DrawArrays (segments, {_segVertexCount}): 0x{err:X}");
        }

        // Be a tidy guest: leave the program / vertex array unbound so the
        // Avalonia compositor's next pass starts from a clean slate.
        if (_locPos >= 0) _gl.DisableVertexAttribArray((uint)_locPos);
        if (_locCol >= 0) _gl.DisableVertexAttribArray((uint)_locCol);
        _gl.UseProgram(0);
        _gl.BindBuffer(GlBindings.GL_ARRAY_BUFFER, 0);
        _gl.BindBuffer(GlBindings.GL_ELEMENT_ARRAY_BUFFER, 0);
    }

    private void BindAttribs(uint vbo)
    {
        _gl.BindBuffer(GlBindings.GL_ARRAY_BUFFER, vbo);
        if (_locPos >= 0)
        {
            _gl.EnableVertexAttribArray((uint)_locPos);
            _gl.VertexAttribPointer((uint)_locPos, 3, GlBindings.GL_FLOAT, 0, GlVertex.SizeInBytes, IntPtr.Zero);
        }
        if (_locCol >= 0)
        {
            _gl.EnableVertexAttribArray((uint)_locCol);
            _gl.VertexAttribPointer((uint)_locCol, 4, GlBindings.GL_UNSIGNED_BYTE, 1, GlVertex.SizeInBytes, (IntPtr)12);
        }
    }

    public void Dispose()
    {
        DeleteBuffer(ref _meshVbo);
        DeleteBuffer(ref _meshIbo);
        DeleteBuffer(ref _edgeVbo);
        DeleteBuffer(ref _segVbo);
        if (_program != 0) _gl.DeleteProgram(_program);
    }

    private void DeleteBuffer(ref uint id)
    {
        if (id == 0) return;
        uint b = id;
        _gl.DeleteBuffers(1, &b);
        id = 0;
    }
}
