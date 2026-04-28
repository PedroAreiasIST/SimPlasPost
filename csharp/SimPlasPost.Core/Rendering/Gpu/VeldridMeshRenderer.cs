using Veldrid;

namespace SimPlasPost.Core.Rendering.Gpu;

/// <summary>
/// GPU-resident mesh state plus draw helpers. Owns vertex/index buffers for
/// the filled mesh, edge buffer, and segment buffer (feature edges + iso-contours).
///
/// All position values are in screen pixels (origin top-left, y down) plus an
/// eye-space depth coordinate. The host control is responsible for projecting
/// world vertices → screen pixels each frame.
/// </summary>
public sealed class VeldridMeshRenderer : IDisposable
{
    private readonly VeldridBackend _be;

    private DeviceBuffer? _meshVbo;
    private DeviceBuffer? _meshIbo;
    private uint _meshIndexCount;

    private DeviceBuffer? _edgeVbo;
    private uint _edgeVertexCount;

    private DeviceBuffer? _segVbo;
    private uint _segVertexCount;

    public VeldridMeshRenderer(VeldridBackend backend)
    {
        _be = backend;
    }

    /// <summary>
    /// Upload triangulated mesh faces (already projected to screen) plus their
    /// per-vertex colors. Faces are fan-triangulated.
    /// </summary>
    public void UploadMesh(
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, ReadOnlySpan<float> sz,
        ReadOnlySpan<byte> vR, ReadOnlySpan<byte> vG, ReadOnlySpan<byte> vB,
        int[] faceOffsets, int[] faceVertices)
    {
        int nVerts = sx.Length;
        var verts = new GpuVertex[nVerts];
        for (int i = 0; i < nVerts; i++)
            verts[i] = new GpuVertex(sx[i], sy[i], sz[i], vR[i], vG[i], vB[i]);

        // Fan-triangulate every face.
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

        EnsureBuffer(ref _meshVbo, (uint)verts.Length * GpuVertex.SizeInBytes, BufferUsage.VertexBuffer);
        EnsureBuffer(ref _meshIbo, (uint)indices.Length * sizeof(uint), BufferUsage.IndexBuffer);
        _be.Device.UpdateBuffer(_meshVbo!, 0, verts);
        _be.Device.UpdateBuffer(_meshIbo!, 0, indices);
        _meshIndexCount = (uint)indices.Length;
    }

    /// <summary>
    /// Upload face-edge geometry as a flat line list. Coordinates and color
    /// are in screen pixels / per-vertex bytes (use the same projection as <see cref="UploadMesh"/>).
    /// </summary>
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

        var verts = new GpuVertex[segCount * 2];
        int wi = 0;
        for (int f = 0; f < nFaces; f++)
        {
            if (!drawEdges[f]) continue;
            int s = faceOffsets[f], e = faceOffsets[f + 1], nv = e - s;
            if (nv < 2) continue;
            for (int j = 0; j < nv; j++)
            {
                int a = faceVertices[s + j], b = faceVertices[s + (j + 1) % nv];
                verts[wi++] = new GpuVertex(sx[a], sy[a], sz[a], er, eg, eb);
                verts[wi++] = new GpuVertex(sx[b], sy[b], sz[b], er, eg, eb);
            }
        }
        EnsureBuffer(ref _edgeVbo, (uint)verts.Length * GpuVertex.SizeInBytes, BufferUsage.VertexBuffer);
        _be.Device.UpdateBuffer(_edgeVbo!, 0, verts);
        _edgeVertexCount = (uint)verts.Length;
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

        var verts = new GpuVertex[nSegs * 2];
        for (int i = 0; i < nSegs; i++)
        {
            uint c = (perSegmentColors != null && i < perSegmentColors.Length) ? perSegmentColors[i] : defaultColor;
            byte r = (byte)((c >> 16) & 0xFF), g = (byte)((c >> 8) & 0xFF), b = (byte)(c & 0xFF);
            int p = i * 6;
            verts[i * 2 + 0] = new GpuVertex(segments[p],     segments[p + 1], segments[p + 2], r, g, b);
            verts[i * 2 + 1] = new GpuVertex(segments[p + 3], segments[p + 4], segments[p + 5], r, g, b);
        }
        EnsureBuffer(ref _segVbo, (uint)verts.Length * GpuVertex.SizeInBytes, BufferUsage.VertexBuffer);
        _be.Device.UpdateBuffer(_segVbo!, 0, verts);
        _segVertexCount = (uint)verts.Length;
    }

    /// <summary>
    /// Issue draw commands for a full frame: clear color + depth, draw filled
    /// mesh, then edges, then segments.  Uniform must be updated by caller via
    /// <see cref="VeldridBackend.UpdateUniforms"/>.
    /// </summary>
    public void RenderFrame(CommandList cl, Framebuffer target, RgbaFloat clearColor, bool drawFill = true)
    {
        cl.SetFramebuffer(target);
        cl.ClearColorTarget(0, clearColor);
        cl.ClearDepthStencil(1f);

        if (drawFill && _meshVbo != null && _meshIbo != null && _meshIndexCount > 0)
        {
            cl.SetPipeline(_be.TriPipeline);
            cl.SetGraphicsResourceSet(0, _be.UniformSet);
            cl.SetVertexBuffer(0, _meshVbo);
            cl.SetIndexBuffer(_meshIbo, IndexFormat.UInt32);
            cl.DrawIndexed(_meshIndexCount);
        }

        if (_edgeVbo != null && _edgeVertexCount > 0)
        {
            cl.SetPipeline(_be.LinePipeline);
            cl.SetGraphicsResourceSet(0, _be.UniformSet);
            cl.SetVertexBuffer(0, _edgeVbo);
            cl.Draw(_edgeVertexCount);
        }

        if (_segVbo != null && _segVertexCount > 0)
        {
            cl.SetPipeline(_be.LinePipeline);
            cl.SetGraphicsResourceSet(0, _be.UniformSet);
            cl.SetVertexBuffer(0, _segVbo);
            cl.Draw(_segVertexCount);
        }
    }

    private void EnsureBuffer(ref DeviceBuffer? buf, uint size, BufferUsage usage)
    {
        if (buf == null || buf.SizeInBytes < size)
        {
            buf?.Dispose();
            buf = _be.Factory.CreateBuffer(new BufferDescription(size, usage));
        }
    }

    public void Dispose()
    {
        _meshVbo?.Dispose();
        _meshIbo?.Dispose();
        _edgeVbo?.Dispose();
        _segVbo?.Dispose();
    }
}
