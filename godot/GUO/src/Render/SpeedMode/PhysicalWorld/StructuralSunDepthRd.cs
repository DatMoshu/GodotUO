// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: bounded native structure depth for optional enhanced sunlight.
using System;
using System.Runtime.InteropServices;
using Godot;
using N4 = System.Numerics.Vector4;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

/// <summary>
/// Render-thread-owned depth map. Render consumes an immutable admitted-ticket
/// snapshot; callers bind DepthRid and ParametersRid only after Ready is true.
/// A failure throws through the existing whole-light-frame classic fallback.
/// No worker, readback, presentation geometry or scene mutation is used here.
/// </summary>
internal sealed class StructuralSunDepthRd : IDisposable
{
    internal const int ParameterHeaderBytes = 128;
    private const int VertexBytes = 16;
    private RD _rd;
    private Rid _shader, _pipeline, _depth, _framebuffer, _vertices, _parameters, _set;
    private uint _vertexCapacity, _parameterCapacity;
    private int _resolution, _vertexCount;
    private byte[] _vertexUpload = Array.Empty<byte>(), _parameterUpload = Array.Empty<byte>();
    private uint[] _known = Array.Empty<uint>();
    private PhysicalWorldSnapshot _uploadedSnapshot, _coveredSnapshot;
    private PhysicalSunProjection _drawnProjection, _coveredProjection;
    private bool _hasDraw, _uploadedBypass;

    internal Rid DepthRid => Ready ? _depth : default;
    internal Rid ParametersRid => Ready ? _parameters : default;
    internal bool Ready { get; private set; }
    internal int KnownReceiverCellCount { get; private set; }
    internal int UnknownReceiverCellCount { get; private set; }
    internal int GeometryUploads { get; private set; }
    internal int DepthDraws { get; private set; }
    internal int TriangleCount => _vertexCount / 3;
    internal PhysicalSnapshotKey SnapshotKey { get; private set; }
    internal PhysicalSunProjection Projection { get; private set; }
#if DEBUG
    // Source-isolation fixture: preserve every resource and pass, suppress only
    // the structural contribution. Never a runtime/player-facing option.
    internal static bool ProbeBypass;
#endif

