//
// Copyright (c) 2026-2026 Drehd
//
// This file is part of MemoryEngine360.
//
// MemoryEngine360 is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either
// version 3.0 of the License, or (at your option) any later version.
//
// MemoryEngine360 is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with MemoryEngine360. If not, see <https://www.gnu.org/licenses/>.
//

using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MemEngine360.PS4.PSRPC;

/// <summary>
/// An async client for the PSRPC PRX's wire protocol (PRX/libc/rpc.h). Each call sends one request
/// and waits for its response, so callers must not overlap calls; the connection's busy token sees to that.
/// <para>
/// PS4 is little-endian x86-64, so nothing in this protocol is byte swapped
/// </para>
/// </summary>
public sealed class PsRpcClient {
    public const int DefaultPort = 9157;  // RPC_PORT in rpc.h
    public const uint Magic = 0xE35F00AD; // RPC_MAGIC in rpc.h

    /// <summary>
    /// The most bytes moved per request. The PRX allocates the whole transfer in one block,
    /// so large reads and writes are split instead of asking it for one huge allocation
    /// </summary>
    public const int MaxTransferSize = 0x100000;

    private const int RequestHeaderSize = 36;
    private const int ResponseHeaderSize = 20;
    private const int RegionWireSize = 56;
    private const uint RegionBatchSize = 1024;

    private readonly Socket socket;
    private readonly byte[] responseHeader = new byte[ResponseHeaderSize];

    public EndPoint EndPoint { get; }

    public int TimeoutMilliseconds { get; set; } = 10000;

    public PsRpcClient(Socket socket, EndPoint endPoint) {
        this.socket = socket;
        this.socket.NoDelay = true;
        this.EndPoint = endPoint;
    }

    public void Disconnect() {
        try {
            this.socket.Shutdown(SocketShutdown.Both);
        }
        catch {
            // ignored
        }

        this.socket.Dispose();
    }

    /// <summary>
    /// Gets the base address of the first executable segment with the given name. An empty name
    /// gets the game's main executable. Returns 0 when nothing matched
    /// </summary>
    public async Task<ulong> GetModuleBase(string moduleName) {
        byte[] name = Encoding.ASCII.GetBytes(moduleName + "\0");
        Response response = await this.Transact(PsRpcOpcode.GetModule, PsRpcType.UInt64, 0, 0, name, Memory<byte>.Empty).ConfigureAwait(false);
        return response.ReturnValue;
    }

    /// <summary>
    /// Returns true when the PRX implements the memory-map opcodes (<see cref="PsRpcOpcode.VirtualQuery"/>
    /// and the safe read/write). Older builds answer them with <see cref="PsRpcStatus.UnknownOpcode"/>
    /// </summary>
    public async Task<bool> SupportsSafeMemoryAccess() {
        byte[] buffer = new byte[RegionWireSize];
        try {
            await this.Transact(PsRpcOpcode.VirtualQuery, PsRpcType.Void, 0, 1, ReadOnlyMemory<byte>.Empty, buffer).ConfigureAwait(false);
            return true;
        }
        catch (PsRpcStatusException e) when (e.Status == PsRpcStatus.UnknownOpcode) {
            return false;
        }
    }

    /// <summary>
    /// Reads memory with a bare memcpy on the console. Unmapped memory crashes the game,
    /// so prefer <see cref="ReadMemorySafe"/> when the PRX supports it
    /// </summary>
    public async Task ReadMemory(ulong address, Memory<byte> dst) {
        Response response = await this.Transact(PsRpcOpcode.ReadMemory, PsRpcType.ByteArray, address, (uint) dst.Length, ReadOnlyMemory<byte>.Empty, dst).ConfigureAwait(false);
        EnsurePayloadLength(response, dst.Length);
    }

    /// <summary>
    /// Reads memory, with bytes that are unmapped or not CPU-readable coming back as 0
    /// </summary>
    /// <returns>The number of bytes that were really read</returns>
    public async Task<int> ReadMemorySafe(ulong address, Memory<byte> dst) {
        Response response = await this.Transact(PsRpcOpcode.ReadMemorySafe, PsRpcType.ByteArray, address, (uint) dst.Length, ReadOnlyMemory<byte>.Empty, dst).ConfigureAwait(false);
        EnsurePayloadLength(response, dst.Length);
        return (int) response.ReturnValue;
    }

    /// <summary>
    /// Writes memory with a bare memcpy on the console. Unmapped or read-only memory crashes
    /// the game, so prefer <see cref="WriteMemorySafe"/> when the PRX supports it
    /// </summary>
    public Task WriteMemory(ulong address, ReadOnlyMemory<byte> src) {
        return this.Transact(PsRpcOpcode.WriteMemory, PsRpcType.Void, address, 0, src, Memory<byte>.Empty);
    }

