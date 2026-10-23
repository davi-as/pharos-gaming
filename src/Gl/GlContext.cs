using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace PharosGaming;

/// <summary>
/// Contexto OpenGL sem janela (EGL), para os testes de GPU renderizarem fora da tela, sem V-Sync nem compositor no meio.
/// Varre os dispositivos EGL (EGL_EXT_platform_device) e fica com a primeira GPU de verdade: o dispositivo padrão do
/// Mesa costuma ser o llvmpipe (software), inútil para benchmark. Use só na thread que criou o contexto.
/// </summary>
public sealed class GlContext : IDisposable
{
    private const string Egl = "libEGL.so.1";
    private const int EGL_OPENGL_API = 0x30A2, EGL_PLATFORM_DEVICE = 0x313F, EGL_NONE = 0x3038;
    private const int EGL_CONTEXT_MAJOR = 0x3098, EGL_CONTEXT_MINOR = 0x30FB, EGL_PROFILE_MASK = 0x30FD, EGL_CORE_BIT = 1;

    [DllImport(Egl)] private static extern IntPtr eglGetProcAddress(string name);
    [DllImport(Egl)] private static extern bool eglInitialize(IntPtr dpy, out int major, out int minor);
    [DllImport(Egl)] private static extern bool eglTerminate(IntPtr dpy);
    [DllImport(Egl)] private static extern bool eglBindAPI(int api);
    [DllImport(Egl)] private static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int[] attribs);
    [DllImport(Egl)] private static extern bool eglDestroyContext(IntPtr dpy, IntPtr ctx);
    [DllImport(Egl)] private static extern bool eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);

    private delegate bool QueryDevicesFn(int max, IntPtr[] devices, out int count);
    private delegate IntPtr GetPlatformDisplayFn(int platform, IntPtr native, int[]? attribs);

    public GL Gl { get; }
    public string Renderer { get; }
    /// <summary>glClipControl disponível (profundidade 0..1, como as matrizes do System.Numerics).</summary>
    public bool ClipControl { get; }

    private readonly IntPtr _dpy, _ctx;

    private GlContext(IntPtr dpy, IntPtr ctx, GL gl, string renderer)
    {
        _dpy = dpy; _ctx = ctx; Gl = gl; Renderer = renderer;
        ClipControl = gl.IsExtensionPresent("GL_ARB_clip_control") || gl.GetInteger(GetPName.MajorVersion) * 10 + gl.GetInteger(GetPName.MinorVersion) >= 45;
    }

    public static bool IsSoftware(string renderer) =>
        renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || renderer.Contains("softpipe", StringComparison.OrdinalIgnoreCase)
        || renderer.Contains("lavapipe", StringComparison.OrdinalIgnoreCase) || renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cria o contexto na primeira GPU com aceleração (de preferência a que tem <paramref name="prefer"/> no nome).</summary>
    public static GlContext Create(string prefer = "")
    {
        IntPtr q, g;
        try { q = eglGetProcAddress("eglQueryDevicesEXT"); g = eglGetProcAddress("eglGetPlatformDisplayEXT"); }
        catch (DllNotFoundException) { throw new InvalidOperationException("libEGL não encontrada: instale o driver de vídeo com suporte a EGL (mesa ou nvidia-utils)."); }
        if (q == IntPtr.Zero || g == IntPtr.Zero) throw new InvalidOperationException("O EGL deste sistema não lista dispositivos (EGL_EXT_platform_device).");
        var query = Marshal.GetDelegateForFunctionPointer<QueryDevicesFn>(q);
        var platform = Marshal.GetDelegateForFunctionPointer<GetPlatformDisplayFn>(g);
        var devices = new IntPtr[16];
        if (!query(devices.Length, devices, out int count)) count = 0;

        GlContext? fallback = null;
        for (int i = 0; i < count; i++)
        {
            var ctx = TryDevice(platform(EGL_PLATFORM_DEVICE, devices[i], null));
            if (ctx == null) continue;
            if (IsSoftware(ctx.Renderer)) { ctx.Dispose(); continue; }
            if (prefer.Length == 0 || PrefersName(ctx.Renderer, prefer)) { fallback?.Dispose(); return ctx; }
            if (fallback == null) { fallback = ctx; eglMakeCurrent(ctx._dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
            else ctx.Dispose();
        }
        if (fallback != null) { eglMakeCurrent(fallback._dpy, IntPtr.Zero, IntPtr.Zero, fallback._ctx); return fallback; }
        throw new InvalidOperationException("Nenhuma GPU com aceleração OpenGL 4.3 encontrada (só renderização por software).");
    }

    /// <summary>"NVIDIA GeForce RTX 3060" combina com "NVIDIA GeForce RTX 3060/PCIe/SSE2"; "AMD Radeon RX 7800 XT" com "AMD Radeon RX 7800 XT (radeonsi...)".</summary>
    private static bool PrefersName(string renderer, string prefer)
    {
        string core = prefer.Replace("NVIDIA ", "").Replace("AMD ", "").Replace("Intel ", "").Trim();
        return renderer.Contains(core, StringComparison.OrdinalIgnoreCase);
    }

    private static GlContext? TryDevice(IntPtr dpy)
    {
        if (dpy == IntPtr.Zero || !eglInitialize(dpy, out _, out _)) return null;
        if (!eglBindAPI(EGL_OPENGL_API)) { eglTerminate(dpy); return null; }
        foreach (var (major, minor) in new[] { (4, 6), (4, 5), (4, 3) })
        {
            // sem config (EGL_KHR_no_config_context) e sem superfície (EGL_KHR_surfaceless_context): só render em FBO
            var ctx = eglCreateContext(dpy, IntPtr.Zero, IntPtr.Zero,
                new[] { EGL_CONTEXT_MAJOR, major, EGL_CONTEXT_MINOR, minor, EGL_PROFILE_MASK, EGL_CORE_BIT, EGL_NONE });
            if (ctx == IntPtr.Zero) continue;
            if (!eglMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero, ctx)) { eglDestroyContext(dpy, ctx); continue; }
            var gl = GL.GetApi(eglGetProcAddress);
            return new GlContext(dpy, ctx, gl, gl.GetStringS(StringName.Renderer) ?? "GPU");
        }
        eglTerminate(dpy);
        return null;
    }

    public void Dispose()
    {
        eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        eglDestroyContext(_dpy, _ctx);
        Gl.Dispose();
    }
}