    internal void Render(RD device, PhysicalWorldSnapshot snapshot, in PhysicalSunProjection projection)
    {
        Ready = false;
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            if (snapshot.Overflow || snapshot.Triangles.Length > PhysicalWorldBuilder.MaxTriangles)
                throw new InvalidOperationException("Physical sun snapshot exceeds its structural domain.");
            _rd ??= device ?? throw new NotSupportedException("Structural sun requires the Depth World RenderingDevice.");
            if (_rd != device) throw new InvalidOperationException("Structural sun device changed during a ticket.");
            EnsureResources(projection.Resolution);
            bool geometryChanged = !ReferenceEquals(_uploadedSnapshot, snapshot);
            bool coverageChanged = !ReferenceEquals(_coveredSnapshot, snapshot)
                || _coveredProjection.SourceToSun != projection.SourceToSun
                || _coveredProjection.ReceiverMinZ != projection.ReceiverMinZ
                || _coveredProjection.CasterCeiling != projection.CasterCeiling;
            if (geometryChanged) UploadGeometry(snapshot);
            if (coverageChanged)
            {
                int cells = checked(snapshot.Bounds.Width * snapshot.Bounds.Height);
                if (_known.Length < cells) _known = new uint[cells];
                KnownReceiverCellCount = projection.BuildKnownReceiverCells(snapshot, _known);
                UnknownReceiverCellCount = cells - KnownReceiverCellCount;
                _coveredSnapshot = snapshot; _coveredProjection = projection;
            }
            bool bypass = false;
#if DEBUG
            bypass = ProbeBypass;
#endif
            bool parametersChanged = !_hasDraw || geometryChanged || coverageChanged
                || _drawnProjection != projection || bypass != _uploadedBypass;
            if (parametersChanged) UploadParameters(snapshot, projection, bypass);
            EnsureSet();
            if (!_hasDraw || geometryChanged || _drawnProjection != projection)
            {
                DrawDepth();
                _drawnProjection = projection; _hasDraw = true;
            }
            SnapshotKey = snapshot.Key; Projection = projection;
            Ready = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void EnsureResources(int resolution)
    {
        if (!_shader.IsValid)
        {
            using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl,
                SourceVertex = VertexGlsl, SourceFragment = FragmentGlsl };
            using var code = _rd.ShaderCompileSpirVFromSource(source);
            if (!string.IsNullOrEmpty(code.CompileErrorVertex) || !string.IsNullOrEmpty(code.CompileErrorFragment))
                throw new InvalidOperationException(code.CompileErrorVertex + code.CompileErrorFragment);
            _shader = _rd.ShaderCreateFromSpirV(code, "guo_structural_sun_depth");
            Require(_shader, "shader");
        }
        if (_depth.IsValid && _resolution == resolution) return;
        Free(ref _pipeline); Free(ref _framebuffer); Free(ref _depth);
        _hasDraw = false;
        var usage = RD.TextureUsageBits.DepthStencilAttachmentBit | RD.TextureUsageBits.SamplingBit
            | RD.TextureUsageBits.CanCopyFromBit;
        if (!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.D32Sfloat, usage))
            throw new NotSupportedException("Structural sun requires a sampleable D32 depth attachment.");
        using var format = new RDTextureFormat { Width = (uint)resolution, Height = (uint)resolution,
            Depth = 1, ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D,
            Samples = RD.TextureSamples.Samples1, Format = RD.DataFormat.D32Sfloat, UsageBits = usage };
        using var view = new RDTextureView();
        _depth = _rd.TextureCreate(format, view); Require(_depth, "depth texture");
        _framebuffer = _rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _depth });
        Require(_framebuffer, "framebuffer");
        using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
        using var samples = new RDPipelineMultisampleState();
        using var depth = new RDPipelineDepthStencilState { EnableDepthTest = true, EnableDepthWrite = true,
            DepthCompareOperator = RD.CompareOperator.LessOrEqual };
        using var blend = new RDPipelineColorBlendState();
        _pipeline = _rd.RenderPipelineCreate(_shader, _rd.FramebufferGetFormat(_framebuffer), RD.InvalidId,
            RD.RenderPrimitive.Triangles, raster, samples, depth, blend);
        Require(_pipeline, "pipeline");
        if (!_rd.RenderPipelineIsValid(_pipeline)) throw new InvalidOperationException("Structural sun pipeline is invalid.");
        _resolution = resolution;
    }

    private void UploadGeometry(PhysicalWorldSnapshot snapshot)
    {
        ReadOnlySpan<PhysicalTriangle> triangles = snapshot.Triangles.Span;
        int count = 0;
        foreach (ref readonly PhysicalTriangle triangle in triangles)
            if ((triangle.Material & PhysicalMaterial.BlocksSun) != 0) count += 3;
        int byteCount = checked(count * VertexBytes);
        EnsureBuffer(ref _vertices, ref _vertexCapacity, Math.Max(VertexBytes, byteCount));
        if (_vertexUpload.Length < byteCount) _vertexUpload = new byte[_vertexCapacity];
        Span<N4> vertices = MemoryMarshal.Cast<byte, N4>(_vertexUpload.AsSpan(0, byteCount));
        int index = 0;
        foreach (ref readonly PhysicalTriangle triangle in triangles)
        {
            if ((triangle.Material & PhysicalMaterial.BlocksSun) == 0) continue;
            vertices[index++] = new N4(triangle.A, 1);
            vertices[index++] = new N4(triangle.B, 1);
            vertices[index++] = new N4(triangle.C, 1);
        }
        if (byteCount != 0 && _rd.BufferUpdate(_vertices, 0, (uint)byteCount, _vertexUpload.AsSpan(0, byteCount)) != Error.Ok)
            throw new InvalidOperationException("Structural sun geometry upload failed.");
        _vertexCount = count; _uploadedSnapshot = snapshot; GeometryUploads++;
    }

    private void UploadParameters(PhysicalWorldSnapshot snapshot, in PhysicalSunProjection p, bool bypass)
    {
        PhysicalRect bounds = snapshot.Bounds;
        int cells = checked(bounds.Width * bounds.Height);
        int bytes = checked(ParameterHeaderBytes + cells * sizeof(uint));
        EnsureBuffer(ref _parameters, ref _parameterCapacity, bytes);
        if (_parameterUpload.Length < bytes) _parameterUpload = new byte[_parameterCapacity];
        Span<N4> header = MemoryMarshal.Cast<byte, N4>(_parameterUpload.AsSpan(0, ParameterHeaderBytes));
        header[0] = p.U; header[1] = p.V; header[2] = p.Depth;
        header[3] = new(bounds.X - p.Origin.X, bounds.Y - p.Origin.Y,
            bounds.Right - p.Origin.X, bounds.Bottom - p.Origin.Y);
        header[4] = new(bounds.Width, bounds.Height, 1f / p.Resolution, p.DepthBias);
        header[5] = new(p.SourceToSun.X / p.SourceToSun.Z, p.SourceToSun.Y / p.SourceToSun.Z,
            p.CasterCeiling, bypass ? 0 : p.Strength);
        header[6] = new(p.ReceiverMinZ, p.CasterCeiling, 1, KnownReceiverCellCount);
        header[7] = new(p.Origin.X, p.Origin.Y, 0, 0);
        MemoryMarshal.AsBytes(_known.AsSpan(0, cells)).CopyTo(_parameterUpload.AsSpan(ParameterHeaderBytes));
        if (_rd.BufferUpdate(_parameters, 0, (uint)bytes, _parameterUpload.AsSpan(0, bytes)) != Error.Ok)
            throw new InvalidOperationException("Structural sun parameters upload failed.");
        _uploadedBypass = bypass;
    }

    private void EnsureBuffer(ref Rid buffer, ref uint capacity, int bytes)
    {
        if (buffer.IsValid && capacity >= bytes) return;
        FreeSet(); Free(ref buffer);
        uint next = Math.Max(4096u, capacity);
        while (next < bytes) next = checked(next * 2);
        buffer = _rd.StorageBufferCreate(next, ReadOnlySpan<byte>.Empty, (RD.StorageBufferUsage)0, (RD.BufferCreationBits)0);
        Require(buffer, "storage buffer"); capacity = next;
    }

    private void EnsureSet()
    {
        if (_set.IsValid && _rd.UniformSetIsValid(_set)) return;
        using var vertices = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 0 };
        using var parameters = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 1 };
        vertices.AddId(_vertices); parameters.AddId(_parameters);
        _set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { vertices, parameters }, _shader, 0);
        Require(_set, "uniform set");
    }

    private void DrawDepth()
    {
        _rd.CaptureTimestamp("speed.structural-sun.begin");
        long list = _rd.DrawListBegin(_framebuffer, RD.DrawFlags.ClearDepth, null, 1.0f);
        if (list == RD.InvalidId) throw new InvalidOperationException("Structural sun draw list failed.");
        try
        {
            if (_vertexCount != 0)
            {
                _rd.DrawListBindRenderPipeline(list, _pipeline);
                _rd.DrawListBindUniformSet(list, _set, 0);
                _rd.DrawListDraw(list, false, 1, (uint)_vertexCount);
            }
        }
        finally { _rd.DrawListEnd(); }
        _rd.CaptureTimestamp("speed.structural-sun.end");
        DepthDraws++;
    }

    private static void Require(Rid rid, string name)
    {
        if (!rid.IsValid) throw new InvalidOperationException("Could not create structural sun " + name + ".");
    }
    private void FreeSet()
    {
        if (_set.IsValid && _rd.UniformSetIsValid(_set)) _rd.FreeRid(_set);
        _set = default;
    }
    private void Free(ref Rid rid) { if (rid.IsValid) _rd.FreeRid(rid); rid = default; }

    public void Dispose()
    {
        Ready = false; SnapshotKey = default;
        if (_rd != null)
        {
            FreeSet(); Free(ref _pipeline); Free(ref _framebuffer); Free(ref _depth);
            Free(ref _vertices); Free(ref _parameters); Free(ref _shader);
        }
        _rd = null; _resolution = _vertexCount = 0;
        _vertexCapacity = _parameterCapacity = 0;
        _uploadedSnapshot = _coveredSnapshot = null; _hasDraw = false;
        KnownReceiverCellCount = UnknownReceiverCellCount = 0;
        _vertexUpload = _parameterUpload = Array.Empty<byte>(); _known = Array.Empty<uint>();
    }

    private const string VertexGlsl = """
        #version 450
        layout(set=0,binding=0,std430) readonly buffer Vertices { vec4 vertices[]; };
        layout(set=0,binding=1,std430) readonly buffer Parameters {
            vec4 row_u; vec4 row_v; vec4 row_depth; vec4 bounds;
            vec4 info; vec4 sun; vec4 domain; vec4 origin; uint known[];
        } physical;
        void main() {
            vec4 p=vertices[gl_VertexIndex];
            p.xy-=physical.origin.xy;
            vec3 q=vec3(dot(physical.row_u,p),dot(physical.row_v,p),dot(physical.row_depth,p));
            gl_Position=vec4(q.xy*2.0-1.0,q.z,1.0);
        }
        """;
    private const string FragmentGlsl = """
        #version 450
        void main() { }
        """;
}
