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

using System.Text;
using MemEngine360.Commands;
using MemEngine360.Engine;
using MemEngine360.Engine.Scanners;
using MemEngine360.Engine.View;
using MemEngine360.PS4.PSRPC;
using PFXToolKitUI.CommandSystem;
using PFXToolKitUI.Services.Messaging;
using PFXToolKitUI.Services.UserInputs;
using PFXToolKitUI.Utils;

namespace MemEngine360.PS4.Commands;

/// <summary>
/// Moves the PSRPC connection's 4 GB memory window to an address the user enters, listing
/// the windows that hold readable memory so they know where to point it
/// </summary>
public class SetMemoryWindowCommand : BaseMemoryEngineCommand {
    private const int MaxListedWindows = 16;

    protected override Executability CanExecuteCore(MemoryEngine engine, MemoryEngineViewState engineVs, CommandEventArgs e) {
        if (engine.Connection == null || engine.Connection.IsClosed)
            return Executability.ValidButCannotExecute;
        if (!(engine.Connection is ConsoleConnectionPSRPC))
            return Executability.Invalid;

        return Executability.Valid;
    }

    protected override async Task ExecuteCommandAsync(MemoryEngineViewState engineVs, MemoryEngine engine, CommandEventArgs e) {
        if (!(engine.Connection is ConsoleConnectionPSRPC connection) || connection.IsClosed)
            return;

        string? windowList = null;
        if (connection.HasSafeMemoryAccess) {
            using IBusyToken? token = await engine.BusyLock.BeginBusyOperationUsingActivity(new BusyTokenRequestUsingActivity() {
                Progress = { Caption = "Memory Window", Text = BusyLock.WaitingMessage },
                QuickReleaseIntention = true
            });

            if (token == null) {
                return;
            }

            try {
                windowList = DescribeWindows(await connection.GetFullMemoryMap(), connection.WindowBase);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException) {
                engine.CheckConnection(token, ConnectionChangeCause.ConnectionError);
                await IMessageDialogService.Instance.ShowMessage("Error", "Failed to read the PS4 memory map: " + ex.Message);
                return;
            }
        }

        SingleUserInputInfo info = new SingleUserInputInfo("Set Memory Window", windowList, "PS4 address (64-bit)", $"0x{connection.WindowBase:X16}") {
            Validate = args => {
                if (!NumberUtils.TryParseHexOrRegular(args.Input, out ulong _)) {
                    args.Errors.Add("Invalid address. Must be an unsigned 64-bit integer");
                }
            },
            Footer = "Rounded down to a multiple of 0x100000000. Scan results and saved addresses are relative to the window, so they move with it"
        };

        if (await IUserInputDialogService.Instance.ShowInputDialogAsync(info) != true || connection.IsClosed) {
            return;
        }

        connection.WindowBase = NumberUtils.ParseHexOrRegular<ulong>(info.Text);
    }

    private static string DescribeWindows(List<PsRpcRegion> regions, ulong currentWindow) {
        // Readable bytes and region names per window. A region that crosses a window boundary counts towards each
        SortedDictionary<ulong, (ulong Bytes, HashSet<string> Names)> windows = new SortedDictionary<ulong, (ulong, HashSet<string>)>();
        foreach (PsRpcRegion region in regions) {
            if (!region.IsReadable) {
                continue;
            }

            for (ulong start = region.Start; start < region.End;) {
                ulong window = ConsoleConnectionPSRPC.AlignToWindow(start);
                ulong end = Math.Min(region.End, window + ConsoleConnectionPSRPC.WindowSize);
                if (end <= start) {
                    break; // the last window of the 64-bit space
                }

                if (!windows.TryGetValue(window, out (ulong Bytes, HashSet<string> Names) entry)) {
                    entry = (0, new HashSet<string>());
                }

                if (!string.IsNullOrEmpty(region.Name)) {
                    entry.Names.Add(region.Name);
                }

                windows[window] = (entry.Bytes + (end - start), entry.Names);
                start = end;
            }
        }

        if (windows.Count < 1) {
            return "The PS4 reported no readable memory";
        }

        StringBuilder sb = new StringBuilder("Windows holding readable memory:");
        foreach (KeyValuePair<ulong, (ulong Bytes, HashSet<string> Names)> window in windows.OrderByDescending(x => x.Value.Bytes).Take(MaxListedWindows).OrderBy(x => x.Key)) {
            sb.AppendLine().Append($"0x{window.Key:X16}  {ValueScannerUtils.FormatBytes(window.Value.Bytes)}");
            if (window.Key == currentWindow) {
                sb.Append("  (current)");
            }

            if (window.Value.Names.Count > 0) {
                string names = string.Join(", ", window.Value.Names.Take(3));
                sb.Append(window.Value.Names.Count > 3 ? $"  [{names}, ...]" : $"  [{names}]");
            }
        }

        if (windows.Count > MaxListedWindows) {
            sb.AppendLine().Append($"... and {windows.Count - MaxListedWindows} smaller windows");
        }

        return sb.ToString();
    }
}
