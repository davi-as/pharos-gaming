using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace PharosGaming;

public sealed class GpuBurnSettings
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int Msaa { get; init; } = 1;        // 1 = sem MSAA
    public int Shells { get; init; } = 96;     // camadas de pelo (overdraw)
    public int Iterations { get; init; }       // carga extra de ALU no pixel shader
    public string AdapterName { get; init; } = "";
}

/// <summary>
/// Stress de GPU no estilo FurMark: um torus com pelos renderizado por "shells" (a malha é desenhada N vezes, cada camada
/// inflada ao longo da normal, com pixels descartados para formar os fios). Gera overdraw massivo + shader pesado, sem
/// limite de FPS, num contexto OpenGL fora da tela (a prévia aparece no app).
/// </summary>
public sealed class GpuBurn
{
    private Thread? _thread;
    private volatile bool _stop;
    private long _frames;
    private double _seconds;

    public bool Running => _thread?.IsAlive == true;
    public double Fps { get; private set; }
    public long Frames => Interlocked.Read(ref _frames);
    public double AverageFps => _seconds > 0 ? Frames / _seconds : 0;
    public string? Error { get; private set; }
    public string AdapterUsed { get; private set; } = "";
    public GlPreview? Preview { get; private set; }

    public void Start(GpuBurnSettings s)
    {
        if (Running) return;
        _stop = false; _frames = 0; _seconds = 0; Fps = 0; Error = null;
        _thread = new Thread(() => Run(s)) { IsBackground = true, Name = "GPU burn" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector3 Pos; public Vector3 Normal; public Vector2 Uv; }

    private const string Vs = """
        #version 430 core
        layout(location = 0) in vec3 pos;
        layout(location = 1) in vec3 nrm;
        layout(location = 2) in vec2 uvIn;
        uniform mat4 World, ViewProj;
        uniform float Time, ShellCount, FurLength;
        out vec3 vNrm; out vec2 vUv; out float vShell;
        void main()
        {
            float s = float(gl_InstanceID) / ShellCount;          // 0 = raiz, 1 = ponta
            vec3 p = pos + nrm * (FurLength * s);
            vec3 w = (World * vec4(p, 1)).xyz;
            w.y -= 0.10 * s * s;                                   // gravidade
            w += 0.025 * s * sin(Time * 2.7 + pos.yzx * 7.0);      // vento
            gl_Position = ViewProj * vec4(w, 1);
            vNrm = normalize((World * vec4(nrm, 0)).xyz);
            vUv = uvIn;
            vShell = s;
        }
        """;

    private const string Fs = """
        #version 430 core
        in vec3 vNrm; in vec2 vUv; in float vShell;
        uniform float Iterations;
        out vec4 outColor;
        float hash(vec2 p)
        {
            p = fract(p * vec2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }
        void main()
        {
            float h = hash(floor(vUv * vec2(900, 380)));
            // Carga de ALU: cadeia de FMAs vetoriais (o que mais consome energia na GPU), antes do descarte
            // e ligada a ele, para o compilador não poder pular o cálculo.
            vec4 a = vec4(h, vUv, vShell), b = a.wzyx + 0.5;
            for (int k = 0; k < int(Iterations); k++)
            {
                a = fma(a, vec4(1.0001), b * 0.0003);
                b = fma(b, vec4(0.9997), a * 0.0002);
            }
            float acc = fract(dot(a, b));
            if (vShell > 0.0 && h + acc * 1e-6 < vShell) discard; // fios afinam até a ponta
            vec3 L = normalize(vec3(-0.4, 0.8, -0.5));
            float diff = clamp(dot(vNrm, L), 0.0, 1.0) * 0.75 + 0.25;
            float ao = mix(0.18, 1.0, vShell);                       // raiz escura, ponta clara
            float c = diff * ao * (0.9 + 0.1 * acc);
            outColor = vec4(c, c, c, mix(1.0, 0.55, vShell));       // pontas translúcidas
        }
        """;

    private unsafe void Run(GpuBurnSettings s)
    {
        try
        {
            using var ctx = GlContext.Create(s.AdapterName);
            var gl = ctx.Gl;
            AdapterUsed = ctx.Renderer.Split('/')[0];
            if (ctx.ClipControl) gl.ClipControl(ClipControlOrigin.LowerLeft, ClipControlDepth.ZeroToOne);
            Preview = new GlPreview(gl);
            int w = s.Width, h = s.Height;
            int msaa = Math.Min(s.Msaa, gl.GetInteger((GLEnum)0x8D57) /* GL_MAX_SAMPLES */);

            // Alvo multisample (ou simples) + resolve para a prévia
            uint fbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            uint color = gl.GenRenderbuffer(), depth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)Math.Max(0, msaa > 1 ? msaa : 0), InternalFormat.Rgba8, (uint)w, (uint)h);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, color);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)Math.Max(0, msaa > 1 ? msaa : 0), InternalFormat.DepthComponent32f, (uint)w, (uint)h);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
            var (resolveFbo, _, _) = GlUtil.Target(gl, w, h, InternalFormat.Rgba8, depth: false);

            uint prog = GlUtil.Program(gl, (ShaderType.VertexShader, Vs), (ShaderType.FragmentShader, Fs));
            BuildTorus(out var vertices, out var indices);
            uint vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            uint vb = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vb);
            fixed (Vertex* p = vertices) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(Vertex)), p, BufferUsageARB.StaticDraw);
            uint ib = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ib);
            fixed (uint* p = indices) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * 4), p, BufferUsageARB.StaticDraw);
            uint stride = (uint)sizeof(Vertex);
            gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
            gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)24);

            gl.Enable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            // Blending nas camadas: soma carga de ROP e banda de memória (como a pelagem translúcida do FurMark).
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.UseProgram(prog);
            GlUtil.F(gl, prog, "ShellCount", s.Shells);
            GlUtil.F(gl, prog, "FurLength", 0.2f);
            GlUtil.F(gl, prog, "Iterations", s.Iterations);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, w / (float)h, 0.1f, 50f);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -3.5f), Vector3.Zero, Vector3.UnitY);
            GlUtil.Mat(gl, prog, "ViewProj", view * proj);

            // Até 6 frames na fila (fences): a GPU nunca fica ociosa esperando a CPU.
            const int inFlight = 6;
            var fences = new Queue<IntPtr>();
            var clock = Stopwatch.StartNew();
            double lastT = 0, lastPreview = 0;
            long lastFrames = 0;
            while (!_stop)
            {
                if (fences.Count >= inFlight)
                {
                    var f = fences.Dequeue();
                    while (gl.ClientWaitSync(f, SyncObjectMask.Bit, 1_000_000) is GLEnum.TimeoutExpired && !_stop) { }
                    gl.DeleteSync(f);
                }
                float t = (float)clock.Elapsed.TotalSeconds;
                var world = Matrix4x4.CreateRotationY(t * 0.6f) * Matrix4x4.CreateRotationX(0.55f + 0.25f * MathF.Sin(t * 0.35f));
                GlUtil.Mat(gl, prog, "World", world);
                GlUtil.F(gl, prog, "Time", t);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                gl.Viewport(0, 0, (uint)w, (uint)h);
                gl.ClearColor(0, 0, 0, 1);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)indices.Length, DrawElementsType.UnsignedInt, null, (uint)s.Shells);
                double now = clock.Elapsed.TotalSeconds;
                if (now - lastPreview >= 0.25)
                {
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
                    gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, resolveFbo);
                    gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                    Preview.Capture(resolveFbo, w, h);
                    lastPreview = now;
                }
                fences.Enqueue(gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None));
                gl.Flush();
                Interlocked.Increment(ref _frames);
                _seconds = now;
                if (now - lastT >= 0.5)
                {
                    long fr = Frames;
                    Fps = (fr - lastFrames) / (now - lastT);
                    lastT = now; lastFrames = fr;
                }
            }
            gl.Finish();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Fps = 0; }
    }

    private static void BuildTorus(out Vertex[] vertices, out uint[] indices)
    {
        const int seg = 160, sides = 64;
        const float R = 1f, r = 0.42f;
        vertices = new Vertex[(seg + 1) * (sides + 1)];
        for (int i = 0, k = 0; i <= seg; i++)
        {
            float u = i / (float)seg * MathF.Tau;
            for (int j = 0; j <= sides; j++, k++)
            {
                float v = j / (float)sides * MathF.Tau;
                var n = new Vector3(MathF.Cos(u) * MathF.Cos(v), MathF.Sin(v), MathF.Sin(u) * MathF.Cos(v));
                vertices[k] = new Vertex
                {
                    Pos = new Vector3(MathF.Cos(u) * R, 0, MathF.Sin(u) * R) + n * r,
                    Normal = n,
                    Uv = new Vector2(i / (float)seg, j / (float)sides)
                };
            }
        }
        indices = new uint[seg * sides * 6];
        for (int i = 0, k = 0; i < seg; i++)
            for (int j = 0; j < sides; j++)
            {
                uint a = (uint)(i * (sides + 1) + j), b = (uint)((i + 1) * (sides + 1) + j);
                indices[k++] = a; indices[k++] = b; indices[k++] = a + 1;
                indices[k++] = a + 1; indices[k++] = b; indices[k++] = b + 1;
            }
    }
}
