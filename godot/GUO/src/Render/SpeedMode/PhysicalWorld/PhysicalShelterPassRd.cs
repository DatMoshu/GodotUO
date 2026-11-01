// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: vertical weather shelter from the admitted native physical snapshot.
using System;
using System.Runtime.InteropServices;
using Godot;
using N4 = System.Numerics.Vector4;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

/// <summary>
/// Render-thread-only vertical shelter. Depth is cached by immutable source;
/// screen status follows the actual visible receiver on every admitted ticket.
/// Status R8:0 unknown,128 exposed,255 protected. Unknown preserves the existing
/// weather fallback. This is overhead shelter, not wind-driven precipitation.
/// </summary>
internal sealed class PhysicalShelterPassRd : IDisposable
{
    internal const int Resolution = 1024;
    internal const float MinimumZ = -128, MaximumZ = 382, ReceiverBias = .5f;
    private RD _rd;
    private Rid _depthShader, _depthPipeline, _computeShader, _computePipeline;
    private Rid _depth, _framebuffer, _status, _sampler, _vertices, _parameters, _depthSet, _computeSet;
    private Rid _boundSurface;
    private uint _vertexCapacity, _parameterCapacity;
    private byte[] _vertexUpload = Array.Empty<byte>(), _parameterUpload = Array.Empty<byte>();
    private int _vertexCount;
    private Vector2I _size, _origin;
    private PhysicalWorldSnapshot _snapshot;
    private Texture2Drd _texture;
    internal Texture2Drd Texture => _texture;
    internal bool Ready { get; private set; }
    internal Rid StatusRid => Ready ? _status : default;
    internal Rid DepthRid => Ready ? _depth : default;
    internal PhysicalSnapshotKey SnapshotKey { get; private set; }
    internal int GeometryUploads { get; private set; }
    internal int DepthDraws { get; private set; }

