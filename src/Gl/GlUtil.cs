using System.Numerics;
using Silk.NET.OpenGL;

namespace PharosGaming;

/// <summary>Compilação de shaders e uniforms comuns aos testes de GPU.</summary>
public static class GlUtil
{
    public static unsafe uint Program(GL gl, params (ShaderType Type, string Source)[] stages)
    {
        uint prog = gl.CreateProgram();
        var shaders = new List<uint>();
        foreach (var (type, src) in stages)
        {
            uint s = gl.CreateShader(type);
            // sem tamanho = texto terminado em nulo: o overload com string conta caracteres, não bytes, e corta fontes com acento
            gl.ShaderSource(s, 1, new[] { src }, (int*)null);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"Shader ({type}) não compilou: {gl.GetShaderInfoLog(s)}");
            gl.AttachShader(prog, s);
            shaders.Add(s);
        }
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException("Programa não linkou: " + gl.GetProgramInfoLog(prog));
        foreach (var s in shaders) { gl.DetachShader(prog, s); gl.DeleteShader(s); }
        return prog;
    }

    /// <summary>Matriz do System.Numerics (vetor-linha, v*M) enviada crua: no GLSL vale "M * v" com o mesmo resultado.</summary>
    public static unsafe void Mat(GL gl, uint prog, string name, Matrix4x4 m) =>
        gl.UniformMatrix4(gl.GetUniformLocation(prog, name), 1, false, (float*)&m);

    public static void Vec3(GL gl, uint prog, string name, Vector3 v) => gl.Uniform3(gl.GetUniformLocation(prog, name), v.X, v.Y, v.Z);
    public static void F(GL gl, uint prog, string name, float v) => gl.Uniform1(gl.GetUniformLocation(prog, name), v);
    public static void I(GL gl, uint prog, string name, int v) => gl.Uniform1(gl.GetUniformLocation(prog, name), v);

    /// <summary>Cor RGBA8 (ou HDR) + profundidade opcional num FBO.</summary>
    public static (uint Fbo, uint Color, uint Depth) Target(GL gl, int w, int h, InternalFormat color, bool depth)
    {
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexStorage2D(TextureTarget.Texture2D, 1, (SizedInternalFormat)color, (uint)w, (uint)h);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        uint rb = 0;
        if (depth)
        {
            rb = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent32f, (uint)w, (uint)h);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, rb);
        }
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("Framebuffer incompleto (formato não suportado pela GPU).");
        return (fbo, tex, rb);
    }
}

/// <summary>
/// Prévia do render para a interface: a cada chamada copia o quadro (reduzido) para um PBO e lê o PBO da vez anterior,
/// então a GPU nunca espera pela CPU (sem engasgo no frametime medido).
/// </summary>
public sealed class GlPreview
{
    public const int W = 480, H = 270;
    private readonly GL _gl;
    private readonly uint _fbo, _tex;
    private readonly uint[] _pbo = new uint[2];
    private int _turn;
    private bool _primed;
    /// <summary>Último quadro, RGBA8, de baixo para cima (como o OpenGL lê).</summary>
    public byte[] Pixels { get; } = new byte[W * H * 4];
    public long Version;

    public unsafe GlPreview(GL gl)
    {
        _gl = gl;
        (_fbo, _tex, _) = GlUtil.Target(gl, W, H, InternalFormat.Rgba8, depth: false);
        gl.GenBuffers(2, _pbo);
        foreach (var b in _pbo)
        {
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, b);
            gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)(W * H * 4), null, BufferUsageARB.StreamRead);
        }
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
    }

    public unsafe void Capture(uint srcFbo, int srcW, int srcH)
    {
        var gl = _gl;
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, srcFbo);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);
        gl.BlitFramebuffer(0, 0, srcW, srcH, 0, 0, W, H, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pbo[_turn]);
        gl.ReadPixels(0, 0, W, H, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _turn ^= 1;
        if (_primed)
        {
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pbo[_turn]);
            void* p = gl.MapBuffer(BufferTargetARB.PixelPackBuffer, BufferAccessARB.ReadOnly);
            if (p != null)
            {
                lock (Pixels) new Span<byte>(p, Pixels.Length).CopyTo(Pixels);
                Interlocked.Increment(ref Version);
            }
            gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
        }
        _primed = true;
        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
    }
}
