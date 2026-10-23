using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace PharosGaming;

/// <summary>Uma passada medida do teste de ray tracing.</summary>
public sealed record RtPass(double AvgFps, double Low1Fps, int Frames);

/// <summary>
/// Resultado do teste de ray tracing: nota pela MEDIANA das passadas (resiste a uma passada ruim) e variação entre elas,
/// que diz se o resultado é confiável para comparar.
/// </summary>
public sealed record GpuRtResult(IReadOnlyList<RtPass> Passes, int Width, int Height, string Preset, string Adapter, double GigaRaysPerSec)
{
    public double MedianFps => Median(Passes.Select(p => p.AvgFps));
    public double MedianLow1 => Median(Passes.Select(p => p.Low1Fps));
    /// <summary>(maior − menor) ÷ mediana, em %.</summary>
    public double VariationPct => Passes.Count < 2 || MedianFps <= 0 ? 0 : (Passes.Max(p => p.AvgFps) - Passes.Min(p => p.AvgFps)) / MedianFps * 100;
    public bool Reliable => Passes.Count >= 3 && VariationPct < 3;
    /// <summary>Nota = FPS mediano × 10 (mesma escala da cena de jogo).</summary>
    public double Score => MedianFps * 10;

    public static double Median(IEnumerable<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}

/// <summary>
/// PHAROS BENCH · Ray tracing. No Windows usava DXR 1.1 (núcleos RT); no Linux o caminho equivalente seria Vulkan com
/// ray query, então aqui o path tracer roda num compute shader OpenGL (raios calculados pelos shaders, sem os núcleos RT):
/// a nota não se compara com a do Windows. Mesma cena: ~4 mil objetos (esferas, toros, colunas, caixas) em fosco, metal e
/// espelho, luzes emissivas; por pixel, visibilidade, sombra suave do sol, oclusão de ambiente e rebatidas. A estrutura de
/// aceleração (grade uniforme) recebe os objetos girando a cada frame, como uma TLAS refeita.
/// Para comparar com confiança: aquecimento sem medir, depois 3 passadas iguais; nota pela mediana e variação entre passadas.
/// </summary>
public sealed class GpuRtBench
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public string Preset { get; init; } = "alta";     // normal | alta | extrema
    public TimeSpan PassDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int Passes { get; init; } = 3;
    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(10);
    public string AdapterName { get; init; } = "";

    public double Progress { get; private set; }
    public double CurrentFps { get; private set; }
    public string Phase { get; private set; } = "";
    public string? Error { get; private set; }
    public bool Cancelled { get; private set; }
    public GlPreview? Preview { get; private set; }

    private volatile bool _stop;
    public void Cancel() => _stop = true;

    private (uint Spp, uint Bounces) Quality => Preset switch
    {
        "normal" => (2u, 2u),
        "extrema" => (8u, 4u),
        _ => (4u, 3u),
    };

    /// <summary>Raios por amostra: 1 de visibilidade/rebatida + 1 sombra + 1 oclusão por rebatida.</summary>
    private double RaysPerPixel => Quality.Spp * Quality.Bounces * 3.0;

    // ------------------------------------------------------------------ cena (CPU)

    public const int Sphere = 0, Torus = 1, Column = 2, Box = 3;
    private const int ObjectCount = 4000;
    private const float World = 220;
    private const int Grid = 56;
    private const float GridMin = -112, Cell = 4;

    /// <summary>Objeto no formato std430 do shader (96 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuObj
    {
        public Vector4 PosBound;    // centro + raio envolvente
        public Vector4 SizeType;    // tamanho (por tipo) + tipo
        public Vector4 AlbedoMetal;
        public Vector4 RoughEmis;   // rugosidade, emissão
        public Matrix4x4 Rot;       // rotação mundo → objeto (caixas e toros girando)
    }

    private sealed record Obj(int Type, Vector3 Pos, Vector3 Size, float Bound, float Spin, Vector3 Axis, Vector3 Albedo, float Metal, float Rough, float Emissive);

