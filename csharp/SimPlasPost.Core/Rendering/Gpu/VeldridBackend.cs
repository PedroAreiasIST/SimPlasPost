using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Veldrid;
using Veldrid.OpenGL;

namespace SimPlasPost.Core.Rendering.Gpu;

/// <summary>
/// Per-vertex layout used by every pipeline:
/// 12 bytes position (eye-space x,y,z in screen pixels for x/y, eye-Z for z) +
/// 4 bytes RGBA color (linear, 0..255).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GpuVertex
{
    public float X, Y, Z;
    public byte R, G, B, A;

    public const uint SizeInBytes = 16;

    public GpuVertex(float x, float y, float z, byte r, byte g, byte b, byte a = 255)
    { X = x; Y = y; Z = z; R = r; G = g; B = b; A = a; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ViewportUniforms
{
    public Vector4 ViewportDepth; // x=w, y=h, z=zNear, w=zFar (16 bytes — std140 minimum block)
}

/// <summary>
/// Owns a Veldrid <see cref="GraphicsDevice"/> plus the small set of pipelines
/// that the SimPlasPost on-screen renderer needs:
///   • <see cref="TriPipeline"/>  — filled triangles, depth-test on, no cull, smooth shading
///   • <see cref="LinePipeline"/> — colored lines, depth-test on
///
/// (The offscreen depth-only pipeline used by <see cref="OffscreenDepthRenderer"/>
/// is owned locally there because it must match a different framebuffer's
/// output description.)
///
/// Construct via <see cref="CreateOpenGL"/> from an Avalonia OpenGlControlBase.
/// </summary>
public sealed class VeldridBackend : IDisposable
{
    public GraphicsDevice Device { get; }
    public ResourceFactory Factory => Device.ResourceFactory;
    public ResourceLayout UniformLayout { get; }
    public DeviceBuffer UniformBuffer { get; }
    public ResourceSet UniformSet { get; }

    public Pipeline TriPipeline { get; private set; } = null!;
    public Pipeline LinePipeline { get; private set; } = null!;

    /// <summary>
    /// Header text prepended to every GLSL shader source before compilation.
    /// Set by the factory method to match the active GL flavor (desktop GL
    /// 3.3 core vs OpenGL ES 3.0). <see cref="OffscreenDepthRenderer"/>
    /// reuses the same header so its depth-only pipeline matches.
    /// </summary>
    public string ShaderHeader { get; private set; } = Shaders.HeaderDesktop;

    private VeldridBackend(GraphicsDevice device, string shaderHeader)
    {
        Device = device;
        ShaderHeader = shaderHeader;

        UniformLayout = Factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Uniforms", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

        UniformBuffer = Factory.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<ViewportUniforms>(),
            BufferUsage.UniformBuffer | BufferUsage.Dynamic));

        UniformSet = Factory.CreateResourceSet(new ResourceSetDescription(UniformLayout, UniformBuffer));
    }

    /// <summary>
    /// Build pipelines for a target with the given output description. Pipelines
    /// must match the framebuffer they render into (color + depth formats), so
    /// they are rebuilt whenever the swapchain is recreated at a new size or
    /// when an offscreen target is allocated.
    /// </summary>
    public void BuildPipelines(OutputDescription output)
    {
        TriPipeline?.Dispose();
        LinePipeline?.Dispose();

        var vertLayout = new VertexLayoutDescription(
            new VertexElementDescription("in_pos", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("in_col", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm));

        Shader[] meshShaders = LoadShaders(Shaders.MeshVert, Shaders.MeshFrag);
        Shader[] lineShaders = LoadShaders(Shaders.LineVert, Shaders.LineFrag);

        var rasterizer = new RasterizerStateDescription(
            cullMode: FaceCullMode.None,
            fillMode: PolygonFillMode.Solid,
            frontFace: FrontFace.CounterClockwise,
            depthClipEnabled: true,
            scissorTestEnabled: false);

        var depthLessEqual = DepthStencilStateDescription.DepthOnlyLessEqual;

        TriPipeline = Factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
            BlendStateDescription.SingleOverrideBlend,
            depthLessEqual,
            rasterizer,
            PrimitiveTopology.TriangleList,
            new ShaderSetDescription(new[] { vertLayout }, meshShaders),
            new[] { UniformLayout },
            output));

        LinePipeline = Factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
            BlendStateDescription.SingleOverrideBlend,
            depthLessEqual,
            rasterizer,
            PrimitiveTopology.LineList,
            new ShaderSetDescription(new[] { vertLayout }, lineShaders),
            new[] { UniformLayout },
            output));

        foreach (var s in meshShaders) s.Dispose();
        foreach (var s in lineShaders) s.Dispose();
    }

    private Shader[] LoadShaders(string vert, string frag)
    {
        // OpenGL/GLES backends accept raw GLSL bytes; no SPIR-V cross-compilation needed.
        var vs = Factory.CreateShader(new ShaderDescription(
            ShaderStages.Vertex, Encoding.UTF8.GetBytes(ShaderHeader + vert), "main"));
        var fs = Factory.CreateShader(new ShaderDescription(
            ShaderStages.Fragment, Encoding.UTF8.GetBytes(ShaderHeader + frag), "main"));
        return new[] { vs, fs };
    }

    public void UpdateUniforms(int width, int height, float zNear, float zFar)
    {
        var u = new ViewportUniforms
        {
            ViewportDepth = new Vector4(width, height, zNear, zFar),
        };
        Device.UpdateBuffer(UniformBuffer, 0, ref u);
    }

    /// <summary>
    /// Build a Veldrid device on top of an existing GL context owned by Avalonia.
    /// <paramref name="useGles"/> selects desktop OpenGL 3.3 vs OpenGL ES 3.0;
    /// the matching shader header is wired up automatically.  On Linux Avalonia
    /// almost always provides an EGL/GLES context, so callers should detect
    /// the GL flavor (e.g. <c>gl.Version</c>) and pass <c>true</c> there.
    /// </summary>
    public static VeldridBackend CreateOpenGL(
        Func<string, IntPtr> getProcAddress,
        Action<IntPtr> makeCurrent,
        Func<IntPtr> getCurrentContext,
        Action clearCurrentContext,
        Action<IntPtr> deleteContext,
        Action swapBuffers,
        Action<bool> setSyncToVerticalBlank,
        IntPtr contextHandle,
        uint width, uint height,
        bool useGles)
    {
        var options = new GraphicsDeviceOptions(
            debug: false,
            swapchainDepthFormat: PixelFormat.D24_UNorm_S8_UInt,
            syncToVerticalBlank: false,
            resourceBindingModel: ResourceBindingModel.Improved,
            preferDepthRangeZeroToOne: false,
            preferStandardClipSpaceYDirection: false);

        var platformInfo = new OpenGLPlatformInfo(
            contextHandle,
            getProcAddress,
            makeCurrent,
            getCurrentContext,
            clearCurrentContext,
            deleteContext,
            swapBuffers,
            setSyncToVerticalBlank);

        var gd = useGles
            ? GraphicsDevice.CreateOpenGLES(options, platformInfo, width, height)
            : GraphicsDevice.CreateOpenGL(options, platformInfo, width, height);

        var header = useGles ? Shaders.HeaderGles : Shaders.HeaderDesktop;
        var be = new VeldridBackend(gd, header);
        be.BuildPipelines(gd.MainSwapchain.Framebuffer.OutputDescription);
        return be;
    }

    public void Dispose()
    {
        TriPipeline?.Dispose();
        LinePipeline?.Dispose();
        UniformSet?.Dispose();
        UniformBuffer?.Dispose();
        UniformLayout?.Dispose();
        Device?.Dispose();
    }
}
