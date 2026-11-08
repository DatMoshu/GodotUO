// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;
using N4 = System.Numerics.Vector4;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

/// <summary>
/// Render-thread-only one-lamp pilot. Six perspective faces occupy one 3x2 D32
/// atlas and use one nearest sampler. Triangles preserve full Z intervals; no
/// nearest-wall/top reduction, CPU per-pixel query or cubemap allocation exists.
/// </summary>
internal sealed class StructuralLampDepthRd : IDisposable
{
    internal const int ParameterHeaderBytes = 80;
    private RD _rd;
    private Rid _shader, _pipeline, _depth, _framebuffer, _vertices, _parameters, _set;
    private uint _vertexCapacity, _parameterCapacity;
    private int _resolution, _vertexCount;
    private byte[] _vertexUpload = Array.Empty<byte>(), _parameterUpload = Array.Empty<byte>();
    private uint[] _known = Array.Empty<uint>();
    private PhysicalWorldSnapshot _uploaded, _covered;
    private PhysicalLampProjection _drawnProjection, _coveredProjection;
    private bool _hasDraw, _lastBypass;
    internal Rid DepthRid => Ready ? _depth : default;
    internal Rid ParametersRid => Ready ? _parameters : default;
    internal bool Ready { get; private set; }
    internal int KnownReceiverCellCount { get; private set; }
    internal int TriangleCount => _vertexCount / 3;
    internal int GeometryUploads { get; private set; }
    internal int DepthDraws { get; private set; }
    internal PhysicalSnapshotKey SnapshotKey { get; private set; }
    internal PhysicalLampProjection Projection { get; private set; }
#if DEBUG
    internal static bool ProbeBypass;
#endif