    /// <summary>
    /// Writes memory. The PRX refuses with <see cref="PsRpcStatus.NotWritable"/> unless
    /// the whole range is mapped CPU-writable
    /// </summary>
    public Task WriteMemorySafe(ulong address, ReadOnlyMemory<byte> src) {
        return this.Transact(PsRpcOpcode.WriteMemorySafe, PsRpcType.Void, address, 0, src, Memory<byte>.Empty);
    }

    /// <summary>
    /// Gets the memory map regions that overlap <c>[startAddress, endAddress)</c>, in address order
    /// </summary>
    public async Task<List<PsRpcRegion>> GetMemoryRegions(ulong startAddress, ulong endAddress) {
        List<PsRpcRegion> regions = new List<PsRpcRegion>();
        byte[] buffer = new byte[RegionBatchSize * RegionWireSize];
        ulong next = startAddress;
        while (true) {
            Response response = await this.Transact(PsRpcOpcode.VirtualQuery, PsRpcType.Void, next, RegionBatchSize, ReadOnlyMemory<byte>.Empty, buffer).ConfigureAwait(false);
            int count = (int) response.ReturnValue;
            if (count * RegionWireSize != response.PayloadLength) {
                throw new IOException("PSRPC sent a malformed memory map");
            }

            for (int i = 0; i < count; i++) {
                PsRpcRegion region = PsRpcRegion.Read(buffer.AsSpan(i * RegionWireSize, RegionWireSize));
                if (region.Start >= endAddress) {
                    return regions;
                }

                regions.Add(region);
            }

            // A short batch is the end of the map. The End check guards against a PRX
            // that hands back the same region forever
            if (count < RegionBatchSize || regions[^1].End <= next) {
                return regions;
            }

            next = regions[^1].End;
        }
    }

    private static void EnsurePayloadLength(Response response, int expected) {
        if (response.PayloadLength != expected) {
            throw new IOException($"PSRPC sent {response.PayloadLength} bytes when {expected} were requested");
        }
    }

    private async Task<Response> Transact(PsRpcOpcode opcode, PsRpcType returnType, ulong address, uint arraySize, ReadOnlyMemory<byte> payload, Memory<byte> responsePayload) {
        using CancellationTokenSource cts = new CancellationTokenSource(this.TimeoutMilliseconds);
        try {
            int cbRequest = RequestHeaderSize + payload.Length;
            byte[] request = ArrayPool<byte>.Shared.Rent(cbRequest);
            try {
                Span<byte> header = request.AsSpan(0, RequestHeaderSize);
                BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint) opcode);
                BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint) returnType);
                BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0); // Flags
                BinaryPrimitives.WriteUInt64LittleEndian(header[16..], address);
                BinaryPrimitives.WriteUInt32LittleEndian(header[24..], arraySize);
                BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 0); // ArgCount
                BinaryPrimitives.WriteUInt32LittleEndian(header[32..], (uint) payload.Length);
                payload.Span.CopyTo(request.AsSpan(RequestHeaderSize));

                await this.SendExact(request.AsMemory(0, cbRequest), cts.Token).ConfigureAwait(false);
            }
            finally {
                ArrayPool<byte>.Shared.Return(request);
            }

            await this.ReceiveExact(this.responseHeader, cts.Token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(this.responseHeader) != Magic) {
                throw new IOException("PSRPC response had an invalid magic");
            }

            PsRpcStatus status = (PsRpcStatus) BinaryPrimitives.ReadUInt32LittleEndian(this.responseHeader.AsSpan(4));
            ulong returnValue = BinaryPrimitives.ReadUInt64LittleEndian(this.responseHeader.AsSpan(8));
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(this.responseHeader.AsSpan(16));
            if (payloadLength > responsePayload.Length) {
                // Nothing sane fits here, and the bytes still on the wire leave the stream out of sync
                throw new IOException($"PSRPC sent an unexpected {payloadLength} byte response to {opcode}");
            }

            if (payloadLength > 0) {
                await this.ReceiveExact(responsePayload[..(int) payloadLength], cts.Token).ConfigureAwait(false);
            }

            if (status != PsRpcStatus.Ok) {
                throw new PsRpcStatusException(opcode, status);
            }

            return new Response(returnValue, (int) payloadLength);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) {
            throw new TimeoutException("Timed out waiting for the PS4 to respond");
        }
        catch (SocketException e) {
            throw new IOException("Socket error: " + e.Message, e);
        }
    }

    private async ValueTask SendExact(ReadOnlyMemory<byte> buffer, CancellationToken cancellation) {
        while (buffer.Length > 0) {
            int sent = await this.socket.SendAsync(buffer, SocketFlags.None, cancellation).ConfigureAwait(false);
            if (sent <= 0) {
                throw new IOException("Connection closed by the PS4");
            }

            buffer = buffer[sent..];
        }
    }

    private async ValueTask ReceiveExact(Memory<byte> buffer, CancellationToken cancellation) {
        while (buffer.Length > 0) {
            int received = await this.socket.ReceiveAsync(buffer, SocketFlags.None, cancellation).ConfigureAwait(false);
            if (received <= 0) {
                throw new IOException("Connection closed by the PS4");
            }

            buffer = buffer[received..];
        }
    }

    private readonly struct Response(ulong returnValue, int payloadLength) {
        public ulong ReturnValue { get; } = returnValue;

        public int PayloadLength { get; } = payloadLength;
    }
}

