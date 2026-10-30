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

using System.Diagnostics.CodeAnalysis;
using System.Net;
using MemEngine360.Connections;
using MemEngine360.Connections.Features;
using PFXToolKitUI.Utils.Events;

namespace MemEngine360.PS4.PSRPC;

/// <summary>
/// A connection to a PS4 game running the PSRPC PRX.
/// <para>
/// MemoryEngine addresses are 32-bit but the PS4 is 64-bit, so the connection exposes a 4 GB
/// window of the game's address space: MemoryEngine address <c>X</c> is PS4 address
/// <c><see cref="WindowBase"/> + X</c>. The window always starts on a 4 GB boundary, which makes a
/// MemoryEngine address simply the low 32 bits of the PS4 address, and lets 64-bit pointers that
/// point into the same window resolve the way 32-bit pointers do
/// </para>
/// </summary>
public class ConsoleConnectionPSRPC : BaseConsoleConnection, INetworkConsoleConnection {
    /// <summary>
    /// The size of the window MemoryEngine sees, which is all a 32-bit address can reach
    /// </summary>
    public const ulong WindowSize = 0x1_0000_0000;

    private readonly PsRpcClient client;
    private readonly FeaturesImpl features;

    public override RegisteredConnectionType ConnectionType => ConnectionTypePS4PSRPC.Instance;

    public override bool IsLittleEndian => true;

    public override AddressRange AddressableRange => new AddressRange(0, uint.MaxValue);

    public EndPoint? EndPoint => !this.IsClosed ? this.client.EndPoint : null;

    /// <summary>
    /// True when the PRX has the memory-map opcodes. Without them, reads and writes are a bare
    /// memcpy on the console, so touching unmapped memory crashes the game, and memory regions
    /// are unavailable
    /// </summary>
    public bool HasSafeMemoryAccess { get; }

    /// <summary>
    /// Gets or sets the PS4 address that MemoryEngine address 0 maps to. Setting it rounds down to
    /// a multiple of <see cref="WindowSize"/>. Scan results and saved addresses are relative to the
    /// window, so they point somewhere else once it moves
    /// </summary>
    public ulong WindowBase {
        get => field;
        set => PropertyHelper.SetAndRaiseINE(ref field, AlignToWindow(value), this, static t => {
            t.WindowBaseChanged?.Invoke(t, EventArgs.Empty);
            t.ConnectionType.RaiseConnectionStatusBarTextInvalidated(t);
        });
    }

    public event EventHandler? WindowBaseChanged;

    public ConsoleConnectionPSRPC(PsRpcClient client, bool hasSafeMemoryAccess, ulong windowBase) {
        this.client = client;
        this.HasSafeMemoryAccess = hasSafeMemoryAccess;
        this.WindowBase = windowBase;
        this.features = new FeaturesImpl(this);
    }

    public static ulong AlignToWindow(ulong address) => address & ~(WindowSize - 1);

    public override bool TryGetFeature<T>([NotNullWhen(true)] out T? feature) where T : class {
        if (this.HasSafeMemoryAccess && this.features is T t) {
            feature = t;
            return true;
        }

        return base.TryGetFeature(out feature);
    }

    public override bool HasFeature(Type typeOfFeature) {
        return (this.HasSafeMemoryAccess && typeOfFeature.IsInstanceOfType(this.features)) || base.HasFeature(typeOfFeature);
    }

    public override int GetRecommendedReadChunkSize(int readTotal) {
        return Math.Min(readTotal, PsRpcClient.MaxTransferSize);
    }

    protected override void CloseOverride() {
        this.client.Disconnect();
    }

    public override async Task<bool?> IsMemoryInvalidOrProtected(uint address, int count) {
        if (!this.HasSafeMemoryAccess) {
            return null;
        }

        if (count <= 0) {
            return false;
        }

        this.EnsureNotClosed();
        using BusyToken x = this.CreateBusyToken();

        ulong start = this.WindowBase + address, end = start + (ulong) count;
        List<PsRpcRegion> regions = await this.RunNetworkOperation(() => this.client.GetMemoryRegions(start, end)).ConfigureAwait(false);

        ulong covered = start;
        foreach (PsRpcRegion region in regions) {
            if (region.Start > covered || !region.IsReadable) {
                return true;
            }

            covered = region.End;
            if (covered >= end) {
                return false;
            }
        }

        return true;
    }

