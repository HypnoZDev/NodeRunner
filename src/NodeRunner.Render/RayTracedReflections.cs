using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NodeRunner.Data.Models;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using D11 = Vortice.Direct3D11;
using D12 = Vortice.Direct3D12;

namespace NodeRunner.Render;

// Positions and triangles of a model (as in its GPU buffers), for the ray tracing.
internal sealed class RayGeometry(Vector3[] positions, ushort[] indices)
{
    public Vector3[] Positions { get; } = positions;
    public ushort[] Indices { get; } = indices;
}

// Reflections on the water and wet ground followed through the 3D scene by the graphics card (DirectX 12 ray tracing,
// next to the DirectX 11 drawing): what they mirror is found even off screen. Each model is sent once (its own
// acceleration structure, built a few per frame); each frame, the objects around the camera are put together and, for
// each water or wet pixel, the reflected ray (written by WaterRenderer.BeginRayStarts, SceneImage.WriteWetRayStarts)
// is followed. The models show the average colour of their textures, lit by the sun (with shadows) and the sky.
public sealed unsafe class RayTracedReflections : IDisposable
{
    private const int FramesInFlight = 3;
    private const int MaxArenas = 64;
    private const long GeometryArenaSize = 64L << 20;
    private const long StructureArenaSize = 64L << 20;
    private const int TableSlots = 1 << 18;
    private const int GeometryEntrySize = 32;
    private const int MaxModelsPerFrame = 64;
    private const long MaxTrianglesPerFrame = 200_000;

