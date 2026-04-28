using System.Text;
using SimPlasPost.Core.Models;
using Veldrid;

namespace SimPlasPost.Core.Rendering.Gpu;

/// <summary>
/// Builds a CPU-readable depth buffer for hidden-line removal in vector exports.
/// The Avalonia OpenGL backend cannot create a hidden GL context out of process,
/// so this class expects to be handed a <see cref="VeldridBackend"/> already
/// initialised against an existing context (the live viewport's, in practice).
///
/// Output matches the contract previously provided by <c>ZBufferRenderer.Build</c>:
/// a float[w*h] in row-major order, depth values from the eye-Z space used by
/// <see cref="Rendering.Camera.Project"/>, with float.MaxValue meaning "background".
///
/// The renderer owns its own depth-only pipeline and framebuffer, scoped to the
/// offscreen output description.  It does NOT mutate the backend's main-swapchain
/// pipelines, so the on-screen viewport keeps rendering correctly while a depth
/// readback is in flight.
/// </summary>
public sealed class OffscreenDepthRenderer : IDisposable
{
    private readonly VeldridBackend _be;
    private Texture? _depthTex;
    private Texture? _depthStaging;
    private Texture? _colorTex;       // dummy color attachment (some GL drivers require one)
    private Framebuffer? _fbo;
    private Pipeline? _depthPipeline;
    private DeviceBuffer? _vbo;
    private DeviceBuffer? _ibo;
    private uint _indexCount;
    private uint _w, _h;

    public OffscreenDepthRenderer(VeldridBackend backend)
    {
        _be = backend;
    }

    private void EnsureSize(uint w, uint h)
    {
        if (_fbo != null && w == _w && h == _h) return;
        _fbo?.Dispose();
        _depthTex?.Dispose();
        _depthStaging?.Dispose();
        _colorTex?.Dispose();
        _depthPipeline?.Dispose();

        var f = _be.Factory;
        _depthTex = f.CreateTexture(TextureDescription.Texture2D(
            w, h, mipLevels: 1, arrayLayers: 1,
            PixelFormat.D32_Float, TextureUsage.DepthStencil));
        _depthStaging = f.CreateTexture(TextureDescription.Texture2D(
            w, h, 1, 1, PixelFormat.D32_Float, TextureUsage.Staging));
        _colorTex = f.CreateTexture(TextureDescription.Texture2D(
            w, h, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.RenderTarget));
        _fbo = f.CreateFramebuffer(new FramebufferDescription(_depthTex, _colorTex));
        _depthPipeline = BuildDepthPipeline(_fbo.OutputDescription);
        _w = w; _h = h;
    }