    protected override async Task ReadBytesCore(uint address, byte[] dstBuffer, int offset, int count) {
        ulong ps4Address = this.WindowBase + address;
        while (count > 0) {
            int cbRead = Math.Min(count, PsRpcClient.MaxTransferSize);
            Memory<byte> dst = dstBuffer.AsMemory(offset, cbRead);
            if (this.HasSafeMemoryAccess) {
                await this.client.ReadMemorySafe(ps4Address, dst).ConfigureAwait(false);
            }
            else {
                await this.client.ReadMemory(ps4Address, dst).ConfigureAwait(false);
            }

            ps4Address += (ulong) cbRead;
            offset += cbRead;
            count -= cbRead;
        }
    }

    protected override async Task WriteBytesCore(uint address, byte[] srcBuffer, int offset, int count) {
        ulong ps4Address = this.WindowBase + address;
        while (count > 0) {
            int cbWrite = Math.Min(count, PsRpcClient.MaxTransferSize);
            ReadOnlyMemory<byte> src = srcBuffer.AsMemory(offset, cbWrite);
            if (this.HasSafeMemoryAccess) {
                await this.client.WriteMemorySafe(ps4Address, src).ConfigureAwait(false);
            }
            else {
                await this.client.WriteMemory(ps4Address, src).ConfigureAwait(false);
            }

            ps4Address += (ulong) cbWrite;
            offset += cbWrite;
            count -= cbWrite;
        }
    }

    /// <summary>
    /// Gets the base address of the game's main executable, or 0 if the PRX could not find it
    /// </summary>
    public async Task<ulong> GetGameExecutableBase() {
        this.EnsureNotClosed();
        using BusyToken x = this.CreateBusyToken();
        return await this.RunNetworkOperation(() => this.client.GetModuleBase("")).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the game's whole memory map with 64-bit addresses. Requires <see cref="HasSafeMemoryAccess"/>
    /// </summary>
    public async Task<List<PsRpcRegion>> GetFullMemoryMap() {
        if (!this.HasSafeMemoryAccess) {
            throw new InvalidOperationException("The PSRPC PRX does not support memory map queries");
        }

        this.EnsureNotClosed();
        using BusyToken x = this.CreateBusyToken();
        return await this.RunNetworkOperation(() => this.client.GetMemoryRegions(0, ulong.MaxValue)).ConfigureAwait(false);
    }

    private async Task<List<MemoryRegion>> GetWindowMemoryRegions(bool willRead, bool willWrite) {
        this.EnsureNotClosed();
        using BusyToken x = this.CreateBusyToken();

        ulong windowBase = this.WindowBase;
        ulong windowEnd = windowBase + WindowSize;
        List<PsRpcRegion> regions = await this.RunNetworkOperation(() => this.client.GetMemoryRegions(windowBase, windowEnd)).ConfigureAwait(false);

        List<MemoryRegion> list = new List<MemoryRegion>(regions.Count);
        foreach (PsRpcRegion region in regions) {
            if ((willRead && !region.IsReadable) || (willWrite && !region.IsWritable)) {
                continue;
            }

            // Clip to the window. The last byte of the window is dropped, because a MemoryRegion
            // reaching the very end would need a 33-bit end address
            ulong start = Math.Max(region.Start, windowBase) - windowBase;
            ulong end = Math.Min(Math.Min(region.End, windowEnd) - windowBase, uint.MaxValue);
            if (end > start) {
                list.Add(new MemoryRegion((uint) start, (uint) (end - start), ToXboxProtection(region.Protection), 0));
            }
        }

        return list;
    }

    /// <summary>
    /// The memory region views only know the Xbox (Win32 PAGE_*) protection flags, so translate
    /// the PS4's ones into the closest of those
    /// </summary>
    private static uint ToXboxProtection(uint ps4Protection) {
        bool read = (ps4Protection & (PsRpcRegion.ProtRead | PsRpcRegion.ProtWrite)) != 0;
        bool write = (ps4Protection & PsRpcRegion.ProtWrite) != 0;
        if ((ps4Protection & PsRpcRegion.ProtExec) != 0) {
            return write ? 0x40u /* ExecuteReadWrite */ : read ? 0x20u /* ExecuteRead */ : 0x10u /* Execute */;
        }

        return write ? 0x04u /* ReadWrite */ : read ? 0x02u /* ReadOnly */ : 0x01u /* NoAccess (GPU only) */;
    }

    private async Task<T> RunNetworkOperation<T>(Func<Task<T>> operation) {
        try {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException || e is IOException) {
            this.Close();
            throw;
        }
    }

    private class FeaturesImpl(ConsoleConnectionPSRPC connection) : IFeatureMemoryRegions {
        public IConsoleConnection Connection => connection;

        public Task<List<MemoryRegion>> GetMemoryRegions(bool willRead, bool willWrite) {
            return connection.GetWindowMemoryRegions(willRead, willWrite);
        }
    }
}