/// <summary>
/// One region of the game process' memory map. Mirrors PSRPCRegion in rpc.h
/// </summary>
public readonly struct PsRpcRegion(ulong start, ulong end, uint protection, string name) {
    public const uint ProtRead = 0x1, ProtWrite = 0x2, ProtExec = 0x4;

    public ulong Start { get; } = start;

    /// <summary>
    /// One past the region's last byte
    /// </summary>
    public ulong End { get; } = end;

    /// <summary>
    /// SceKernelVirtualQueryInfo.protection. CPU bits are <see cref="ProtRead"/>, <see cref="ProtWrite"/>
    /// and <see cref="ProtExec"/>; 0x10/0x20 are GPU read/write, which the CPU cannot touch
    /// </summary>
    public uint Protection { get; } = protection;

    public string Name { get; } = name;

    public ulong Size => this.End - this.Start;

    // Writable memory is readable on x86, so either bit means the CPU can read it
    public bool IsReadable => (this.Protection & (ProtRead | ProtWrite)) != 0;

    public bool IsWritable => (this.Protection & ProtWrite) != 0;

    public static PsRpcRegion Read(ReadOnlySpan<byte> data) {
        ReadOnlySpan<byte> name = data.Slice(24, 32);
        int nameLength = name.IndexOf((byte) 0);
        return new PsRpcRegion(
            BinaryPrimitives.ReadUInt64LittleEndian(data),
            BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            Encoding.ASCII.GetString(nameLength < 0 ? name : name[..nameLength]));
    }
}

/// <summary>
/// Thrown when the PRX answers a request with a non-zero status. The connection itself is still
/// in sync and usable, which is why this is not an <see cref="IOException"/>
/// </summary>
public class PsRpcStatusException(PsRpcOpcode opcode, PsRpcStatus status) : Exception(GetMessage(opcode, status)) {
    public PsRpcOpcode Opcode { get; } = opcode;

    public PsRpcStatus Status { get; } = status;

    private static string GetMessage(PsRpcOpcode opcode, PsRpcStatus status) {
        return status switch {
            PsRpcStatus.UnknownOpcode => $"The PSRPC PRX does not support {opcode}. Rebuild it from the latest source",
            PsRpcStatus.BadPayload => $"The PSRPC PRX rejected the {opcode} request",
            PsRpcStatus.NotWritable => "The memory is not mapped writable on the PS4",
            PsRpcStatus.OutOfMemory => $"The PSRPC PRX ran out of memory handling {opcode}",
            _ => $"The PSRPC PRX returned error status {(uint) status} for {opcode}"
        };
    }
}

// Opcodes, types and statuses MUST match PSRPC_Opcode, PSRPC_Type and PSRPCResponse.Status in rpc.h

public enum PsRpcOpcode : uint {
    Call = 1,
    ReadMemory = 2,
    WriteMemory = 3,
    GetModule = 4,
    Allocate = 5,
    Free = 6,
    Notify = 7,
    GameCall = 8,
    AllocExec = 9,
    FreeExec = 10,
    Reboot = 11,
    CrashGuard = 12,
    PadHook = 13,
    PadButtons = 14,
    NotifyIcon = 15,
    LoadModule = 16,
    VirtualQuery = 17,
    ReadMemorySafe = 18,
    WriteMemorySafe = 19,
}

public enum PsRpcType : uint {
    Void = 0,
    Int = 1,
    String = 2,
    Float = 3,
    Byte = 4,
    IntArray = 5,
    FloatArray = 6,
    ByteArray = 7,
    UInt64 = 8,
    UInt64Array = 9,
}

public enum PsRpcStatus : uint {
    Ok = 0,
    UnknownOpcode = 1,
    BadPayload = 2,
    NotWritable = 3,
    OutOfMemory = 4,
}