    private Pipeline BuildDepthPipeline(OutputDescription output)
    {
        var f = _be.Factory;
        var vertLayout = new VertexLayoutDescription(
            new VertexElementDescription("in_pos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("in_col", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm));

        var vs = f.CreateShader(new ShaderDescription(
            ShaderStages.Vertex, Encoding.UTF8.GetBytes(Shaders.DepthVert), "main"));
        var fs = f.CreateShader(new ShaderDescription(
            ShaderStages.Fragment, Encoding.UTF8.GetBytes(Shaders.DepthFrag), "main"));

        var pipeline = f.CreateGraphicsPipeline(new GraphicsPipelineDescription(
            BlendStateDescription.SingleDisabled,
            DepthStencilStateDescription.DepthOnlyLessEqual,
            new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: false),
            PrimitiveTopology.TriangleList,
            new ShaderSetDescription(new[] { vertLayout }, new[] { vs, fs }),
            new[] { _be.UniformLayout },
            output));

        vs.Dispose(); fs.Dispose();
        return pipeline;
    }

    private void UploadGeometry(
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, ReadOnlySpan<float> sz,
        int[] faceOffsets, int[] faceVertices)
    {
        int nVerts = sx.Length;
        var verts = new GpuVertex[nVerts];
        for (int i = 0; i < nVerts; i++) verts[i] = new GpuVertex(sx[i], sy[i], sz[i], 0, 0, 0, 0);

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

        uint vbSize = (uint)verts.Length * GpuVertex.SizeInBytes;
        uint ibSize = (uint)indices.Length * sizeof(uint);
        if (_vbo == null || _vbo.SizeInBytes < vbSize)
        { _vbo?.Dispose(); _vbo = _be.Factory.CreateBuffer(new BufferDescription(vbSize, BufferUsage.VertexBuffer)); }
        if (_ibo == null || _ibo.SizeInBytes < ibSize)
        { _ibo?.Dispose(); _ibo = _be.Factory.CreateBuffer(new BufferDescription(ibSize, BufferUsage.IndexBuffer)); }
        _be.Device.UpdateBuffer(_vbo, 0, verts);
        _be.Device.UpdateBuffer(_ibo, 0, indices);
        _indexCount = (uint)indices.Length;
    }

    /// <summary>
    /// Render the given (already-projected to screen pixels) mesh into the
    /// offscreen depth target and return the readback.  Z-near / z-far define
    /// the linear mapping the vertex shader uses.
    /// </summary>
    public float[] RenderDepth(
        int w, int h,
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, ReadOnlySpan<float> sz,
        int[] faceOffsets, int[] faceVertices,
        float zNear, float zFar)
    {
        EnsureSize((uint)w, (uint)h);
        UploadGeometry(sx, sy, sz, faceOffsets, faceVertices);

        _be.UpdateUniforms(w, h, zNear, zFar);

        var cl = _be.Factory.CreateCommandList();
        cl.Begin();
        cl.SetFramebuffer(_fbo!);
        cl.ClearColorTarget(0, RgbaFloat.White);
        cl.ClearDepthStencil(1f);
        if (_indexCount > 0 && _vbo != null && _ibo != null)
        {
            cl.SetPipeline(_depthPipeline!);
            cl.SetGraphicsResourceSet(0, _be.UniformSet);
            cl.SetVertexBuffer(0, _vbo);
            cl.SetIndexBuffer(_ibo, IndexFormat.UInt32);
            cl.DrawIndexed(_indexCount);
        }
        cl.CopyTexture(_depthTex!, _depthStaging!);
        cl.End();
        _be.Device.SubmitCommands(cl);
        _be.Device.WaitForIdle();
        cl.Dispose();

        // Read back depth and remap NDC depth → eye-Z so callers can compare
        // against vertex Z values directly.
        var map = _be.Device.Map(_depthStaging!, MapMode.Read);
        var dst = new float[w * h];
        unsafe
        {
            float* src = (float*)map.Data.ToPointer();
            uint rowPitch = map.RowPitch / sizeof(float);
            float zSpan = zFar - zNear;
            for (int y = 0; y < h; y++)
            {
                int srcRow = (int)(y * rowPitch);
                int dstRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    float d = src[srcRow + x];   // NDC [0,1]
                    // 1.0 == cleared / background — surface "at infinity"
                    dst[dstRow + x] = d >= 0.999999f ? float.MaxValue : zNear + d * zSpan;
                }
            }
        }
        _be.Device.Unmap(_depthStaging!);
        return dst;
    }

    /// <summary>
    /// Sample-based occlusion test, identical contract to the previous
    /// <c>ZBufferRenderer.IsSegmentVisible</c>: compares the segment's eye-Z
    /// against the depth readback at five sample points along the segment.
    /// </summary>
    public static bool IsSegmentVisible(double[] a, double[] b, float[] zbuf, int w, int h)
    {
        const int N = 5;
        int vis = 0;
        for (int i = 0; i < N; i++)
        {
            double t = (i + 0.5) / N;
            int x = (int)Math.Round(a[0] + t * (b[0] - a[0]));
            int y = (int)Math.Round(a[1] + t * (b[1] - a[1]));
            double z = a[2] + t * (b[2] - a[2]);
            if (x < 0 || x >= w || y < 0 || y >= h) continue;
            if (z <= zbuf[y * w + x] + 0.015) vis++;
        }
        return vis >= (N + 1) / 2;
    }

    public void Dispose()
    {
        _vbo?.Dispose();
        _ibo?.Dispose();
        _depthPipeline?.Dispose();
        _fbo?.Dispose();
        _depthTex?.Dispose();
        _depthStaging?.Dispose();
        _colorTex?.Dispose();
    }
}
