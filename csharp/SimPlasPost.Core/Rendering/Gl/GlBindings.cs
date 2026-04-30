using System.Runtime.InteropServices;
using System.Text;

namespace SimPlasPost.Core.Rendering.Gl;

/// <summary>
/// Raw GL bindings loaded from <see cref="GlInterface.GetProcAddress"/>. We
/// use this instead of the typed methods on <c>GlInterface</c> directly so
/// the renderer doesn't depend on which subset of GL the host's <c>GlInterface</c>
/// happens to expose — every entry point we need is resolved by name and
/// invoked through a delegate.
///
/// The renderer runs entirely on the UI thread inside <c>OnOpenGlRender</c>,
/// so the GL context is always current when we call any of these.
/// </summary>
public sealed unsafe class GlBindings
{
    // Constants we use from <GL/gl.h>.
    public const uint GL_VERTEX_SHADER = 0x8B31;
    public const uint GL_FRAGMENT_SHADER = 0x8B30;
    public const uint GL_COMPILE_STATUS = 0x8B81;
    public const uint GL_LINK_STATUS = 0x8B82;
    public const uint GL_INFO_LOG_LENGTH = 0x8B84;

    public const uint GL_ARRAY_BUFFER = 0x8892;
    public const uint GL_ELEMENT_ARRAY_BUFFER = 0x8893;
    public const uint GL_STATIC_DRAW = 0x88E4;
    public const uint GL_DYNAMIC_DRAW = 0x88E8;

    public const uint GL_FLOAT = 0x1406;
    public const uint GL_UNSIGNED_INT = 0x1405;
    public const uint GL_UNSIGNED_BYTE = 0x1401;

    public const uint GL_TRIANGLES = 0x0004;
    public const uint GL_LINES = 0x0001;

    public const uint GL_DEPTH_TEST = 0x0B71;
    public const uint GL_BLEND = 0x0BE2;
    public const uint GL_CULL_FACE = 0x0B44;
    public const uint GL_LEQUAL = 0x0203;

    public const uint GL_COLOR_BUFFER_BIT = 0x4000;
    public const uint GL_DEPTH_BUFFER_BIT = 0x0100;

    public const uint GL_NO_ERROR = 0x0000;

    public const uint GL_FRAMEBUFFER = 0x8D40;

    // Delegate signatures (UnmanagedFunctionPointer = Cdecl by default for OpenGL on POSIX).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint GlCreateShader(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlShaderSource(uint shader, int count, IntPtr* strings, int* lengths);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlCompileShader(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlGetShaderiv(uint shader, uint pname, int* @params);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlGetShaderInfoLog(uint shader, int bufSize, int* length, byte* infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDeleteShader(uint shader);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint GlCreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlAttachShader(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlLinkProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlGetProgramiv(uint program, uint pname, int* @params);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlGetProgramInfoLog(uint program, int bufSize, int* length, byte* infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlUseProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDeleteProgram(uint program);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GlGetUniformLocation(uint program, byte* name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlUniform4f(int location, float x, float y, float z, float w);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GlGetAttribLocation(uint program, byte* name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlBindAttribLocation(uint program, uint index, byte* name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlGenBuffers(int n, uint* buffers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDeleteBuffers(int n, uint* buffers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlBindBuffer(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlBufferData(uint target, IntPtr size, IntPtr data, uint usage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, IntPtr offset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlEnableVertexAttribArray(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDisableVertexAttribArray(uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDrawArrays(uint mode, int first, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDrawElements(uint mode, int count, uint type, IntPtr indices);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlClearColor(float r, float g, float b, float a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlClear(uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlViewport(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlEnable(uint cap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDisable(uint cap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlDepthFunc(uint func);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint GlGetError();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr GlGetString(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GlBindFramebuffer(uint target, uint framebuffer);

    public GlCreateShader CreateShader = null!;
    public GlShaderSource ShaderSource = null!;
    public GlCompileShader CompileShader = null!;
    public GlGetShaderiv GetShaderiv = null!;
    public GlGetShaderInfoLog GetShaderInfoLog = null!;
    public GlDeleteShader DeleteShader = null!;

    public GlCreateProgram CreateProgram = null!;
    public GlAttachShader AttachShader = null!;
    public GlLinkProgram LinkProgram = null!;
    public GlGetProgramiv GetProgramiv = null!;
    public GlGetProgramInfoLog GetProgramInfoLog = null!;
    public GlUseProgram UseProgram = null!;
    public GlDeleteProgram DeleteProgram = null!;

    public GlGetUniformLocation GetUniformLocation = null!;
    public GlUniform4f Uniform4f = null!;
    public GlGetAttribLocation GetAttribLocation = null!;
    public GlBindAttribLocation BindAttribLocation = null!;

    public GlGenBuffers GenBuffers = null!;
    public GlDeleteBuffers DeleteBuffers = null!;
    public GlBindBuffer BindBuffer = null!;
    public GlBufferData BufferData = null!;