    /// <summary>Objetos espalhados com semente fixa: a mesma cena em qualquer PC.</summary>
    private static List<Obj> BuildObjects()
    {
        var rnd = new Random(20261015);
        float R() => (float)rnd.NextDouble();
        var list = new List<Obj>();
        // Esferas-espelho e douradas grandes no centro (reflexos de reflexos).
        for (int i = 0; i < 7; i++)
        {
            float a = i / 7f * MathF.Tau;
            bool gold = i % 2 == 1;
            list.Add(new(Sphere, new(MathF.Cos(a) * 16, 6, MathF.Sin(a) * 16), new(6), 6, 0, Vector3.UnitY,
                         gold ? new(1.0f, 0.78f, 0.34f) : new(0.95f, 0.95f, 0.97f), 1, gold ? 0.12f : 0.0f, 0));
        }
        while (list.Count < ObjectCount)
        {
            var pos = new Vector3((R() - 0.5f) * World, 0, (R() - 0.5f) * World);
            if (new Vector2(pos.X, pos.Z).Length() < 26) continue; // deixa o centro livre para as esferas grandes
            int type = R() switch { < 0.35f => Sphere, < 0.55f => Torus, < 0.75f => Column, _ => Box };
            float s = 0.6f + R() * 1.8f;
            Vector3 size; float bound;
            switch (type)
            {
                case Column: size = new(s * 0.6f, s * (2 + R() * 3), 0); pos.Y = size.Y / 2; bound = MathF.Sqrt(size.X * size.X + size.Y * size.Y / 4); break;
                case Torus: size = new(s, s * 0.35f, 0); pos.Y = s * 0.35f + 0.6f; bound = s * 1.35f; break;
                case Box: size = new(s); pos.Y = s; bound = s * 1.733f; break;
                default: size = new(s); pos.Y = s; bound = s; break;
            }
            float roll = R();
            var albedo = Palette(R(), R());
            float metal = roll < 0.3f ? 1 : 0, rough = roll < 0.3f ? R() * 0.35f : 0.6f + R() * 0.4f, emis = roll > 0.97f ? 6 + R() * 10 : 0;
            float spin = type is Torus or Box && R() < 0.5f ? 0.3f + R() * 1.2f : 0;
            var axis = Vector3.Normalize(new Vector3(R() - 0.5f, 1, R() - 0.5f));
            list.Add(new(type, pos, size, bound, spin, axis, albedo, metal, rough, emis));
        }
        return list;
    }

    /// <summary>Cores saturadas de uma paleta fixa, com brilho variado.</summary>
    private static Vector3 Palette(float pick, float shade)
    {
        Vector3[] p = { new(0.85f, 0.18f, 0.12f), new(0.95f, 0.55f, 0.08f), new(0.12f, 0.45f, 0.85f), new(0.1f, 0.65f, 0.4f),
                        new(0.9f, 0.85f, 0.75f), new(0.45f, 0.2f, 0.7f), new(0.08f, 0.08f, 0.1f), new(0.75f, 0.75f, 0.78f) };
        return p[(int)(pick * p.Length) % p.Length] * (0.55f + shade * 0.45f);
    }