    internal void Render(RD device, PhysicalWorldSnapshot snapshot, Rid receiverSurface, Vector2I origin, Vector2I size, bool publish = true)
    {
        Ready = false;
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            if (snapshot.Overflow || snapshot.Min.Z < MinimumZ || snapshot.Max.Z > MaximumZ
                || snapshot.Triangles.Length > PhysicalWorldBuilder.MaxTriangles || size.X <= 0 || size.Y <= 0)
                throw new InvalidOperationException("Unsupported physical shelter domain.");
            _rd ??= device ?? throw new NotSupportedException("Physical shelter requires the Depth World RenderingDevice.");
            if (_rd != device || !receiverSurface.IsValid || !_rd.TextureIsValid(receiverSurface))
                throw new InvalidOperationException("Physical shelter has no admitted native receiver surface.");
            bool sourceChanged = !ReferenceEquals(_snapshot, snapshot);
            bool targetsChanged = _size != size || !_status.IsValid;
            EnsureResources(size);
            if (sourceChanged) UploadGeometry(snapshot);
            if (sourceChanged || targetsChanged || _origin != origin) UploadParameters(snapshot, origin, size);
            EnsureSets(receiverSurface);
            if (sourceChanged)
            {
                DrawDepth();
                _snapshot = snapshot;
            }
            ClassifyReceivers(size);
            _origin = origin;
            SnapshotKey = snapshot.Key;
            Ready = true;
            if (publish)
            {
                _texture ??= new Texture2Drd();
                _texture.TextureRdRid = _status;
            }
        }
        catch { Dispose(); throw; }
    }

    private void EnsureResources(Vector2I size)
    {
        if (!_depthShader.IsValid)
        {
            using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl,
                SourceVertex = VertexGlsl, SourceFragment = FragmentGlsl };
            using var code = _rd.ShaderCompileSpirVFromSource(source);
            if (!string.IsNullOrEmpty(code.CompileErrorVertex) || !string.IsNullOrEmpty(code.CompileErrorFragment))
                throw new InvalidOperationException(code.CompileErrorVertex + code.CompileErrorFragment);
            _depthShader = _rd.ShaderCreateFromSpirV(code, "guo_physical_shelter_depth"); Require(_depthShader);
            using var compute = new RDShaderSource { Language = RD.ShaderLanguage.Glsl, SourceCompute = ComputeGlsl };
            using var computeCode = _rd.ShaderCompileSpirVFromSource(compute);
            if (!string.IsNullOrEmpty(computeCode.CompileErrorCompute)) throw new InvalidOperationException(computeCode.CompileErrorCompute);
            _computeShader = _rd.ShaderCreateFromSpirV(computeCode, "guo_physical_shelter_status"); Require(_computeShader);
            _computePipeline = _rd.ComputePipelineCreate(_computeShader); Require(_computePipeline);
            using var sampler = new RDSamplerState { MinFilter = RD.SamplerFilter.Nearest,
                MagFilter = RD.SamplerFilter.Nearest, MipFilter = RD.SamplerFilter.Nearest,
                RepeatU = RD.SamplerRepeatMode.ClampToEdge, RepeatV = RD.SamplerRepeatMode.ClampToEdge,
                RepeatW = RD.SamplerRepeatMode.ClampToEdge, MinLod = 0, MaxLod = 0, EnableCompare = false };
            _sampler = _rd.SamplerCreate(sampler); Require(_sampler);
            _depth = CreateTexture(Resolution, Resolution, RD.DataFormat.D32Sfloat,
                RD.TextureUsageBits.DepthStencilAttachmentBit | RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit);
            _framebuffer = _rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _depth }); Require(_framebuffer);
            using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
            using var samples = new RDPipelineMultisampleState();
            using var depth = new RDPipelineDepthStencilState { EnableDepthTest = true, EnableDepthWrite = true,
                DepthCompareOperator = RD.CompareOperator.LessOrEqual };
            using var blend = new RDPipelineColorBlendState();
            _depthPipeline = _rd.RenderPipelineCreate(_depthShader, _rd.FramebufferGetFormat(_framebuffer), RD.InvalidId,
                RD.RenderPrimitive.Triangles, raster, samples, depth, blend); Require(_depthPipeline);
        }
        if (_status.IsValid && _size == size) return;
        if (_texture != null) _texture.TextureRdRid = default;
        FreeSet(ref _computeSet); Free(ref _status);
        _status = CreateTexture(size.X, size.Y, RD.DataFormat.R8Unorm,
            RD.TextureUsageBits.StorageBit | RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit);
        _size = size;
    }

    private Rid CreateTexture(int width, int height, RD.DataFormat format, RD.TextureUsageBits usage)
    {
        if (!_rd.TextureIsFormatSupportedForUsage(format, usage))
            throw new NotSupportedException("Physical shelter attachment format is unavailable: " + format);
        using var spec = new RDTextureFormat { Width = (uint)width, Height = (uint)height, Depth = 1,
            ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D,
            Samples = RD.TextureSamples.Samples1, Format = format, UsageBits = usage };
        using var view = new RDTextureView();
        var texture = _rd.TextureCreate(spec, view); Require(texture); return texture;
    }

    private void UploadGeometry(PhysicalWorldSnapshot snapshot)
    {
        var triangles = snapshot.Triangles.Span;
        int count = 0;
        foreach (ref readonly PhysicalTriangle triangle in triangles)
            if ((triangle.Material & PhysicalMaterial.BlocksWeather) != 0) count += 3;
        int bytes = checked(count * 16);
        EnsureBuffer(ref _vertices, ref _vertexCapacity, Math.Max(16, bytes));
        if (_vertexUpload.Length < bytes) _vertexUpload = new byte[_vertexCapacity];
        var vertices = MemoryMarshal.Cast<byte, N4>(_vertexUpload.AsSpan(0, bytes));
        int index = 0;
        foreach (ref readonly PhysicalTriangle triangle in triangles)
        {
            if ((triangle.Material & PhysicalMaterial.BlocksWeather) == 0) continue;
            vertices[index++] = new N4(triangle.A, 1); vertices[index++] = new N4(triangle.B, 1);
            vertices[index++] = new N4(triangle.C, 1);
        }
        if (bytes > 0 && _rd.BufferUpdate(_vertices, 0, (uint)bytes, _vertexUpload.AsSpan(0, bytes)) != Error.Ok)
            throw new InvalidOperationException("Physical shelter vertex upload failed.");
        _vertexCount = count; GeometryUploads++;
    }

    private void UploadParameters(PhysicalWorldSnapshot snapshot, Vector2I origin, Vector2I size)
    {
        var b = snapshot.Bounds;
        int cells = checked(b.Width * b.Height), bytes = checked(64 + cells * 4);
        EnsureBuffer(ref _parameters, ref _parameterCapacity, bytes);
        if (_parameterUpload.Length < bytes) _parameterUpload = new byte[_parameterCapacity];
        var header = MemoryMarshal.Cast<byte, N4>(_parameterUpload.AsSpan(0, 64));
        header[0] = new(b.X, b.Y, b.Width, b.Height);
        header[1] = new(origin.X, origin.Y, size.X, size.Y);
        header[2] = new(MinimumZ, MaximumZ, ReceiverBias, 0);
        header[3] = new(Resolution, 0, 0, 0);
        var known = MemoryMarshal.Cast<byte, uint>(_parameterUpload.AsSpan(64, cells * 4));
        var unknown = snapshot.UnknownCells.Span;
        for (int i = 0; i < cells; i++) known[i] = unknown[i] == PhysicalUnknownReason.None ? 1u : 0u;
        if (_rd.BufferUpdate(_parameters, 0, (uint)bytes, _parameterUpload.AsSpan(0, bytes)) != Error.Ok)
            throw new InvalidOperationException("Physical shelter parameter upload failed.");
    }

    private void EnsureBuffer(ref Rid buffer, ref uint capacity, int bytes)
    {
        if (buffer.IsValid && capacity >= bytes) return;
        FreeSet(ref _depthSet); FreeSet(ref _computeSet); Free(ref buffer);
        uint next = Math.Max(4096u, capacity);
        while (next < bytes) next = checked(next * 2);
        buffer = _rd.StorageBufferCreate(next, ReadOnlySpan<byte>.Empty, (RD.StorageBufferUsage)0, (RD.BufferCreationBits)0);
        Require(buffer); capacity = next;
    }

    private void EnsureSets(Rid surface)
    {
        if (!_depthSet.IsValid || !_rd.UniformSetIsValid(_depthSet))
        {
            using var vertices = BufferUniform(0, _vertices);
            using var parameters = BufferUniform(1, _parameters);
            _depthSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { vertices, parameters }, _depthShader, 0);
            Require(_depthSet);
        }
        if (_boundSurface == surface && _computeSet.IsValid && _rd.UniformSetIsValid(_computeSet)) return;
        FreeSet(ref _computeSet);
        using var receiver = TextureUniform(0, surface);
        using var depth = TextureUniform(1, _depth);
        using var info = BufferUniform(2, _parameters);
        using var result = new RDUniform { UniformType = RD.UniformType.Image, Binding = 3 };
        result.AddId(_status);
        _computeSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { receiver, depth, info, result }, _computeShader, 0);
        Require(_computeSet); _boundSurface = surface;
    }

    private static RDUniform BufferUniform(int binding, Rid buffer)
    { var u = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = binding }; u.AddId(buffer); return u; }
    private RDUniform TextureUniform(int binding, Rid texture)
    { var u = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = binding }; u.AddId(_sampler); u.AddId(texture); return u; }

    private void DrawDepth()
    {
        _rd.CaptureTimestamp("speed.physical-shelter.depth.begin");
        long list = _rd.DrawListBegin(_framebuffer, RD.DrawFlags.ClearDepth, null, 1f);
        if (list == RD.InvalidId) throw new InvalidOperationException("Physical shelter depth draw failed.");
        try
        {
            if (_vertexCount > 0)
            {
                _rd.DrawListBindRenderPipeline(list, _depthPipeline); _rd.DrawListBindUniformSet(list, _depthSet, 0);
                _rd.DrawListDraw(list, false, 1, (uint)_vertexCount);
            }
        }
        finally { _rd.DrawListEnd(); }
        DepthDraws++;
        _rd.CaptureTimestamp("speed.physical-shelter.depth.end");
    }

    private void ClassifyReceivers(Vector2I size)
    {
        _rd.CaptureTimestamp("speed.physical-shelter.status.begin");
        long list = _rd.ComputeListBegin();
        if (list == RD.InvalidId) throw new InvalidOperationException("Physical shelter compute list failed.");
        try
        {
            _rd.ComputeListBindComputePipeline(list, _computePipeline); _rd.ComputeListBindUniformSet(list, _computeSet, 0);
            _rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
        }
        finally { _rd.ComputeListEnd(); }
        _rd.CaptureTimestamp("speed.physical-shelter.status.end");
    }

    private static void Require(Rid rid) { if (!rid.IsValid) throw new InvalidOperationException("Physical shelter resource creation failed."); }
    private void FreeSet(ref Rid set) { if (set.IsValid && _rd.UniformSetIsValid(set)) _rd.FreeRid(set); set = default; }
    private void Free(ref Rid rid) { if (rid.IsValid) _rd.FreeRid(rid); rid = default; }
    public void Dispose()
    {
        Ready = false; SnapshotKey = default;
        if (_texture != null) { _texture.TextureRdRid = default; _texture.Dispose(); _texture = null; }
        if (_rd != null)
        {
            FreeSet(ref _computeSet); FreeSet(ref _depthSet);
            Free(ref _computePipeline); Free(ref _depthPipeline); Free(ref _framebuffer);
            Free(ref _status); Free(ref _depth); Free(ref _vertices); Free(ref _parameters);
            Free(ref _sampler); Free(ref _computeShader); Free(ref _depthShader);
        }
        _rd = null; _snapshot = null; _boundSurface = default; _size = _origin = default;
        _vertexCount = 0; _vertexCapacity = _parameterCapacity = 0;
        _vertexUpload = _parameterUpload = Array.Empty<byte>();
    }

    private const string VertexGlsl = """
        #version 450
        layout(set=0,binding=0,std430) readonly buffer Vertices { vec4 vertices[]; };
        layout(set=0,binding=1,std430) readonly buffer Parameters { vec4 bounds; vec4 frame; vec4 vertical; vec4 settings; uint known[]; } p;
        void main() {
            vec3 v=vertices[gl_VertexIndex].xyz;
            vec2 uv=(v.xy-p.bounds.xy)/p.bounds.zw;
            float depth=(p.vertical.y-v.z)/(p.vertical.y-p.vertical.x);
            gl_Position=vec4(uv*2.0-1.0,depth,1.0);
        }
        """;
    private const string FragmentGlsl = """
        #version 450
        void main() { }
        """;
    private const string ComputeGlsl = """
        #version 450
        layout(local_size_x=8,local_size_y=8,local_size_z=1) in;
        layout(set=0,binding=0) uniform sampler2D receiver_surface;
        layout(set=0,binding=1) uniform sampler2D shelter_depth;
        layout(set=0,binding=2,std430) readonly buffer Parameters { vec4 bounds; vec4 frame; vec4 vertical; vec4 settings; uint known[]; } p;
        layout(set=0,binding=3,r8) uniform writeonly image2D shelter_status;
        void main() {
            ivec2 pixel=ivec2(gl_GlobalInvocationID.xy);
            if(any(greaterThanEqual(pixel,ivec2(p.frame.zw)))) return;
            vec4 receiver=texelFetch(receiver_surface,pixel,0);
            int packed=int(receiver.w+0.5);
            vec2 local=receiver.xy+p.frame.xy-p.bounds.xy;
            float status=0.0;
            if((packed&256)!=0 && (packed&128)==0 && receiver.z>=p.vertical.x && receiver.z<=p.vertical.y
                && all(greaterThanEqual(local,vec2(0))) && all(lessThan(local,p.bounds.zw))) {
                float depth=textureLod(shelter_depth,local/p.bounds.zw,0.0).r;
                float top=p.vertical.y-depth*(p.vertical.y-p.vertical.x);
                ivec2 cell=ivec2(floor(local));
                uint index=uint(cell.y)*uint(p.bounds.z)+uint(cell.x);
                if(depth<1.0 && top>receiver.z+p.vertical.z) status=1.0;
                else if(p.known[index]!=0u) status=128.0/255.0;
            }
            imageStore(shelter_status,pixel,vec4(status,0,0,1));
        }
        """;
}