    public GlVertexAttribPointer VertexAttribPointer = null!;
    public GlEnableVertexAttribArray EnableVertexAttribArray = null!;
    public GlDisableVertexAttribArray DisableVertexAttribArray = null!;

    public GlDrawArrays DrawArrays = null!;
    public GlDrawElements DrawElements = null!;

    public GlClearColor ClearColor = null!;
    public GlClear Clear = null!;
    public GlViewport Viewport = null!;
    public GlEnable Enable = null!;
    public GlDisable Disable = null!;
    public GlDepthFunc DepthFunc = null!;
    public GlGetError GetError = null!;
    public GlGetString GetString = null!;
    public GlBindFramebuffer BindFramebuffer = null!;

    public string GlVersion { get; private set; } = "?";
    public bool IsGles { get; private set; }

    public static GlBindings Load(Func<string, IntPtr> getProcAddress)
    {
        var b = new GlBindings();
        T Get<T>(string name) where T : Delegate
        {
            var p = getProcAddress(name);
            if (p == IntPtr.Zero)
                throw new InvalidOperationException($"GL function not found: {name}");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        b.CreateShader = Get<GlCreateShader>("glCreateShader");
        b.ShaderSource = Get<GlShaderSource>("glShaderSource");
        b.CompileShader = Get<GlCompileShader>("glCompileShader");
        b.GetShaderiv = Get<GlGetShaderiv>("glGetShaderiv");
        b.GetShaderInfoLog = Get<GlGetShaderInfoLog>("glGetShaderInfoLog");
        b.DeleteShader = Get<GlDeleteShader>("glDeleteShader");

        b.CreateProgram = Get<GlCreateProgram>("glCreateProgram");
        b.AttachShader = Get<GlAttachShader>("glAttachShader");
        b.LinkProgram = Get<GlLinkProgram>("glLinkProgram");
        b.GetProgramiv = Get<GlGetProgramiv>("glGetProgramiv");
        b.GetProgramInfoLog = Get<GlGetProgramInfoLog>("glGetProgramInfoLog");
        b.UseProgram = Get<GlUseProgram>("glUseProgram");
        b.DeleteProgram = Get<GlDeleteProgram>("glDeleteProgram");

        b.GetUniformLocation = Get<GlGetUniformLocation>("glGetUniformLocation");
        b.Uniform4f = Get<GlUniform4f>("glUniform4f");
        b.GetAttribLocation = Get<GlGetAttribLocation>("glGetAttribLocation");
        b.BindAttribLocation = Get<GlBindAttribLocation>("glBindAttribLocation");

        b.GenBuffers = Get<GlGenBuffers>("glGenBuffers");
        b.DeleteBuffers = Get<GlDeleteBuffers>("glDeleteBuffers");
        b.BindBuffer = Get<GlBindBuffer>("glBindBuffer");
        b.BufferData = Get<GlBufferData>("glBufferData");

        b.VertexAttribPointer = Get<GlVertexAttribPointer>("glVertexAttribPointer");
        b.EnableVertexAttribArray = Get<GlEnableVertexAttribArray>("glEnableVertexAttribArray");
        b.DisableVertexAttribArray = Get<GlDisableVertexAttribArray>("glDisableVertexAttribArray");

        b.DrawArrays = Get<GlDrawArrays>("glDrawArrays");
        b.DrawElements = Get<GlDrawElements>("glDrawElements");

        b.ClearColor = Get<GlClearColor>("glClearColor");
        b.Clear = Get<GlClear>("glClear");
        b.Viewport = Get<GlViewport>("glViewport");
        b.Enable = Get<GlEnable>("glEnable");
        b.Disable = Get<GlDisable>("glDisable");
        b.DepthFunc = Get<GlDepthFunc>("glDepthFunc");
        b.GetError = Get<GlGetError>("glGetError");
        b.GetString = Get<GlGetString>("glGetString");
        b.BindFramebuffer = Get<GlBindFramebuffer>("glBindFramebuffer");

        var ver = b.GetString(0x1F02 /* GL_VERSION */);
        b.GlVersion = ver != IntPtr.Zero ? (Marshal.PtrToStringAnsi(ver) ?? "?") : "?";
        b.IsGles = b.GlVersion.IndexOf("OpenGL ES", StringComparison.OrdinalIgnoreCase) >= 0;
        return b;
    }

    public string GetShaderInfoLogString(uint shader)
    {
        int len = 0;
        GetShaderiv(shader, GL_INFO_LOG_LENGTH, &len);
        if (len <= 0) return string.Empty;
        var buf = new byte[len];
        fixed (byte* p = buf) GetShaderInfoLog(shader, len, null, p);
        return Encoding.UTF8.GetString(buf, 0, Math.Max(0, len - 1));
    }

    public string GetProgramInfoLogString(uint program)
    {
        int len = 0;
        GetProgramiv(program, GL_INFO_LOG_LENGTH, &len);
        if (len <= 0) return string.Empty;
        var buf = new byte[len];
        fixed (byte* p = buf) GetProgramInfoLog(program, len, null, p);
        return Encoding.UTF8.GetString(buf, 0, Math.Max(0, len - 1));
    }
}