    /// <summary>Grade uniforme no plano XZ: para cada célula, a lista de objetos cujo círculo envolvente a toca.</summary>
    public static (int[] Start, int[] Items) BuildGrid(IReadOnlyList<(Vector3 Pos, float Bound)> objs, int grid, float min, float cell)
    {
        var cells = new List<int>[grid * grid];
        for (int i = 0; i < cells.Length; i++) cells[i] = new List<int>();
        for (int i = 0; i < objs.Count; i++)
        {
            var (p, b) = objs[i];
            int x0 = Math.Clamp((int)MathF.Floor((p.X - b - min) / cell), 0, grid - 1), x1 = Math.Clamp((int)MathF.Floor((p.X + b - min) / cell), 0, grid - 1);
            int z0 = Math.Clamp((int)MathF.Floor((p.Z - b - min) / cell), 0, grid - 1), z1 = Math.Clamp((int)MathF.Floor((p.Z + b - min) / cell), 0, grid - 1);
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) cells[z * grid + x].Add(i);
        }
        var start = new int[cells.Length + 1];
        var items = new List<int>();
        for (int i = 0; i < cells.Length; i++) { start[i] = items.Count; items.AddRange(cells[i]); }
        start[cells.Length] = items.Count;
        return (start, items.ToArray());
    }

    /// <summary>Câmera em trajeto fixo pelo tempo da passada (0..1): mesma sequência de imagens em qualquer placa.</summary>
    private static (Vector3 Eye, Vector3 Target) CameraAt(float t)
    {
        float a = t * MathF.Tau * 0.75f + 0.4f;
        float r = 40 + 26 * MathF.Sin(t * MathF.PI);
        var eye = new Vector3(MathF.Cos(a) * r, 12 + 10 * (0.5f + 0.5f * MathF.Sin(t * MathF.Tau * 1.5f)), MathF.Sin(a) * r);
        var target = new Vector3(MathF.Cos(a + 0.6f) * 6, 3, MathF.Sin(a + 0.6f) * 6);
        return (eye, target);
    }

    // ------------------------------------------------------------------ shader (GLSL 4.30, compute)

    private const string Cs = """
        #version 430 core
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(rgba8, binding = 0) uniform writeonly image2D Output;
        uniform mat4 InvViewProj;
        uniform vec3 CamPos; uniform vec3 SunDir;
        uniform uint FrameIndex, Width, Height, Spp, Bounces;
        struct Obj { vec4 posBound; vec4 sizeType; vec4 albedoMetal; vec4 roughEmis; mat4 rot; };
        layout(std430, binding = 1) readonly buffer Objs { Obj objs[]; };
        layout(std430, binding = 2) readonly buffer CellStart { int cellStart[]; };
        layout(std430, binding = 3) readonly buffer CellItems { int cellItems[]; };
        const int GRID = 56; const float GMIN = -112.0, CELL = 4.0, HMAX = 13.0;

        uint Pcg(inout uint s) { s = s * 747796405u + 2891336453u; uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u; return (w >> 22u) ^ w; }
        float Rnd(inout uint s) { return float(Pcg(s) & 0xFFFFFFu) / 16777216.0; }

        vec3 CosineDir(vec3 n, inout uint s)
        {
            float u = Rnd(s), v = Rnd(s), r = sqrt(u), phi = 6.2831853 * v;
            vec3 t = abs(n.y) < 0.99 ? normalize(cross(n, vec3(0, 1, 0))) : vec3(1, 0, 0);
            vec3 b = cross(n, t);
            return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u)));
        }

        vec3 Sky(vec3 d)
        {
            vec3 sky = mix(vec3(0.85, 0.9, 1.0), vec3(0.25, 0.45, 0.85), clamp(d.y, 0.0, 1.0));
            float sun = pow(clamp(dot(d, -SunDir), 0.0, 1.0), 900.0) * 60.0;
            return sky * 0.8 + sun * vec3(1.0, 0.92, 0.8);
        }

        // ---- interseções (t > 0 mais próximo, normal no espaço do mundo) ----
        bool HitSphere(vec3 o, vec3 d, vec3 c, float r, float tmax, out float t, out vec3 n)
        {
            vec3 oc = o - c; float b = dot(oc, d), q = dot(oc, oc) - r * r, disc = b * b - q;
            t = 0.0; n = vec3(0);
            if (disc < 0.0) return false;
            float sq = sqrt(disc); t = -b - sq; if (t < 1e-3) t = -b + sq;
            if (t < 1e-3 || t > tmax) return false;
            n = (o + d * t - c) / r; return true;
        }
        bool HitBox(vec3 o, vec3 d, Obj ob, float tmax, out float t, out vec3 n)
        {
            mat3 R = mat3(ob.rot);
            vec3 lo = R * (o - ob.posBound.xyz), ld = R * d, h = ob.sizeType.xyz;
            vec3 inv = 1.0 / ld, t0 = (-h - lo) * inv, t1 = (h - lo) * inv;
            vec3 tn = min(t0, t1), tf = max(t0, t1);
            float a = max(max(tn.x, tn.y), tn.z), b = min(min(tf.x, tf.y), tf.z);
            t = a > 1e-3 ? a : b; n = vec3(0);
            if (a > b || t < 1e-3 || t > tmax) return false;
            vec3 p = (lo + ld * t) / h, ap = abs(p);
            vec3 ln = ap.x > ap.y && ap.x > ap.z ? vec3(sign(p.x), 0, 0) : ap.y > ap.z ? vec3(0, sign(p.y), 0) : vec3(0, 0, sign(p.z));
            n = transpose(R) * ln; return true;
        }
        bool HitColumn(vec3 o, vec3 d, Obj ob, float tmax, out float t, out vec3 n)
        {
            vec3 c = ob.posBound.xyz; float r = ob.sizeType.x, hh = ob.sizeType.y * 0.5;
            vec2 oc = o.xz - c.xz; float a = dot(d.xz, d.xz), b = dot(oc, d.xz), q = dot(oc, oc) - r * r;
            t = 1e30; n = vec3(0);
            float disc = b * b - a * q;
            if (a > 1e-8 && disc >= 0.0)
            {
                float tc = (-b - sqrt(disc)) / a; float y = o.y + d.y * tc - c.y;
                if (tc > 1e-3 && abs(y) <= hh) { t = tc; n = vec3((o.xz + d.xz * tc - c.xz) / r, 0).xzy; }
            }
            if (abs(d.y) > 1e-6)   // tampas
            {
                float ty = (c.y + hh * sign(o.y - c.y) - o.y) / d.y; vec2 p = o.xz + d.xz * ty - c.xz;
                if (ty > 1e-3 && ty < t && dot(p, p) <= r * r) { t = ty; n = vec3(0, sign(o.y - c.y), 0); }
            }
            return t < tmax;
        }
        float TorusSdf(vec3 p, float R, float r) { vec2 q = vec2(length(p.xz) - R, p.y); return length(q) - r; }
        bool HitTorus(vec3 o, vec3 d, Obj ob, float tmax, out float t, out vec3 n)
        {
            t = 0.0; n = vec3(0);
            float tb, dummy; vec3 dn;
            vec3 oc = o - ob.posBound.xyz; float b = dot(oc, d), rb = ob.posBound.w, q = dot(oc, oc) - rb * rb, disc = b * b - q;
            if (disc < 0.0) return false;
            float sq = sqrt(disc); float ta = max(-b - sq, 1e-3), te = min(-b + sq, tmax);
            mat3 Rm = mat3(ob.rot); vec3 lo = Rm * oc, ld = Rm * d;
            float R = ob.sizeType.x, r = ob.sizeType.y;
            t = ta;
            for (int i = 0; i < 48 && t < te; i++)   // sphere tracing no espaço do objeto
            {
                float dist = TorusSdf(lo + ld * t, R, r);
                if (dist < 1e-3)
                {
                    vec3 p = lo + ld * t; vec2 e = vec2(1e-3, 0);
                    vec3 ln = normalize(vec3(TorusSdf(p + e.xyy, R, r) - TorusSdf(p - e.xyy, R, r), TorusSdf(p + e.yxy, R, r) - TorusSdf(p - e.yxy, R, r),
                                             TorusSdf(p + e.yyx, R, r) - TorusSdf(p - e.yyx, R, r)));
                    n = transpose(Rm) * ln; return true;
                }
                t += dist;
            }
            return false;
        }
        bool HitObj(vec3 o, vec3 d, int i, float tmax, out float t, out vec3 n)
        {
            Obj ob = objs[i]; int type = int(ob.sizeType.w);
            if (type == 0) return HitSphere(o, d, ob.posBound.xyz, ob.sizeType.x, tmax, t, n);
            if (type == 1) return HitTorus(o, d, ob, tmax, t, n);
            if (type == 2) return HitColumn(o, d, ob, tmax, t, n);
            return HitBox(o, d, ob, tmax, t, n);
        }

        // Percorre a grade XZ (DDA) até achar o mais próximo (ou qualquer um, para sombra/oclusão).
        bool Trace(vec3 o, vec3 d, float tmax, bool anyHit, out float tBest, out vec3 nBest, out int idBest)
        {
            tBest = tmax; nBest = vec3(0); idBest = -2;
            if (d.y < -1e-6) { float tg = -o.y / d.y; if (tg > 1e-3 && tg < tBest) { tBest = tg; nBest = vec3(0, 1, 0); idBest = -1; if (anyHit) return true; } }
            vec3 bmin = vec3(GMIN, 0, GMIN), bmax = vec3(GMIN + GRID * CELL, HMAX, GMIN + GRID * CELL);
            vec3 inv = 1.0 / d, s0 = (bmin - o) * inv, s1 = (bmax - o) * inv;
            float te = max(max(min(s0.x, s1.x), min(s0.y, s1.y)), min(s0.z, s1.z));
            float tx = min(min(max(s0.x, s1.x), max(s0.y, s1.y)), max(s0.z, s1.z));
            te = max(te, 0.0); tx = min(tx, tBest);
            if (te > tx) return idBest != -2;
            vec2 p = (o.xz + d.xz * te - GMIN) / CELL;
            ivec2 c = clamp(ivec2(floor(p)), ivec2(0), ivec2(GRID - 1));
            ivec2 st = ivec2(d.x > 0.0 ? 1 : -1, d.z > 0.0 ? 1 : -1);
            vec2 dxz = vec2(abs(d.x) < 1e-8 ? 1e-8 : d.x, abs(d.z) < 1e-8 ? 1e-8 : d.z);
            vec2 tDelta = abs(CELL / dxz);
            vec2 nextB = (vec2(c) + vec2(st.x > 0 ? 1 : 0, st.y > 0 ? 1 : 0)) * CELL + GMIN;
            vec2 tMaxV = (nextB - o.xz) / dxz;
            for (int guard = 0; guard < GRID * 3; guard++)
            {
                int cell = c.y * GRID + c.x;
                for (int k = cellStart[cell]; k < cellStart[cell + 1]; k++)
                {
                    int i = cellItems[k]; float t; vec3 n;
                    if (HitObj(o, d, i, tBest, t, n)) { tBest = t; nBest = n; idBest = i; if (anyHit) return true; }
                }
                float tExit = min(tMaxV.x, tMaxV.y);
                if (tBest <= tExit || tExit > tx) break;
                if (tMaxV.x < tMaxV.y) { c.x += st.x; tMaxV.x += tDelta.x; } else { c.y += st.y; tMaxV.y += tDelta.y; }
                if (c.x < 0 || c.y < 0 || c.x >= GRID || c.y >= GRID) break;
            }
            return idBest != -2;
        }

        void main()
        {
            uvec2 id = gl_GlobalInvocationID.xy;
            if (id.x >= Width || id.y >= Height) return;
            uint seed = (id.y * Width + id.x) * 9781u + FrameIndex * 6271u;
            vec3 color = vec3(0);
            for (uint s = 0u; s < Spp; s++)
            {
                vec2 uv = (vec2(id) + vec2(Rnd(seed), Rnd(seed))) / vec2(Width, Height);
                vec4 p = InvViewProj * vec4(uv * 2.0 - 1.0, 1, 1);
                vec3 o = CamPos, d = normalize(p.xyz / p.w - CamPos);
                vec3 through = vec3(1), acc = vec3(0);
                for (uint b = 0u; b < Bounces; b++)
                {
                    float t; vec3 n; int idh;
                    if (!Trace(o, d, 2000.0, false, t, n, idh)) { acc += through * Sky(d); break; }
                    vec3 albedo; float metal, rough, emis;
                    if (idh < 0) { albedo = vec3(0.55, 0.55, 0.58); metal = 0.15; rough = 0.35; emis = 0.0; }
                    else { Obj ob = objs[idh]; albedo = ob.albedoMetal.rgb; metal = ob.albedoMetal.a; rough = ob.roughEmis.x; emis = ob.roughEmis.y; }
                    if (dot(n, d) > 0.0) n = -n;
                    vec3 hp = o + d * t + n * 0.003;
                    acc += through * albedo * emis;
                    // Sombra suave do sol (direção sorteada num cone) + oclusão de ambiente (um raio no hemisfério).
                    vec3 sd = normalize(-SunDir + (vec3(Rnd(seed), Rnd(seed), Rnd(seed)) - 0.5) * 0.06);
                    float ndl = clamp(dot(n, sd), 0.0, 1.0), ts; vec3 ns; int is;
                    float sunVis = ndl > 0.0 && !Trace(hp, sd, 1000.0, true, ts, ns, is) ? 1.0 : 0.0;
                    vec3 ad = CosineDir(n, seed);
                    float ao = Trace(hp, ad, 6.0, true, ts, ns, is) ? 0.15 : 1.0;
                    vec3 diffuse = albedo * (1.0 - metal);
                    acc += through * diffuse * (ndl * sunVis * vec3(3.6, 3.3, 2.9) + ao * vec3(0.12, 0.15, 0.2));
                    // Próxima rebatida: reflexo (metal) ou iluminação indireta (difuso).
                    if (metal > 0.5) { d = normalize(reflect(d, n) + (CosineDir(n, seed) - n) * rough); through *= albedo; }
                    else { d = CosineDir(n, seed); through *= albedo * 0.8; }
                    o = hp;
                    if (max(through.x, max(through.y, through.z)) < 0.02) break;
                }
                color += acc;
            }
            color /= float(Spp);
            color = color / (1.0 + color);                     // tone mapping (Reinhard)
            color = pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2)); // gama
            imageStore(Output, ivec2(id), vec4(color, 1));
        }
        """;

    // ------------------------------------------------------------------ execução

    public unsafe GpuRtResult? Run()
    {
        try
        {
            using var ctx = GlContext.Create(AdapterName);
            var gl = ctx.Gl;
            Preview = new GlPreview(gl);
            var (outFbo, outTex, _) = GlUtil.Target(gl, Width, Height, InternalFormat.Rgba8, depth: false);
            uint prog = GlUtil.Program(gl, (ShaderType.ComputeShader, Cs));

            var objs = BuildObjects();
            var (start, items) = BuildGrid(objs.Select(o => (o.Pos, o.Bound)).ToList(), Grid, GridMin, Cell);
            uint Ssbo(uint binding, void* data, nuint bytes, BufferUsageARB usage)
            {
                uint b = gl.GenBuffer();
                gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, b);
                gl.BufferData(BufferTargetARB.ShaderStorageBuffer, bytes, data, usage);
                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, b);
                return b;
            }
            var gpu = new GpuObj[objs.Count];
            uint objBuf;
            fixed (GpuObj* p = gpu) objBuf = Ssbo(1, p, (nuint)(gpu.Length * sizeof(GpuObj)), BufferUsageARB.DynamicDraw);
            fixed (int* p = start) Ssbo(2, p, (nuint)(start.Length * 4), BufferUsageARB.StaticDraw);
            fixed (int* p = items) Ssbo(3, p, (nuint)(Math.Max(1, items.Length) * 4), BufferUsageARB.StaticDraw);

            var (spp, bounces) = Quality;
            var sunDir = Vector3.Normalize(new Vector3(-0.35f, -0.8f, -0.45f));
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 2000);
            gl.UseProgram(prog);
            GlUtil.Vec3(gl, prog, "SunDir", sunDir);
            gl.Uniform1(gl.GetUniformLocation(prog, "Width"), (uint)Width);
            gl.Uniform1(gl.GetUniformLocation(prog, "Height"), (uint)Height);
            gl.Uniform1(gl.GetUniformLocation(prog, "Spp"), spp);
            gl.Uniform1(gl.GetUniformLocation(prog, "Bounces"), bounces);
            gl.BindImageTexture(0, outTex, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba8);

            var clock = Stopwatch.StartNew();
            uint frameIndex = 0;
            double lastPreview = 0;
            var passes = new List<RtPass>();
            double totalSecs = Warmup.TotalSeconds + Passes * PassDuration.TotalSeconds;

            // Uma etapa (aquecimento ou passada): fila de 4 frames medida por fences, câmera pelo tempo da etapa.
            List<double> RunStage(TimeSpan dur, double before, string phase)
            {
                Phase = phase;
                var times = new List<double>(4096);
                var fences = new Queue<IntPtr>();
                var sw = Stopwatch.StartNew();
                double lastDone = -1, lastFps = 0; int fpsFrames = 0;
                while (!_stop && sw.Elapsed < dur)
                {
                    if (fences.Count >= 4)
                    {
                        var f = fences.Dequeue();
                        while (gl.ClientWaitSync(f, SyncObjectMask.Bit, 0) is GLEnum.TimeoutExpired && !_stop) Thread.SpinWait(20);
                        gl.DeleteSync(f);
                        double done = sw.Elapsed.TotalMilliseconds;
                        if (lastDone >= 0) times.Add(done - lastDone);
                        lastDone = done; fpsFrames++;
                    }
                    float st = (float)(sw.Elapsed.TotalSeconds / dur.TotalSeconds), time = (float)sw.Elapsed.TotalSeconds;
                    // "TLAS" do frame: objetos girando recebem a rotação nova
                    for (int i = 0; i < objs.Count; i++)
                    {
                        var o = objs[i];
                        var rot = o.Spin != 0 ? Matrix4x4.CreateFromAxisAngle(o.Axis, time * o.Spin) : Matrix4x4.Identity;
                        gpu[i] = new GpuObj
                        {
                            PosBound = new(o.Pos, o.Bound), SizeType = new(o.Size, o.Type),
                            AlbedoMetal = new(o.Albedo, o.Metal), RoughEmis = new(o.Rough, o.Emissive, 0, 0), Rot = rot,
                        };
                    }
                    gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, objBuf);
                    fixed (GpuObj* p = gpu) gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(gpu.Length * sizeof(GpuObj)), p);
                    var (eye, target) = CameraAt(st);
                    Matrix4x4.Invert(Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * proj, out var inv);
                    GlUtil.Mat(gl, prog, "InvViewProj", inv);
                    GlUtil.Vec3(gl, prog, "CamPos", eye);
                    gl.Uniform1(gl.GetUniformLocation(prog, "FrameIndex"), frameIndex++);
                    gl.DispatchCompute((uint)(Width + 7) / 8, (uint)(Height + 7) / 8, 1);
                    double now = clock.Elapsed.TotalSeconds;
                    if (now - lastPreview >= 0.25)
                    {
                        gl.MemoryBarrier(MemoryBarrierMask.FramebufferBarrierBit);
                        Preview.Capture(outFbo, Width, Height);
                        lastPreview = now;
                    }
                    fences.Enqueue(gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None));
                    gl.Flush();
                    if (sw.Elapsed.TotalSeconds - lastFps >= 0.5)
                    {
                        CurrentFps = fpsFrames / (sw.Elapsed.TotalSeconds - lastFps);
                        fpsFrames = 0; lastFps = sw.Elapsed.TotalSeconds;
                    }
                    Progress = (before + sw.Elapsed.TotalSeconds) / totalSecs;
                }
                gl.Finish();
                return times;
            }

            RunStage(Warmup, 0, "Aquecimento (não conta)");
            for (int i = 0; i < Passes && !_stop; i++)
            {
                var times = RunStage(PassDuration, Warmup.TotalSeconds + i * PassDuration.TotalSeconds, $"Passada {i + 1} de {Passes}");
                if (_stop) break;
                var (avg, low1) = GpuGameBench.FrameStatsOf(times);
                passes.Add(new RtPass(avg, low1, times.Count));
            }
            if (_stop) { Cancelled = true; return null; }
            double medianFps = GpuRtResult.Median(passes.Select(p => p.AvgFps));
            return new GpuRtResult(passes, Width, Height, Preset, ctx.Renderer.Split('/')[0], medianFps * Width * Height * RaysPerPixel / 1e9);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
    }
}