    internal void Render(RD device, PhysicalWorldSnapshot snapshot, in PhysicalLampProjection projection)
    {
        Ready = false;
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            if (snapshot.Overflow || snapshot.Triangles.Length > PhysicalWorldBuilder.MaxTriangles)
                throw new InvalidOperationException("Structural lamp snapshot exceeds its bounded domain.");
            _rd ??= device ?? throw new NotSupportedException("Structural lamps require Depth World RenderingDevice.");
            if (_rd != device) throw new InvalidOperationException("Structural lamp device changed during a ticket.");
            EnsureResources(projection.FaceResolution);
            bool geometryChanged = !ReferenceEquals(_uploaded, snapshot);
            if (geometryChanged) UploadGeometry(snapshot);
            bool coverageChanged = !ReferenceEquals(_covered, snapshot)
                || _coveredProjection.Lamp != projection.Lamp;
            if (coverageChanged)
            {
                int cells = checked(snapshot.Bounds.Width * snapshot.Bounds.Height);
                if (_known.Length < cells) _known = new uint[cells];
                KnownReceiverCellCount = projection.BuildKnownReceiverCells(snapshot, _known);
                _covered = snapshot; _coveredProjection = projection;
            }
            bool bypass = false;
#if DEBUG
            bypass = ProbeBypass;
#endif
            if (!_hasDraw || geometryChanged || coverageChanged || _drawnProjection != projection || bypass != _lastBypass)
                UploadParameters(snapshot, projection, bypass);
            EnsureSet();
            if (!_hasDraw || geometryChanged || _drawnProjection != projection)
            {
                DrawDepth(); _drawnProjection = projection; _hasDraw = true;
            }
            SnapshotKey = snapshot.Key; Projection = projection; Ready = true;
        }
        catch { Dispose(); throw; }
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
            _shader = _rd.ShaderCreateFromSpirV(code, "guo_structural_lamp_depth"); Require(_shader, "shader");
        }
        if (_depth.IsValid && _resolution == resolution) return;
        Free(ref _pipeline); Free(ref _framebuffer); Free(ref _depth); _hasDraw = false;
        var usage = RD.TextureUsageBits.DepthStencilAttachmentBit | RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit;
        if (!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.D32Sfloat, usage))
            throw new NotSupportedException("Structural lamps require sampleable D32 depth.");
        using var format = new RDTextureFormat { Width = (uint)(resolution * 3), Height = (uint)(resolution * 2),
            Depth = 1, ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D,
            Samples = RD.TextureSamples.Samples1, Format = RD.DataFormat.D32Sfloat, UsageBits = usage };
        using var view = new RDTextureView();
        _depth = _rd.TextureCreate(format, view); Require(_depth, "atlas");
        _framebuffer = _rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _depth }); Require(_framebuffer, "framebuffer");
        using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
        using var samples = new RDPipelineMultisampleState();
        using var depth = new RDPipelineDepthStencilState { EnableDepthTest = true, EnableDepthWrite = true,
            DepthCompareOperator = RD.CompareOperator.LessOrEqual };
        using var blend = new RDPipelineColorBlendState();
        _pipeline = _rd.RenderPipelineCreate(_shader, _rd.FramebufferGetFormat(_framebuffer), RD.InvalidId,
            RD.RenderPrimitive.Triangles, raster, samples, depth, blend); Require(_pipeline, "pipeline");
        if (!_rd.RenderPipelineIsValid(_pipeline)) throw new InvalidOperationException("Structural lamp pipeline is invalid.");
        _resolution = resolution;
    }

    private void UploadGeometry(PhysicalWorldSnapshot snapshot)
    {
        int count = 0;
        foreach (ref readonly PhysicalTriangle triangle in snapshot.Triangles.Span)
            if ((triangle.Material & PhysicalMaterial.BlocksLocalLight) != 0) count += 3;
        int bytes = checked(count * 16);
        EnsureBuffer(ref _vertices, ref _vertexCapacity, Math.Max(16, bytes));
        if (_vertexUpload.Length < bytes) _vertexUpload = new byte[_vertexCapacity];
        Span<N4> vertices = MemoryMarshal.Cast<byte, N4>(_vertexUpload.AsSpan(0, bytes));
        int i = 0;
        foreach (ref readonly PhysicalTriangle triangle in snapshot.Triangles.Span)
        {
            if ((triangle.Material & PhysicalMaterial.BlocksLocalLight) == 0) continue;
            vertices[i++] = new N4(triangle.A, 1); vertices[i++] = new N4(triangle.B, 1); vertices[i++] = new N4(triangle.C, 1);
        }
        if (bytes > 0 && _rd.BufferUpdate(_vertices, 0, (uint)bytes, _vertexUpload.AsSpan(0, bytes)) != Error.Ok)
            throw new InvalidOperationException("Structural lamp geometry upload failed.");
        _vertexCount = count; _uploaded = snapshot; GeometryUploads++;
    }

    private void UploadParameters(PhysicalWorldSnapshot snapshot, in PhysicalLampProjection p, bool bypass)
    {
        var b = snapshot.Bounds;
        int cells = checked(b.Width * b.Height), bytes = checked(ParameterHeaderBytes + cells * sizeof(uint));
        EnsureBuffer(ref _parameters, ref _parameterCapacity, bytes);
        if (_parameterUpload.Length < bytes) _parameterUpload = new byte[_parameterCapacity];
        Span<N4> header = MemoryMarshal.Cast<byte, N4>(_parameterUpload.AsSpan(0, ParameterHeaderBytes));
        header[0] = new(p.Lamp.Position.X - p.Origin.X, p.Lamp.Position.Y - p.Origin.Y, p.Lamp.Position.Z, p.Lamp.Radius);
        header[1] = new(p.FaceResolution, p.Near, p.Far, p.Tangent);
        header[2] = new(b.X - p.Origin.X, b.Y - p.Origin.Y, b.Right - p.Origin.X, b.Bottom - p.Origin.Y);
        header[3] = new(b.Width, b.Height, p.Bias / p.Far, bypass ? 0 : 1);
        header[4] = new(p.Origin.X, p.Origin.Y, p.OfferIndex, KnownReceiverCellCount);
        MemoryMarshal.AsBytes(_known.AsSpan(0, cells)).CopyTo(_parameterUpload.AsSpan(ParameterHeaderBytes));
        if (_rd.BufferUpdate(_parameters, 0, (uint)bytes, _parameterUpload.AsSpan(0, bytes)) != Error.Ok)
            throw new InvalidOperationException("Structural lamp parameter upload failed.");
        _lastBypass = bypass;
    }

    private void EnsureBuffer(ref Rid buffer, ref uint capacity, int bytes)
    {
        if (buffer.IsValid && capacity >= bytes) return;
        FreeSet(); Free(ref buffer);
        uint next = Math.Max(4096u, capacity);
        while (next < bytes) next = checked(next * 2);
        buffer = _rd.StorageBufferCreate(next, ReadOnlySpan<byte>.Empty, (RD.StorageBufferUsage)0, (RD.BufferCreationBits)0);
        Require(buffer, "buffer"); capacity = next;
    }

    private void EnsureSet()
    {
        if (_set.IsValid && _rd.UniformSetIsValid(_set)) return;
        using var vertices = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 0 };
        using var parameters = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 1 };
        vertices.AddId(_vertices); parameters.AddId(_parameters);
        _set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { vertices, parameters }, _shader, 0);
        Require(_set, "set");
    }

    private void DrawDepth()
    {
        _rd.CaptureTimestamp("speed.structural-lamp.begin");
        long list = _rd.DrawListBegin(_framebuffer, RD.DrawFlags.ClearDepth, null, 1.0f);
        if (list == RD.InvalidId) throw new InvalidOperationException("Structural lamp draw list failed.");
        try
        {
            if (_vertexCount > 0)
            {
                _rd.DrawListBindRenderPipeline(list, _pipeline); _rd.DrawListBindUniformSet(list, _set, 0);
                _rd.DrawListDraw(list, false, 6, (uint)_vertexCount);
            }
        }
        finally { _rd.DrawListEnd(); }
        _rd.CaptureTimestamp("speed.structural-lamp.end"); DepthDraws++;
    }

    private static void Require(Rid rid, string name) { if (!rid.IsValid) throw new InvalidOperationException("Could not create structural lamp " + name + "."); }
    private void Free(ref Rid rid) { if (rid.IsValid) _rd.FreeRid(rid); rid = default; }
    private void FreeSet() { if (_set.IsValid && _rd.UniformSetIsValid(_set)) _rd.FreeRid(_set); _set = default; }
    public void Dispose()
    {
        Ready = false; SnapshotKey = default;
        if (_rd != null)
        {
            FreeSet(); Free(ref _pipeline); Free(ref _framebuffer); Free(ref _depth);
            Free(ref _vertices); Free(ref _parameters); Free(ref _shader);
        }
        _rd = null; _resolution = _vertexCount = 0; _vertexCapacity = _parameterCapacity = 0;
        _uploaded = _covered = null; _hasDraw = false; KnownReceiverCellCount = 0;
        _known = Array.Empty<uint>(); _vertexUpload = _parameterUpload = Array.Empty<byte>();
    }

    private const string BasisGlsl = """
        const vec3 forward_axes[6]=vec3[6](vec3(1,0,0),vec3(-1,0,0),vec3(0,1,0),vec3(0,-1,0),vec3(0,0,1),vec3(0,0,-1));
        const vec3 right_axes[6]=vec3[6](vec3(0,-1,0),vec3(0,1,0),vec3(1,0,0),vec3(-1,0,0),vec3(1,0,0),vec3(1,0,0));
        const vec3 up_axes[6]=vec3[6](vec3(0,0,1),vec3(0,0,1),vec3(0,0,1),vec3(0,0,1),vec3(0,-1,0),vec3(0,1,0));
        """;
    private const string ParameterGlsl = """
        layout(set=0,binding=1,std430) readonly buffer Parameters {
            vec4 lamp; vec4 info; vec4 bounds; vec4 domain; vec4 origin; uint known[];
        } physical_lamp;
        """;
    private const string VertexGlsl = "#version 450\n" + BasisGlsl + "\n" + ParameterGlsl + "\n" + """
        layout(set=0,binding=0,std430) readonly buffer Vertices { vec4 vertices[]; };
        layout(location=0) flat out int face;
        layout(location=1) out vec3 metric_position;
        void main() {
            face=gl_InstanceIndex;
            vec3 p=vertices[gl_VertexIndex].xyz;
            p.xy-=physical_lamp.origin.xy;
            p-=physical_lamp.lamp.xyz; p.z*=0.12856487;
            metric_position=p;
            float major=dot(forward_axes[face],p),near_z=physical_lamp.info.y,far_z=physical_lamp.info.z;
            vec2 clip=vec2(dot(right_axes[face],p),dot(up_axes[face],p))/physical_lamp.info.w;
            vec2 offset=(vec2(face%3,face/3)*2.0+1.0)/vec2(3,2)-1.0;
            gl_Position=vec4(clip/vec2(3,2)+major*offset,
                far_z/(far_z-near_z)*major-near_z*far_z/(far_z-near_z),major);
        }
        """;
    private const string FragmentGlsl = "#version 450\n" + BasisGlsl + "\n" + ParameterGlsl + "\n" + """
        layout(location=0) flat in int face;
        layout(location=1) in vec3 metric_position;
        void main() {
            // Each instance owns one atlas rectangle. Hardware clips near/far;
            // this exact rectangle clip prevents a wide triangle spilling into another face.
            vec2 low=vec2(face%3,face/3)*physical_lamp.info.x;
            if(any(lessThan(gl_FragCoord.xy,low)) || any(greaterThanEqual(gl_FragCoord.xy,low+physical_lamp.info.x))) discard;
            gl_FragDepth=clamp(dot(forward_axes[face],metric_position)/physical_lamp.info.z,0.0,1.0);
        }
        """;

    // Copy/adapt these bindings in the existing light shader. Caller passes native
    // origin-relative XYZ, qualification bit and CURRENT offer index. Only Clear
    // or Blocked replace old reach/polar; Unknown preserves existing behavior.
    internal const string SamplingGlsl = """
        // Required: sampler2D structural_lamp_depth; SSBO physical_lamp with
        // vec4 lamp,info,bounds,domain,origin; uint known[] (80-byte header).
        // -1 unknown, 0 complete clear segment, 1 qualified opaque hit.
        int structural_lamp_status(vec3 position,int packed,int offer) {
            if((packed&256)==0 || physical_lamp.domain.w<0.5 || offer!=int(physical_lamp.origin.z+0.5)
                || any(isnan(position)) || any(isinf(position))) return -1;
            if(any(lessThan(position.xy,physical_lamp.bounds.xy)) || any(greaterThanEqual(position.xy,physical_lamp.bounds.zw))) return -1;
            vec3 p=position-physical_lamp.lamp.xyz; p.z*=0.12856487;
            vec3 a=abs(p);
            int face=a.x>=a.y && a.x>=a.z ? (p.x>=0.0 ? 0:1) : a.y>=a.z ? (p.y>=0.0 ? 2:3) : (p.z>=0.0 ? 4:5);
            float major=max(a.x,max(a.y,a.z));
            if(major<physical_lamp.info.y || major>=physical_lamp.info.z) return -1;
            vec2 side=face==0 ? vec2(-p.y,p.z) : face==1 ? vec2(p.y,p.z) : face==2 ? vec2(p.x,p.z)
                : face==3 ? vec2(-p.x,p.z) : face==4 ? vec2(p.x,-p.y) : vec2(p.x,p.y);
            vec2 uv=side/(major*physical_lamp.info.w)*0.5+0.5;
            float inset=0.5/physical_lamp.info.x;
            uv=(vec2(face%3,face/3)+clamp(uv,vec2(inset),vec2(1.0-inset)))/vec2(3,2);
            float blocker=textureLod(structural_lamp_depth,uv,0.0).r;
            if(blocker<major/physical_lamp.info.z-physical_lamp.domain.z) return 1;
            ivec2 cell=ivec2(floor(position.xy-physical_lamp.bounds.xy));
            uint known=physical_lamp.known[cell.y*int(physical_lamp.domain.x)+cell.x];
            return known!=0u ? 0 : -1;
        }
        """;
}
