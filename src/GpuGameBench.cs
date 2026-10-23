using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace PharosGaming;

public sealed record GpuGameResult(double AvgFps, double Low1Fps, int Frames, double Seconds, int Width, int Height, string Adapter)
{
    /// <summary>Nota = FPS médio × 10.</summary>
    public double Score => AvgFps * 10;
}

/// <summary>
/// Cena de jogo para benchmark de GPU (no estilo 3DMark/Unigine): vale com terreno por ruído fractal, 40 mil árvores
/// por instancing, sombras em tempo real (shadow map 4096² com PCF), água com ondas procedurais, neblina, céu e
/// pós-processamento HDR. Câmera em trajeto fixo por tempo, resolução fixa: o trabalho é igual em qualquer PC.
/// Renderiza fora da tela (OpenGL sem janela) com uma fila de frames medida por fences: não fica preso ao V-Sync.
/// </summary>
public sealed class GpuGameBench
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);
    public string AdapterName { get; init; } = "";

    public double Progress { get; private set; }
    public double CurrentFps { get; private set; }
    public string? Error { get; private set; }
    public bool Cancelled { get; private set; }
    /// <summary>Prévia para a interface (criada quando o contexto fica pronto).</summary>
    public GlPreview? Preview { get; private set; }

    private volatile bool _stop;
    public void Cancel() => _stop = true;

    // ------------------------------------------------------------------ cena (CPU)

    private const int TerrainN = 512;        // vértices por lado
    private const float TerrainSize = 800f;
    private const float WaterY = 6f;
    private const int TreeCount = 40_000;

    /// <summary>Altura do terreno: ruído fractal (fBm) com um vale sinuoso cortando o mapa.</summary>
    public static float TerrainHeight(float x, float z)
    {
        float h = 0, amp = 1, freq = 0.005f;
        for (int o = 0; o < 6; o++) { h += amp * ValueNoise(x * freq + o * 17.3f, z * freq - o * 9.1f); amp *= 0.5f; freq *= 2.03f; }
        h = h * 55 + 10;
        float river = MathF.Sin(z * 0.007f) * 120;                       // curva do vale
        float d = MathF.Abs(x - river);
        float valley = MathF.Min(1, d / 90f);
        return h * (0.25f + 0.75f * valley * valley) - (1 - valley) * 4;
    }

    private static float ValueNoise(float x, float z)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float fx = x - xi, fz = z - zi;
        float u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
        float a = Hash(xi, zi), b = Hash(xi + 1, zi), c = Hash(xi, zi + 1), d = Hash(xi + 1, zi + 1);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private static float Hash(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector3 Pos; public Vector3 Normal; public float Mat; }

    private static (Vertex[] V, uint[] I) BuildTerrain()
    {
        var v = new Vertex[TerrainN * TerrainN];
        float step = TerrainSize / (TerrainN - 1), half = TerrainSize / 2;
        for (int z = 0; z < TerrainN; z++)
            for (int x = 0; x < TerrainN; x++)
            {
                float wx = x * step - half, wz = z * step - half;
                float h = TerrainHeight(wx, wz);
                float hx = TerrainHeight(wx + 0.5f, wz) - TerrainHeight(wx - 0.5f, wz);
                float hz = TerrainHeight(wx, wz + 0.5f) - TerrainHeight(wx, wz - 0.5f);
                v[z * TerrainN + x] = new Vertex { Pos = new(wx, h, wz), Normal = Vector3.Normalize(new(-hx, 1, -hz)), Mat = 0 };
            }
        var idx = new uint[(TerrainN - 1) * (TerrainN - 1) * 6];
        int k = 0;
        for (int z = 0; z < TerrainN - 1; z++)
            for (int x = 0; x < TerrainN - 1; x++)
            {
                uint a = (uint)(z * TerrainN + x), b = a + 1, c = a + TerrainN, d = c + 1;
                idx[k++] = a; idx[k++] = c; idx[k++] = b;
                idx[k++] = b; idx[k++] = c; idx[k++] = d;
            }
        return (v, idx);
    }

    /// <summary>Árvore low-poly: tronco (cilindro) + 3 cones de folhagem. Mat 1 = tronco, 2 = folhas.</summary>
    private static (Vertex[] V, uint[] I) BuildTree()
    {
        var v = new List<Vertex>(); var idx = new List<uint>();
        void Cone(float baseY, float height, float r0, float r1, int seg, float mat)
        {
            uint start = (uint)v.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * MathF.Tau, c = MathF.Cos(a), s = MathF.Sin(a);
                float slope = (r0 - r1) / height;
                var n = Vector3.Normalize(new Vector3(c, slope, s));
                v.Add(new Vertex { Pos = new(c * r0, baseY, s * r0), Normal = n, Mat = mat });
                v.Add(new Vertex { Pos = new(c * r1, baseY + height, s * r1), Normal = n, Mat = mat });
            }
            for (int i = 0; i < seg; i++)
            {
                uint a = start + (uint)i * 2;
                idx.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 });
            }
        }
        Cone(0, 2.0f, 0.25f, 0.2f, 8, 1);
        Cone(1.4f, 2.6f, 2.0f, 0.05f, 12, 2);
        Cone(2.8f, 2.3f, 1.6f, 0.05f, 12, 2);
        Cone(4.0f, 2.0f, 1.1f, 0.02f, 12, 2);
        return (v.ToArray(), idx.ToArray());
    }

    private static Vector4[] PlaceTrees()
    {
        var list = new List<Vector4>(TreeCount);
        uint s = 2024;
        float R() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s >> 8) / 16777216f; }
        while (list.Count < TreeCount)
        {
            float x = (R() - 0.5f) * TerrainSize * 0.98f, z = (R() - 0.5f) * TerrainSize * 0.98f;
            float h = TerrainHeight(x, z);
            float slope = MathF.Abs(TerrainHeight(x + 1, z) - TerrainHeight(x - 1, z)) + MathF.Abs(TerrainHeight(x, z + 1) - TerrainHeight(x, z - 1));
            if (h < WaterY + 1.5f || h > 48 || slope > 1.4f) continue;
            list.Add(new Vector4(x, h - 0.2f, z, 0.7f + R() * 0.9f));
        }
        return list.ToArray();
    }

    // Trajeto da câmera (Catmull-Rom fechado): sobe o vale por uma margem do rio e volta pela outra.
    private static readonly Vector2[] Path = BuildPath();

    private static Vector2[] BuildPath()
    {
        var p = new List<Vector2>();
        for (int i = 0; i <= 6; i++) { float z = -330 + i * 110; p.Add(new(MathF.Sin(z * 0.007f) * 120 + 45, z)); }
        for (int i = 6; i >= 0; i--) { float z = -330 + i * 110; p.Add(new(MathF.Sin(z * 0.007f) * 120 - 75, z)); }
        return p.ToArray();
    }

    private static Vector2 PathAt(float t)
    {
        int n = Path.Length;
        float f = (t % 1 + 1) % 1 * n;
        int i = (int)f; float u = f - i;
        Vector2 p0 = Path[(i - 1 + n) % n], p1 = Path[i], p2 = Path[(i + 1) % n], p3 = Path[(i + 2) % n];
        return 0.5f * (2 * p1 + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u * u * u);
    }

    private static float CamHeight(Vector2 p)
    {
        float h = WaterY;
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++) h = MathF.Max(h, TerrainHeight(p.X + dx * 10, p.Y + dz * 10));
        return h + 26;
    }

    // ------------------------------------------------------------------ shaders (GLSL 4.30)

    private const string Common = """
        #version 430 core
        uniform mat4 ViewProj, LightViewProj, InvViewProj;
        uniform vec3 CamPos; uniform float Time;
        uniform vec3 SunDir;
        layout(std430, binding = 1) readonly buffer TreeBuf { vec4 Trees[]; };
        """;

    private const string SceneVs = Common + """
        layout(location = 0) in vec3 pos;
        layout(location = 1) in vec3 nrm;
        layout(location = 2) in float mat;
        uniform int Mode; // 0 terreno, 1 árvore, 2 água
        out vec3 vWpos; out vec3 vNrm; out float vMat;
        vec3 TreeWorld(vec3 p, int iid, out mat3 rot)
        {
            vec4 t = Trees[iid];
            float a = fract(sin(float(iid) * 12.9898) * 43758.5453) * 6.2831;
            float c = cos(a), s = sin(a);
            rot = mat3(c, 0, s, 0, 1, 0, -s, 0, c);
            float sway = sin(Time * 1.3 + float(iid)) * 0.03 * p.y;   // vento
            return rot * p * t.w + t.xyz + vec3(sway, 0, sway * 0.5);
        }
        void main()
        {
            vMat = mat;
            if (Mode == 1) { mat3 rot; vWpos = TreeWorld(pos, gl_InstanceID, rot); vNrm = rot * nrm; }
            else { vWpos = pos; vNrm = Mode == 2 ? vec3(0, 1, 0) : nrm; }
            gl_Position = ViewProj * vec4(vWpos, 1);
        }
        """;

    private const string ShadowVs = Common + """
        layout(location = 0) in vec3 pos;
        uniform int Mode;
        void main()
        {
            vec3 w = pos;
            if (Mode == 1)
            {
                vec4 t = Trees[gl_InstanceID];
                float a = fract(sin(float(gl_InstanceID) * 12.9898) * 43758.5453) * 6.2831;
                float c = cos(a), s = sin(a);
                w = mat3(c, 0, s, 0, 1, 0, -s, 0, c) * pos * t.w + t.xyz;
            }
            gl_Position = LightViewProj * vec4(w, 1);
        }
        """;

    private const string ShadowFs = "#version 430 core\nvoid main() {}\n";

    private const string Shading = """
        uniform sampler2DShadow ShadowMap;
        vec3 Sky(vec3 dir)
        {
            float up = clamp(dir.y, 0.0, 1.0);
            vec3 col = mix(vec3(0.42), vec3(0.12), pow(up, 0.6));
            float sun = clamp(dot(dir, -SunDir), 0.0, 1.0);
            col += pow(sun, 600.0) * 12.0 + pow(sun, 12.0) * 0.25;
            return col;
        }
        float Shadow(vec3 wpos, vec3 n)
        {
            vec4 lp = LightViewProj * vec4(wpos + n * 0.15, 1);
            vec2 uv = lp.xy * 0.5 + 0.5;
            if (any(lessThan(uv, vec2(0))) || any(greaterThan(uv, vec2(1)))) return 1.0;
            float sum = 0.0;
            for (int y = -2; y <= 2; y++)
                for (int x = -2; x <= 2; x++)
                    sum += texture(ShadowMap, vec3(uv + vec2(x, y) / 4096.0, lp.z - 0.0015));
            return sum / 25.0;
        }
        vec3 Shade(vec3 wpos, vec3 n, float albedo)
        {
            vec3 l = -SunDir;
            float ndl = clamp(dot(n, l), 0.0, 1.0);
            vec3 direct = vec3(ndl * Shadow(wpos, n) * 1.5);
            vec3 ambient = vec3(mix(0.06, 0.16, n.y * 0.5 + 0.5));
            vec3 col = albedo * (direct + ambient);
            float dist = length(wpos - CamPos);
            float fog = 1.0 - exp(-dist * 0.0022);
            return mix(col, Sky(normalize(wpos - CamPos)), fog);
        }
        """;

    private const string SceneFs = Common + Shading + """
        in vec3 vWpos; in vec3 vNrm; in float vMat;
        uniform int Mode;
        out vec4 outColor;
        void main()
        {
            if (Mode == 0)
            {
                vec3 n = normalize(vNrm);
                float slope = 1.0 - n.y;
                float h = vWpos.y;
                float albedo = mix(0.22, 0.15, clamp((h - 8.0) / 25.0, 0.0, 1.0));                          // grama
                albedo = mix(albedo, 0.28, clamp((slope - 0.25) * 4.0, 0.0, 1.0));                         // rocha nas encostas
                albedo = mix(albedo, 0.70, clamp((h - 62.0) / 10.0, 0.0, 1.0) * clamp(1.0 - slope * 3.0, 0.0, 1.0)); // neve nos picos
                albedo *= 0.9 + 0.1 * fract(sin(dot(floor(vWpos.xz * 2.0), vec2(12.9898, 78.233))) * 43758.5453);
                outColor = vec4(Shade(vWpos, n, albedo), 1);
            }
            else if (Mode == 1)
            {
                float albedo = vMat < 1.5 ? 0.08 : 0.11;
                outColor = vec4(Shade(vWpos, normalize(vNrm), albedo), 1);
            }
            else
            {
                vec2 p = vWpos.xz;
                vec3 n = vec3(0, 1, 0);
                for (int k = 0; k < 6; k++)           // ondas somadas
                {
                    vec2 d = vec2(cos(float(k) * 1.7), sin(float(k) * 1.7));
                    float f = 0.15 * float(k + 1), ph = dot(d, p) * f + Time * (1.0 + float(k) * 0.4);
                    n.xz += d * cos(ph) * 0.06 / float(k + 1);
                }
                n = normalize(n);
                vec3 v = normalize(vWpos - CamPos);
                float fres = 0.04 + 0.96 * pow(1.0 - clamp(dot(-v, n), 0.0, 1.0), 5.0);
                vec3 refl = Sky(reflect(v, n)) * Shadow(vWpos, n);
                vec3 col = mix(vec3(0.05), refl, fres);
                float fog = 1.0 - exp(-length(vWpos - CamPos) * 0.0022);
                outColor = vec4(mix(col, Sky(v), fog), 1);
            }
        }
        """;

    private const string FullscreenVs = """
        #version 430 core
        out vec2 uv;
        void main()
        {
            uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(uv * 2.0 - 1.0, 1, 1);
        }
        """;

    private const string SkyFs = Common + Shading + """
        in vec2 uv;
        out vec4 outColor;
        void main()
        {
            vec4 w = InvViewProj * vec4(uv * 2.0 - 1.0, 1, 1);
            outColor = vec4(Sky(normalize(w.xyz / w.w - CamPos)), 1);
        }
        """;

    private const string PostFs = """
        #version 430 core
        in vec2 uv;
        uniform sampler2D Hdr;
        out vec4 outColor;
        void main()
        {
            vec3 c = texture(Hdr, uv).rgb;
            c = c * (2.51 * c + 0.03) / (c * (2.43 * c + 0.59) + 0.14);         // ACES
            vec2 d = uv - 0.5;
            c *= 1.0 - dot(d, d) * 0.9;                                         // vinheta
            float g = pow(clamp(c.r, 0.0, 1.0), 1.0 / 2.2);
            outColor = vec4(g, g, g, 1);
        }
        """;

    // ------------------------------------------------------------------ execução

    private static unsafe uint Mesh(GL gl, Vertex[] v, uint[] i)
    {
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vb = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vb);
        fixed (Vertex* p = v) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(v.Length * sizeof(Vertex)), p, BufferUsageARB.StaticDraw);
        uint ib = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ib);
        fixed (uint* p = i) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(i.Length * 4), p, BufferUsageARB.StaticDraw);
        uint stride = (uint)sizeof(Vertex);
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
        gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)24);
        gl.BindVertexArray(0);
        return vao;
    }

    public unsafe GpuGameResult? Run()
    {
        try
        {
            using var ctx = GlContext.Create(AdapterName);
            var gl = ctx.Gl;
            if (ctx.ClipControl) gl.ClipControl(ClipControlOrigin.LowerLeft, ClipControlDepth.ZeroToOne); // profundidade 0..1 (matrizes do System.Numerics)
            Preview = new GlPreview(gl);

            // ---- Alvos: HDR + profundidade, LDR, shadow map ----
            var (hdrFbo, hdrTex, _) = GlUtil.Target(gl, Width, Height, InternalFormat.Rgba16f, depth: true);
            var (ldrFbo, _, _) = GlUtil.Target(gl, Width, Height, InternalFormat.Rgba8, depth: false);
            const int ShadowRes = 4096;
            uint shadowTex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, shadowTex);
            gl.TexStorage2D(TextureTarget.Texture2D, 1, SizedInternalFormat.DepthComponent32f, ShadowRes, ShadowRes);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            uint shadowFbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, shadowTex, 0);
            gl.DrawBuffer(DrawBufferMode.None);

            // ---- Shaders ----
            uint scene = GlUtil.Program(gl, (ShaderType.VertexShader, SceneVs), (ShaderType.FragmentShader, SceneFs));
            uint shadow = GlUtil.Program(gl, (ShaderType.VertexShader, ShadowVs), (ShaderType.FragmentShader, ShadowFs));
            uint sky = GlUtil.Program(gl, (ShaderType.VertexShader, FullscreenVs), (ShaderType.FragmentShader, SkyFs));
            uint post = GlUtil.Program(gl, (ShaderType.VertexShader, FullscreenVs), (ShaderType.FragmentShader, PostFs));

            // ---- Geometria ----
            var (tv, ti) = BuildTerrain();
            uint terrain = Mesh(gl, tv, ti);
            var (treeV, treeI) = BuildTree();
            uint tree = Mesh(gl, treeV, treeI);
            var trees = PlaceTrees();
            uint treeBuf = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, treeBuf);
            fixed (Vector4* p = trees) gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(trees.Length * 16), p, BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, treeBuf);
            float wh2 = TerrainSize / 2;
            uint water = Mesh(gl, new[]
            {
                new Vertex { Pos = new(-wh2, WaterY, -wh2) }, new Vertex { Pos = new(wh2, WaterY, -wh2) },
                new Vertex { Pos = new(-wh2, WaterY, wh2) }, new Vertex { Pos = new(wh2, WaterY, wh2) },
            }, new uint[] { 0, 2, 1, 1, 2, 3 });
            uint emptyVao = gl.GenVertexArray();

            var sunDir = Vector3.Normalize(new Vector3(-0.45f, -0.55f, -0.35f));
            var lightVP = Matrix4x4.CreateLookAt(-sunDir * 900, Vector3.Zero, Vector3.UnitY) * Matrix4x4.CreateOrthographic(900, 900, 1, 1900);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.5f, 2000);

            void Frame(uint prog, Vector3 eye, Matrix4x4 vp, Matrix4x4 inv, float time)
            {
                gl.UseProgram(prog);
                GlUtil.Mat(gl, prog, "ViewProj", vp); GlUtil.Mat(gl, prog, "LightViewProj", lightVP); GlUtil.Mat(gl, prog, "InvViewProj", inv);
                GlUtil.Vec3(gl, prog, "CamPos", eye); GlUtil.F(gl, prog, "Time", time); GlUtil.Vec3(gl, prog, "SunDir", sunDir);
            }

            // ---- Loop: fila de 8 frames medida por fences ----
            const int inFlight = 8;
            var fences = new Queue<IntPtr>();
            var frameTimes = new List<double>(20000);
            var clock = Stopwatch.StartNew();
            double lastDone = -1, lastPreview = 0, lastTitle = 0;
            int frames = 0, titleFrames = 0;
            gl.Enable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);

            while (!_stop && clock.Elapsed < Duration)
            {
                if (fences.Count >= inFlight)
                {
                    var f = fences.Dequeue();
                    // espera ativa: dormir pode atrasar o registro e contar uma travada que a GPU não teve
                    while (gl.ClientWaitSync(f, SyncObjectMask.Bit, 0) is GLEnum.TimeoutExpired && !_stop) Thread.SpinWait(20);
                    gl.DeleteSync(f);
                    double done = clock.Elapsed.TotalMilliseconds;
                    if (lastDone >= 0) frameTimes.Add(done - lastDone);
                    lastDone = done;
                    frames++; titleFrames++;
                }
                // Câmera pelo tempo (não pelos frames): a mesma cena aparece no mesmo segundo em qualquer PC.
                float t = (float)(clock.Elapsed.TotalSeconds / Duration.TotalSeconds), time = (float)clock.Elapsed.TotalSeconds;
                var p2 = PathAt(t); var ahead = PathAt(t + 0.035f);
                var eye = new Vector3(p2.X, CamHeight(p2), p2.Y);
                var view = Matrix4x4.CreateLookAt(eye, new Vector3(ahead.X, CamHeight(ahead) - 22, ahead.Y), Vector3.UnitY);
                var vp = view * proj;
                Matrix4x4.Invert(vp, out var inv);

                // 1) Mapa de sombras
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFbo);
                gl.Viewport(0, 0, ShadowRes, ShadowRes);
                gl.Clear(ClearBufferMask.DepthBufferBit);
                gl.Enable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(2.0f, 4.0f);
                Frame(shadow, eye, vp, inv, time);
                GlUtil.I(gl, shadow, "Mode", 0);
                gl.BindVertexArray(terrain); gl.DrawElements(PrimitiveType.Triangles, (uint)ti.Length, DrawElementsType.UnsignedInt, null);
                GlUtil.I(gl, shadow, "Mode", 1);
                gl.BindVertexArray(tree); gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)treeI.Length, DrawElementsType.UnsignedInt, null, (uint)trees.Length);
                gl.Disable(EnableCap.PolygonOffsetFill);

                // 2) Cena em HDR: céu, terreno, árvores, água
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, hdrFbo);
                gl.Viewport(0, 0, (uint)Width, (uint)Height);
                gl.Clear(ClearBufferMask.DepthBufferBit);
                gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, shadowTex);
                gl.Disable(EnableCap.DepthTest);
                Frame(sky, eye, vp, inv, time);
                GlUtil.I(gl, sky, "ShadowMap", 0);
                gl.BindVertexArray(emptyVao); gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                gl.Enable(EnableCap.DepthTest);
                Frame(scene, eye, vp, inv, time);
                GlUtil.I(gl, scene, "ShadowMap", 0);
                GlUtil.I(gl, scene, "Mode", 0);
                gl.BindVertexArray(terrain); gl.DrawElements(PrimitiveType.Triangles, (uint)ti.Length, DrawElementsType.UnsignedInt, null);
                GlUtil.I(gl, scene, "Mode", 1);
                gl.BindVertexArray(tree); gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)treeI.Length, DrawElementsType.UnsignedInt, null, (uint)trees.Length);
                GlUtil.I(gl, scene, "Mode", 2);
                gl.BindVertexArray(water); gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, null);

                // 3) Pós-processamento: HDR → LDR (tone mapping + vinheta)
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, ldrFbo);
                gl.Disable(EnableCap.DepthTest);
                gl.UseProgram(post);
                gl.BindTexture(TextureTarget.Texture2D, hdrTex);
                GlUtil.I(gl, post, "Hdr", 0);
                gl.BindVertexArray(emptyVao); gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                gl.Enable(EnableCap.DepthTest);

                double now = clock.Elapsed.TotalSeconds;
                if (now - lastPreview >= 0.25) { Preview.Capture(ldrFbo, Width, Height); lastPreview = now; }
                fences.Enqueue(gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None));
                gl.Flush(); // envia o frame à GPU já; sem isso o driver segura os comandos e a GPU fica com buracos ociosos

                if (now - lastTitle >= 0.5)
                {
                    CurrentFps = titleFrames / (now - lastTitle);
                    titleFrames = 0; lastTitle = now;
                    Progress = now / Duration.TotalSeconds;
                }
            }
            gl.Finish();
            if (_stop) { Cancelled = true; return null; }
            return Result(frameTimes, frames, clock.Elapsed.TotalSeconds, ctx.Renderer);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    private GpuGameResult Result(List<double> frameTimes, int frames, double seconds, string adapter)
    {
        var (avg, low1) = FrameStatsOf(frameTimes);
        return new GpuGameResult(avg, low1, frames, seconds, Width, Height, adapter.Split('/')[0]);
    }

    /// <summary>FPS médio e "1% low" (FPS do frame no percentil 99 de tempo) de uma lista de tempos de frame em ms.</summary>
    public static (double Avg, double Low1) FrameStatsOf(IReadOnlyCollection<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0) return (0, 0);
        var sorted = frameTimesMs.OrderBy(x => x).ToArray();
        double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
        double secs = sorted.Sum() / 1000;
        return (secs > 0 ? sorted.Length / secs : 0, p99 > 0 ? 1000 / p99 : 0);
    }
}