    private const string ShaderSource = @"
#define RS ""RootConstants(num32BitConstants=32, b0), SRV(t0), SRV(t1), DescriptorTable(SRV(t2, numDescriptors=2), UAV(u0)), DescriptorTable(SRV(t0, space=1, numDescriptors=64, flags=DESCRIPTORS_VOLATILE))""

cbuffer Constants : register(b0)
{
    float4 SunDirection;  // w = 1: shadows.
    float4 SunColor;
    float4 SkyAmbient;
    float4 GroundAmbient;
    float4 Horizon;       // Fog colour.
    float4 Fog;           // x = 1: fog, y: fog distance, z: longest ray (m).
    float4 Camera;        // xyz: the camera, the starts are written from it.
    uint2 Size;
    uint2 Pad;
};

// One mesh of a model: where its positions (of its first vertex) and indices (16 bits) are, and its colour.
struct Geometry { uint Arena; uint Positions; uint Indices; uint Pad; float4 Color; };

RaytracingAccelerationStructure Scene : register(t0);
StructuredBuffer<Geometry> Geometries : register(t1);
Texture2D<float4> Starts : register(t2);
Texture2D<float4> Directions : register(t3);
RWTexture2D<float4> Output : register(u0);
ByteAddressBuffer Arenas[64] : register(t0, space1);

uint Index(uint arena, uint at)
{
    uint word = Arenas[NonUniformResourceIndex(arena)].Load(at & ~3u);
    return (at & 2u) != 0u ? word >> 16 : word & 0xFFFFu;
}

float3 Vertex(uint arena, uint at)
{
    return asfloat(Arenas[NonUniformResourceIndex(arena)].Load3(at));
}

[RootSignature(RS)]
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size))
        return;
    float4 start = Starts[id.xy];
    if (start.w < 0.5f)
    {
        Output[id.xy] = 0.0f;
        return;
    }
    float3 dir = normalize(Directions[id.xy].xyz);
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    RayDesc ray;
    ray.Origin = Camera.xyz + start.xyz;
    ray.Direction = dir;
    ray.TMin = 0.05f;
    ray.TMax = Fog.z;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    q.Proceed();
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
    {
        Output[id.xy] = 0.0f; // The sky: the water's own.
        return;
    }

    Geometry g = Geometries[q.CommittedInstanceID() + q.CommittedGeometryIndex()];
    uint p = g.Indices + q.CommittedPrimitiveIndex() * 6u;
    float3 a = Vertex(g.Arena, g.Positions + Index(g.Arena, p) * 12u);
    float3 b = Vertex(g.Arena, g.Positions + Index(g.Arena, p + 2u) * 12u);
    float3 c = Vertex(g.Arena, g.Positions + Index(g.Arena, p + 4u) * 12u);
    float3 n = mul(cross(b - a, c - a), (float3x3)q.CommittedWorldToObject3x4());
    n = dot(n, n) > 1e-12f ? normalize(n) : -dir;
    if (dot(n, dir) > 0.0f)
        n = -n;
    float t = q.CommittedRayT();
    float3 hit = ray.Origin + dir * t;

    float sun = saturate(dot(n, SunDirection.xyz));
    if (SunDirection.w > 0.5f && sun > 0.0f)
    {
        RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> s;
        RayDesc toSun;
        toSun.Origin = hit + n * 0.05f;
        toSun.Direction = SunDirection.xyz;
        toSun.TMin = 0.0f;
        toSun.TMax = 1500.0f;
        s.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, toSun);
        s.Proceed();
        if (s.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
            sun = 0.0f;
    }
    float3 ambient = lerp(GroundAmbient.rgb, SkyAmbient.rgb, 0.5f + 0.5f * n.z);
    float3 color = g.Color.rgb * (ambient + SunColor.rgb * sun);

    // Same haze as the models, over the length of the reflected ray.
    float fog = Fog.x * saturate((t - 0.25f * Fog.y) / (0.75f * Fog.y));
    fog = pow(fog * fog * (3.0f - 2.0f * fog), 1.5f);
    Output[id.xy] = float4(lerp(color, Horizon.rgb, fog), 1.0f);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 SunDirection;
        public Vector4 SunColor;
        public Vector4 SkyAmbient;
        public Vector4 GroundAmbient;
        public Vector4 Horizon;
        public Vector4 Fog;
        public Vector4 Camera;
        public uint Width, Height, Pad0, Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryEntry
    {
        public uint Arena, Positions, Indices, Pad;
        public Vector4 Color;
    }

    private sealed class Arena(ID3D12Resource buffer, long size)
    {
        public ID3D12Resource Buffer { get; } = buffer;
        public RangeAllocator Space { get; } = new(size);
        public bool Written;
    }

    // Where a model is kept: its positions and indices, its acceleration structure, its entries in the table.
    private sealed class RayModel
    {
        public bool Built;
        public bool Skipped;
        public int GeometryArena = -1;
        public long GeometryOffset, GeometryLength;
        public int StructureArena = -1;
        public long StructureOffset, StructureLength;
        public int FirstSlot = -1, SlotCount;
        public ulong Address;
    }

    private readonly record struct Instance(RayModel Model, Matrix4x4 World, ulong Address);

    private readonly ID3D11Device _device11;
    private readonly ID3D11DeviceContext4 _context;
    private readonly ID3D11Fence _fence11;
    private readonly ID3D12Device5 _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[FramesInFlight];
    private readonly ulong[] _allocatorFence = new ulong[FramesInFlight];
    private readonly ID3D12GraphicsCommandList4 _list;
    private readonly ID3D12Fence _fence;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipeline;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly uint _descriptorSize;
    private readonly ID3D12Resource _table;
    private readonly GeometryEntry* _tableData;
    private readonly RangeAllocator _slots = new(TableSlots);
    private readonly List<Arena> _geometryArenas = new();
    private readonly List<Arena> _structureArenas = new();
    private readonly List<GpuModel> _models = new();
    private readonly Queue<GpuModel> _waiting = new();
    private readonly List<Instance> _instances = new();
    // The objects the scene's structure was last built with.
    private readonly List<Instance> _sceneInstances = new();
    private readonly List<(ulong Fence, Action Release)> _retired = new();
    private readonly (ID3D12Resource? Buffer, int Capacity)[] _instanceBuffers = new (ID3D12Resource?, int)[FramesInFlight];
    // Size of each structure built, once packed: written by the graphics card, read back when its frame is done, then
    // the structure copied into that smaller room.
    private readonly ID3D12Resource _packedSizes, _packedSizesRead;
    private readonly ulong* _packedSizesData;
    private readonly List<RayModel>[] _built = new List<RayModel>[FramesInFlight];
    private ID3D12Resource? _scratch, _topScratch, _top;
    private ulong _fenceValue;
    private int _frame;

    // The images shared with DirectX 11: start and direction of each pixel's reflected ray, the colour found.
    private ID3D12Resource? _starts, _directions, _output;
    private ID3D11Texture2D? _starts11, _directions11, _output11;
    private ID3D11RenderTargetView? _startsWrite, _directionsWrite;
    private ID3D11ShaderResourceView? _outputRead;
    private int _width, _height;

    private RayTracedReflections(ID3D11Device device11, ID3D11DeviceContext4 context, ID3D12Device5 device, byte[] shader)
    {
        _device11 = device11;
        _context = context;
        _device = device;
        _queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        for (int i = 0; i < FramesInFlight; i++)
            _allocators[i] = device.CreateCommandAllocator(CommandListType.Direct);
        _list = device.CreateCommandList<ID3D12GraphicsCommandList4>(0, CommandListType.Direct, _allocators[0], null);
        _list.Close();
        _fence = device.CreateFence(0, D12.FenceFlags.Shared);
        var fenceHandle = device.CreateSharedHandle(_fence, null, null!);
        try
        {
            using var device5 = device11.QueryInterface<ID3D11Device5>();
            _fence11 = device5.OpenSharedFence(fenceHandle);
        }
        finally
        {
            CloseHandle(fenceHandle);
        }
        _rootSignature = device.CreateRootSignature(0, shader);
        _pipeline = device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = _rootSignature, ComputeShader = shader });
        _heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            3 + MaxArenas, DescriptorHeapFlags.ShaderVisible));
        _descriptorSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        for (int i = 0; i < MaxArenas; i++)
            device.CreateShaderResourceView(null, RawView(1), Descriptor(3 + i));

        _table = device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)TableSlots * GeometryEntrySize), ResourceStates.GenericRead, null);
        void* data;
        _table.Map(0, null, &data).CheckError();
        _tableData = (GeometryEntry*)data;

        ulong sizesLength = FramesInFlight * MaxModelsPerFrame * 8;
        _packedSizes = Buffer((long)sizesLength, HeapType.Default, ResourceStates.UnorderedAccess, ResourceFlags.AllowUnorderedAccess);
        _packedSizesRead = device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
            ResourceDescription.Buffer(sizesLength), ResourceStates.CopyDest, null);
        _packedSizesRead.Map(0, null, &data).CheckError();
        _packedSizesData = (ulong*)data;
        for (int i = 0; i < FramesInFlight; i++)
            _built[i] = new List<RayModel>();
    }

    // The graphics card's name of what failed, when the ray tracing cannot be used.
    public static RayTracedReflections? TryCreate(ID3D11Device device, ID3D11DeviceContext context, out string? problem)
    {
        problem = null;
        ID3D12Device5? device12 = null;
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            if (D3D12.D3D12CreateDevice(adapter, Vortice.Direct3D.FeatureLevel.Level_12_0, out device12).Failure || device12 is null)
            {
                problem = "DirectX 12";
                return null;
            }
            if (device12.Options5.RaytracingTier < RaytracingTier.Tier1_1)
            {
                problem = "DXR 1.1";
                device12.Dispose();
                return null;
            }
            if (CompileShader(out problem) is not { } shader)
            {
                device12.Dispose();
                return null;
            }
            problem = null;
            var context4 = context.QueryInterface<ID3D11DeviceContext4>();
            return new RayTracedReflections(device, context4, device12, shader);
        }
        catch (Exception ex)
        {
            device12?.Dispose();
            problem = ex.Message;
            return null;
        }
    }

    // The ray tracing shader (DirectX 12 shader model 6.5), or null with the compiler's errors.
    internal static byte[]? CompileShader(out string? errors)
    {
        using var compiled = DxcCompiler.Compile(ShaderSource, ["-T", "cs_6_5", "-E", "main", "-O3"], null);
        errors = compiled.GetErrors();
        return compiled.GetStatus().Failure ? null : compiled.GetObjectBytecodeArray();
    }

    // Bytes taken on the graphics card by the models and their acceleration structures.
    public long ByteSize => (_geometryArenas.Count * GeometryArenaSize) + (_structureArenas.Count * StructureArenaSize);

    // Models ready to be mirrored, and waiting to be sent.
    public int ReadyModelCount { get; private set; }
    public int WaitingModelCount => _waiting.Count;

    // Most bytes the models may take (beyond: the following ones are not mirrored).
    public long MemoryLimit { get; set; } = 1536L << 20;

    // Starts the list of objects to mirror (Add); without it, the objects of the previous frame are mirrored again.
    public void BeginObjects() => _instances.Clear();

    // An object to mirror (models loaded with their ray geometry, see ModelRenderer.Prepare).
    public void Add(GpuModel model, Matrix4x4 world)
    {
        if (model.IsDisposed)
            return;
        if (model.RayState is RayModel ray)
        {
            if (ray.Built)
                _instances.Add(new Instance(ray, world, ray.Address));
            return;
        }
        if (model.RayGeometry is null)
            return;
        model.RayState = new RayModel();
        _models.Add(model);
        _waiting.Enqueue(model);
    }

    // The DirectX 11 images the water and wet ground write their reflected rays into (start from the camera, w = 1;
    // direction), cleared (no ray anywhere), at the image's size.
    public (ID3D11RenderTargetView Starts, ID3D11RenderTargetView Directions) Targets(int width, int height)
    {
        if (_starts is null || width != _width || height != _height)
            CreateImages(width, height);
        _context.ClearRenderTargetView(_startsWrite!, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
        return (_startsWrite!, _directionsWrite!);
    }

    // Follows the reflected rays written into Targets; the colour found (alpha 0: nothing, the sky), or null when
    // nothing could be traced this frame.
    public ID3D11ShaderResourceView? Trace(SceneLighting lighting, Vector3 camera, float longestRay, bool shadows)
    {
        // Wait for what DirectX 11 drew into the images.
        ulong drawn = ++_fenceValue;
        _context.Signal(_fence11, drawn);
        _context.Flush();

        int frame = _frame++ % FramesInFlight;
        WaitFor(_allocatorFence[frame]);
        Release(_fence.CompletedValue);
        if (_frame % 30 == 0)
            Forget();

        var allocator = _allocators[frame];
        allocator.Reset();
        _list.Reset(allocator, null);
        Pack(frame);
        BuildWaitingModels(frame);

        // Objects released since, or whose structure was packed.
        _instances.RemoveAll(i => !i.Model.Built);
        for (int i = 0; i < _instances.Count; i++)
        {
            if (_instances[i].Address != _instances[i].Model.Address)
                _instances[i] = _instances[i] with { Address = _instances[i].Model.Address };
        }
        bool traced = false;
        if (_instances.Count > 0)
        {
            if (_top is null || !CollectionsMarshal.AsSpan(_instances).SequenceEqual(CollectionsMarshal.AsSpan(_sceneInstances)))
            {
                BuildScene(frame);
                _sceneInstances.Clear();
                _sceneInstances.AddRange(_instances);
            }
            Constants constants = new()
            {
                SunDirection = new Vector4(lighting.SunDirection, shadows ? 1f : 0f),
                SunColor = new Vector4(lighting.SunColor, 0f),
                SkyAmbient = new Vector4(lighting.SkyAmbient, 0f),
                GroundAmbient = new Vector4(lighting.GroundAmbient, 0f),
                Horizon = new Vector4(lighting.Horizon, 0f),
                Fog = new Vector4(lighting.FogDistance > 0f ? 1f : 0f, MathF.Max(lighting.FogDistance, 1f), longestRay, 0f),
                Camera = new Vector4(camera, 0f),
                Width = (uint)_width,
                Height = (uint)_height,
            };
            _list.SetComputeRootSignature(_rootSignature);
            _list.SetPipelineState(_pipeline);
            _list.SetDescriptorHeaps(_heap);
            _list.SetComputeRoot32BitConstants(0, 32, &constants, 0);
            _list.SetComputeRootShaderResourceView(1, _top!.GPUVirtualAddress);
            _list.SetComputeRootShaderResourceView(2, _table.GPUVirtualAddress);
            _list.SetComputeRootDescriptorTable(3, GpuDescriptor(0));
            _list.SetComputeRootDescriptorTable(4, GpuDescriptor(3));
            _list.Dispatch((uint)(_width + 7) / 8, (uint)(_height + 7) / 8, 1);
            traced = true;
        }
        _list.Close();

        _queue.Wait(_fence, drawn);
        _queue.ExecuteCommandList(_list);
        ulong done = ++_fenceValue;
        _queue.Signal(_fence, done);
        _allocatorFence[frame] = done;
        _context.Wait(_fence11, done);
        return traced ? _outputRead : null;
    }

    // The structures built by this frame slot's previous frame (now finished) copied into the room they need once packed
    // (often half).
    private void Pack(int frame)
    {
        var built = _built[frame];
        bool packed = false;
        for (int i = 0; i < built.Count; i++)
        {
            var ray = built[i];
            long size = (long)_packedSizesData[frame * MaxModelsPerFrame + i];
            if (!ray.Built || size <= 0 || size > ray.StructureLength * 9 / 10)
                continue;
            if (Allocate(_structureArenas, size, 256, StructureArenaSize, geometry: false) is not { } place)
                continue;
            ulong address = _structureArenas[place.Arena].Buffer.GPUVirtualAddress + (ulong)place.Offset;
            _list.CopyRaytracingAccelerationStructure(address, ray.Address, RaytracingAccelerationStructureCopyMode.Compact);
            var (oldArena, oldOffset, oldLength) = (_structureArenas[ray.StructureArena], ray.StructureOffset, ray.StructureLength);
            Retire(() => oldArena.Space.Free(oldOffset, oldLength));
            ray.StructureArena = place.Arena;
            ray.StructureOffset = place.Offset;
            ray.StructureLength = size;
            ray.Address = address;
            packed = true;
        }
        built.Clear();
        if (packed)
            _list.ResourceBarrierUnorderedAccessView(null!);
    }

    // Sends a few waiting models: their positions and indices, then their acceleration structures.
    private void BuildWaitingModels(int frame)
    {
        var batch = new List<(GpuModel Model, RayModel Ray, BuildRaytracingAccelerationStructureInputs Inputs, long Scratch, long Upload)>();
        long triangles = 0, scratchSize = 0, uploadSize = 0;
        while (_waiting.Count > 0 && batch.Count < MaxModelsPerFrame && triangles < MaxTrianglesPerFrame)
        {
            var model = _waiting.Peek();
            if (model.IsDisposed || model.RayGeometry is not { } geometry || model.RayState is not RayModel ray)
            {
                _waiting.Dequeue();
                continue;
            }
            var parts = model.Parts;
            var kept = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].BlendMode is MeshBlendMode.Opaque or MeshBlendMode.Cutout && !parts[i].Character && parts[i].IndexCount >= 3)
                    kept.Add(i);
            }
            if (kept.Count == 0)
            {
                _waiting.Dequeue();
                ray.Skipped = true;
                model.RayGeometry = null;
                continue;
            }

            long positionBytes = geometry.Positions.Length * 12L;
            long length = positionBytes + (geometry.Indices.Length * 2L + 3) / 4 * 4;
            if (Allocate(_geometryArenas, length, 16, GeometryArenaSize, geometry: true) is not { } place)
                break; // Out of room: tried again later.
            if (_slots.Allocate(kept.Count) is not { } firstSlot)
            {
                _geometryArenas[place.Arena].Space.Free(place.Offset, length);
                break;
            }
            ulong baseAddress = _geometryArenas[place.Arena].Buffer.GPUVirtualAddress + (ulong)place.Offset;
            var descriptions = new RaytracingGeometryDescription[kept.Count];
            for (int k = 0; k < kept.Count; k++)
            {
                var part = parts[kept[k]];
                int end = geometry.Positions.Length;
                foreach (var other in parts)
                {
                    if (other.BaseVertex > part.BaseVertex && other.BaseVertex < end)
                        end = other.BaseVertex;
                }
                descriptions[k] = new RaytracingGeometryDescription(new RaytracingGeometryTrianglesDescription(
                    new GpuVirtualAddressAndStride(baseAddress + (ulong)part.BaseVertex * 12, 12), Format.R32G32B32_Float, (uint)(end - part.BaseVertex),
                    0, baseAddress + (ulong)positionBytes + (ulong)part.FirstIndex * 2, Format.R16_UInt, (uint)part.IndexCount),
                    RaytracingGeometryFlags.Opaque);
                var color = part.Texture?.AverageColor ?? new Vector4(0.6f, 0.6f, 0.6f, 1f);
                _tableData[firstSlot + k] = new GeometryEntry
                {
                    Arena = (uint)place.Arena,
                    Positions = (uint)(place.Offset + part.BaseVertex * 12L),
                    Indices = (uint)(place.Offset + positionBytes + part.FirstIndex * 2L),
                    Color = new Vector4(MathF.Pow(color.X, 2.2f), MathF.Pow(color.Y, 2.2f), MathF.Pow(color.Z, 2.2f), 1f),
                };
                triangles += part.IndexCount / 3;
            }
            var inputs = new BuildRaytracingAccelerationStructureInputs
            {
                Type = RaytracingAccelerationStructureType.BottomLevel,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace | RaytracingAccelerationStructureBuildFlags.AllowCompaction,
                Layout = ElementsLayout.Array,
                DescriptorsCount = (uint)descriptions.Length,
                GeometryDescriptions = descriptions,
            };
            var info = _device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
            long structureLength = (long)info.ResultDataMaxSizeInBytes;
            if (Allocate(_structureArenas, structureLength, 256, StructureArenaSize, geometry: false) is not { } structure)
            {
                _geometryArenas[place.Arena].Space.Free(place.Offset, length);
                _slots.Free(firstSlot, kept.Count);
                break;
            }
            _waiting.Dequeue();
            ray.GeometryArena = place.Arena;
            ray.GeometryOffset = place.Offset;
            ray.GeometryLength = length;
            ray.StructureArena = structure.Arena;
            ray.StructureOffset = structure.Offset;
            ray.StructureLength = structureLength;
            ray.FirstSlot = (int)firstSlot;
            ray.SlotCount = kept.Count;
            ray.Address = _structureArenas[structure.Arena].Buffer.GPUVirtualAddress + (ulong)structure.Offset;
            batch.Add((model, ray, inputs, scratchSize, uploadSize));
            scratchSize += ((long)info.ScratchDataSizeInBytes + 255) / 256 * 256;
            uploadSize += (length + 15) / 16 * 16;
        }
        if (batch.Count == 0)
            return;

        // Positions and indices through a buffer the processor writes into.
        var upload = _device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)uploadSize), ResourceStates.GenericRead, null);
        Retire(() => upload.Dispose());
        byte* data;
        upload.Map(0, null, (void**)&data).CheckError();
        foreach (var (model, ray, _, _, at) in batch)
        {
            var geometry = model.RayGeometry!;
            long positionBytes = geometry.Positions.Length * 12L;
            geometry.Positions.AsSpan().CopyTo(new Span<Vector3>(data + at, geometry.Positions.Length));
            geometry.Indices.AsSpan().CopyTo(new Span<ushort>(data + at + positionBytes, geometry.Indices.Length));
            var arena = _geometryArenas[ray.GeometryArena];
            _list.CopyBufferRegion(arena.Buffer, (ulong)ray.GeometryOffset, upload, (ulong)at, (ulong)ray.GeometryLength);
            arena.Written = true;
            model.RayGeometry = null;
        }
        upload.Unmap(0);
        foreach (var arena in _geometryArenas)
        {
            if (arena.Written)
            {
                _list.ResourceBarrierTransition(arena.Buffer, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
                arena.Written = false;
            }
        }

        if (_scratch is null || (long)_scratch.Description.Width < scratchSize)
        {
            var old = _scratch;
            if (old is not null)
                Retire(() => old.Dispose());
            _scratch = Buffer(Math.Max(scratchSize, 16L << 20), HeapType.Default, ResourceStates.Common, ResourceFlags.AllowUnorderedAccess);
        }
        ulong sizesAt = (ulong)(frame * MaxModelsPerFrame * 8);
        foreach (var (_, ray, inputs, scratch, _) in batch)
        {
            var packedSize = new RaytracingAccelerationStructurePostBuildInfoDescription
            {
                DestBuffer = _packedSizes.GPUVirtualAddress + sizesAt + (ulong)(_built[frame].Count * 8),
                InfoType = RaytracingAccelerationStructurePostBuildInfoType.CompactedSize,
            };
            _list.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(ray.Address, inputs, 0,
                _scratch.GPUVirtualAddress + (ulong)scratch), [packedSize]);
            _built[frame].Add(ray);
            ray.Built = true;
        }
        _list.ResourceBarrierUnorderedAccessView(null!);
        _list.ResourceBarrierTransition(_packedSizes, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _list.CopyBufferRegion(_packedSizesRead, sizesAt, _packedSizes, sizesAt, (ulong)(batch.Count * 8));
        _list.ResourceBarrierTransition(_packedSizes, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        ReadyModelCount += batch.Count;
    }

    // The objects of this frame together, then their structure built.
    private void BuildScene(int frame)
    {
        var (buffer, capacity) = _instanceBuffers[frame];
        if (buffer is null || capacity < _instances.Count)
        {
            var old = buffer;
            if (old is not null)
                Retire(() => old.Dispose());
            capacity = Math.Max(_instances.Count * 3 / 2, 4096);
            buffer = _device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                ResourceDescription.Buffer((ulong)(capacity * sizeof(RaytracingInstanceDescription))), ResourceStates.GenericRead, null);
            _instanceBuffers[frame] = (buffer, capacity);
        }
        RaytracingInstanceDescription* instances;
        buffer.Map(0, null, (void**)&instances).CheckError();
        for (int i = 0; i < _instances.Count; i++)
        {
            var (ray, m, address) = _instances[i];
            instances[i] = new RaytracingInstanceDescription
            {
                Transform = new Vortice.Mathematics.Matrix3x4(m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43),
                InstanceID = new Vortice.UInt24((uint)ray.FirstSlot),
                InstanceMask = 0xFF,
                AccelerationStructure = address,
            };
        }
        buffer.Unmap(0);

        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastBuild,
            Layout = ElementsLayout.Array,
            DescriptorsCount = (uint)_instances.Count,
            InstanceDescriptions = buffer.GPUVirtualAddress,
        };
        var info = _device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        if (_top is null || _top.Description.Width < info.ResultDataMaxSizeInBytes)
        {
            var old = _top;
            if (old is not null)
                Retire(() => old.Dispose());
            _top = Buffer((long)info.ResultDataMaxSizeInBytes * 3 / 2, HeapType.Default, ResourceStates.RaytracingAccelerationStructure,
                ResourceFlags.AllowUnorderedAccess);
        }
        if (_topScratch is null || _topScratch.Description.Width < info.ScratchDataSizeInBytes)
        {
            var old = _topScratch;
            if (old is not null)
                Retire(() => old.Dispose());
            _topScratch = Buffer((long)info.ScratchDataSizeInBytes * 3 / 2, HeapType.Default, ResourceStates.Common, ResourceFlags.AllowUnorderedAccess);
        }
        _list.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(_top.GPUVirtualAddress, inputs, 0,
            _topScratch.GPUVirtualAddress));
        _list.ResourceBarrierUnorderedAccessView(_top);
    }

    // Room for length bytes in one of the arenas (a new one made when needed and allowed).
    private (int Arena, long Offset)? Allocate(List<Arena> arenas, long length, long alignment, long arenaSize, bool geometry)
    {
        for (int i = 0; i < arenas.Count; i++)
        {
            if (arenas[i].Space.Allocate(length, alignment) is { } offset)
                return (i, offset);
        }
        long size = Math.Max(arenaSize, (length + arenaSize - 1) / arenaSize * arenaSize);
        if (arenas.Count >= MaxArenas || ByteSize + size > MemoryLimit)
            return null;
        var buffer = geometry
            ? Buffer(size, HeapType.Default, ResourceStates.Common)
            : Buffer(size, HeapType.Default, ResourceStates.RaytracingAccelerationStructure, ResourceFlags.AllowUnorderedAccess);
        var arena = new Arena(buffer, size);
        arenas.Add(arena);
        if (geometry)
            _device.CreateShaderResourceView(buffer, RawView(size / 4), Descriptor(3 + arenas.Count - 1));
        return (arenas.Count - 1, arena.Space.Allocate(length, alignment)!.Value);
    }

    // Models released by the map: their room is given back once the graphics card no longer reads it.
    private void Forget()
    {
        for (int i = _models.Count - 1; i >= 0; i--)
        {
            var model = _models[i];
            if (!model.IsDisposed)
                continue;
            _models[i] = _models[^1];
            _models.RemoveAt(_models.Count - 1);
            if (model.RayState is not RayModel ray || ray.FirstSlot < 0)
                continue;
            if (ray.Built)
                ReadyModelCount--;
            ray.Built = false;
            var (geometryArena, structureArena) = (_geometryArenas[ray.GeometryArena], _structureArenas[ray.StructureArena]);
            Retire(() =>
            {
                geometryArena.Space.Free(ray.GeometryOffset, ray.GeometryLength);
                structureArena.Space.Free(ray.StructureOffset, ray.StructureLength);
                _slots.Free(ray.FirstSlot, ray.SlotCount);
            });
        }
    }

    // Done once the frame being recorded is finished on the graphics card.
    private void Retire(Action release) => _retired.Add((_fenceValue + 1, release));

    private void Release(ulong completed)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Fence > completed)
                continue;
            _retired[i].Release();
            _retired.RemoveAt(i);
        }
    }

    private void WaitFor(ulong value)
    {
        if (_fence.CompletedValue >= value)
            return;
        using var done = new AutoResetEvent(false);
        _fence.SetEventOnCompletion(value, done.SafeWaitHandle.DangerousGetHandle()).CheckError();
        done.WaitOne();
    }

    private void CreateImages(int width, int height)
    {
        // Still read by frames in flight: released after them.
        var old = (_starts, _directions, _output, _starts11, _directions11, _output11, _startsWrite, _directionsWrite, _outputRead);
        Retire(() =>
        {
            old._startsWrite?.Dispose();
            old._directionsWrite?.Dispose();
            old._outputRead?.Dispose();
            old._starts11?.Dispose();
            old._directions11?.Dispose();
            old._output11?.Dispose();
            old._starts?.Dispose();
            old._directions?.Dispose();
            old._output?.Dispose();
        });
        _width = width;
        _height = height;
        // DirectX 11 cannot open shared images of 32-bit floats: the starts are kept from the camera, in 16 bits.
        _starts = SharedImage(Format.R16G16B16A16_Float);
        _directions = SharedImage(Format.R16G16B16A16_Float);
        _output = SharedImage(Format.R16G16B16A16_Float);
        _starts11 = Open(_starts);
        _directions11 = Open(_directions);
        _output11 = Open(_output);
        _startsWrite = _device11.CreateRenderTargetView(_starts11);
        _directionsWrite = _device11.CreateRenderTargetView(_directions11);
        _outputRead = _device11.CreateShaderResourceView(_output11);

        _device.CreateShaderResourceView(_starts, null, Descriptor(0));
        _device.CreateShaderResourceView(_directions, null, Descriptor(1));
        _device.CreateUnorderedAccessView(_output, null, new D12.UnorderedAccessViewDescription
        {
            Format = Format.R16G16B16A16_Float,
            ViewDimension = D12.UnorderedAccessViewDimension.Texture2D,
        }, Descriptor(2));

        // Shared only with both of these flags.
        ID3D12Resource SharedImage(Format format) => _device.CreateCommittedResource(new HeapProperties(HeapType.Default),
            HeapFlags.Shared, ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, 1, 1, 0,
                ResourceFlags.AllowRenderTarget | ResourceFlags.AllowUnorderedAccess | ResourceFlags.AllowSimultaneousAccess),
            ResourceStates.Common, null);
    }

    private ID3D11Texture2D Open(ID3D12Resource resource)
    {
        var handle = _device.CreateSharedHandle(resource, null, null!);
        try
        {
            using var device5 = _device11.QueryInterface<ID3D11Device5>();
            return device5.OpenSharedResource1<ID3D11Texture2D>(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private ID3D12Resource Buffer(long size, HeapType heap, ResourceStates state, ResourceFlags flags = ResourceFlags.None) =>
        _device.CreateCommittedResource(new HeapProperties(heap), HeapFlags.None, ResourceDescription.Buffer((ulong)Math.Max(size, 256), flags), state, null);

    private static D12.ShaderResourceViewDescription RawView(long words) => new()
    {
        Format = Format.R32_Typeless,
        ViewDimension = D12.ShaderResourceViewDimension.Buffer,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Buffer = new D12.BufferShaderResourceView { FirstElement = 0, NumElements = (uint)words, Flags = D12.BufferShaderResourceViewFlags.Raw },
    };

    private CpuDescriptorHandle Descriptor(int index) => _heap.GetCPUDescriptorHandleForHeapStart() + index * (int)_descriptorSize;

    private GpuDescriptorHandle GpuDescriptor(int index) => _heap.GetGPUDescriptorHandleForHeapStart() + index * (int)_descriptorSize;

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public void Dispose()
    {
        try
        {
            // Everything sent must be finished before anything is released.
            ulong last = ++_fenceValue;
            _queue.Signal(_fence, last);
            WaitFor(last);
        }
        catch
        {
            // Device lost: nothing more runs on it.
        }
        Release(ulong.MaxValue);
        foreach (var model in _models)
            model.RayState = null;
        _models.Clear();
        _waiting.Clear();
        _startsWrite?.Dispose();
        _directionsWrite?.Dispose();
        _outputRead?.Dispose();
        _starts11?.Dispose();
        _directions11?.Dispose();
        _output11?.Dispose();
        _starts?.Dispose();
        _directions?.Dispose();
        _output?.Dispose();
        foreach (var (buffer, _) in _instanceBuffers)
            buffer?.Dispose();
        _scratch?.Dispose();
        _topScratch?.Dispose();
        _top?.Dispose();
        foreach (var arena in _geometryArenas.Concat(_structureArenas))
            arena.Buffer.Dispose();
        _table.Unmap(0);
        _table.Dispose();
        _packedSizesRead.Unmap(0);
        _packedSizesRead.Dispose();
        _packedSizes.Dispose();
        _heap.Dispose();
        _pipeline.Dispose();
        _rootSignature.Dispose();
        _list.Dispose();
        foreach (var allocator in _allocators)
            allocator.Dispose();
        _fence.Dispose();
        _fence11.Dispose();
        _queue.Dispose();
        _device.Dispose();
        _context.Dispose();
    }
}
