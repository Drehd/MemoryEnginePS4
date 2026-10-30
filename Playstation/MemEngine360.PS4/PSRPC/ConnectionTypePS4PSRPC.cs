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

using System.Net;
using System.Net.Sockets;
using MemEngine360.Configs;
using MemEngine360.Connections;
using PFXToolKitUI.AdvancedMenuService;
using PFXToolKitUI.Icons;
using PFXToolKitUI.Interactivity.Contexts;
using PFXToolKitUI.Services.Messaging;

namespace MemEngine360.PS4.PSRPC;

public class ConnectionTypePS4PSRPC : RegisteredConnectionType {
    public const string TheID = "console.ps4.psrpc-coreimpl";
    public static readonly RegisteredConnectionType Instance = new ConnectionTypePS4PSRPC();

    public override string DisplayName => "PS4 (PSRPC)";

    public override string FooterText => "Experimental";

    public override string LongDescription => "A connection to a PS4 game running the PSRPC PRX. MemoryEngine sees a 4 GB window of the game's 64-bit memory, which can be moved from the Remote Controls menu.";

    public override Icon Icon => SimpleIcons.PS4PSRPCIcon;

    private ConnectionTypePS4PSRPC() {
    }

    public override IEnumerable<IMenuEntry> GetRemoteContextOptions() {
        yield return new CommandMenuEntry("commands.ps4.SetMemoryWindowToGameCommand", "Window to Game Executable", "Move the 4 GB memory window to the one holding the game's executable");
        yield return new CommandMenuEntry("commands.ps4.SetMemoryWindowCommand", "Set Memory Window...", "Choose which 4 GB of the PS4's 64-bit memory MemoryEngine addresses refer to");
    }

    protected override string GetStatusBarTextCore(IConsoleConnection connection) {
        ConsoleConnectionPSRPC conn = (ConsoleConnectionPSRPC) connection;
        return base.GetStatusBarTextCore(connection) + $" - Window 0x{conn.WindowBase:X16}";
    }

    public override UserConnectionInfo CreateConnectionInfo() => new ConnectToPSRPCInfo();

    public override async Task<IConsoleConnection?> OpenConnection(UserConnectionInfo? _info, IContextData additionalContext, CancellationTokenSource cancellation) {
        ConnectToPSRPCInfo info = (ConnectToPSRPCInfo) _info!;
        if (string.IsNullOrWhiteSpace(info.IpAddress)) {
            await IMessageDialogService.Instance.ShowMessage("Invalid IP", "IP address is invalid", icon: MessageBoxIcons.ErrorIcon);
            return null;
        }

        BasicApplicationConfiguration.Instance.LastPS4HostName = info.IpAddress;
        BasicApplicationConfiguration.Instance.StorageManager.SaveArea(BasicApplicationConfiguration.Instance);

        IPAddress? address;
        if (!IPAddress.TryParse(info.IpAddress, out address)) {
            try {
                using CancellationTokenSource ctsDnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                ctsDnsTimeout.CancelAfter(3000);

                IPAddress[] addresses = await Dns.GetHostAddressesAsync(info.IpAddress, AddressFamily.InterNetwork, ctsDnsTimeout.Token);
                address = addresses.Length > 0 ? addresses[0] : null;
            }
            catch (Exception) {
                address = null;
            }

            if (address == null) {
                await IMessageDialogService.Instance.ShowMessage("Invalid Ip", "Failed to resolve IP from host name", icon: MessageBoxIcons.ErrorIcon, dialogCancellation: cancellation.Token);
                return null;
            }
        }

        IPEndPoint endPoint = new IPEndPoint(address, info.Port);
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try {
            using CancellationTokenSource ctsSocketTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            ctsSocketTimeout.CancelAfter(5000);
            await socket.ConnectAsync(endPoint, ctsSocketTimeout.Token);
        }
        catch (OperationCanceledException) {
            socket.Dispose();
            await IMessageDialogService.Instance.ShowMessage("Timeout", "Timed out trying to connect to the PS4. Is the game running with the PSRPC PRX loaded?", icon: MessageBoxIcons.ErrorIcon, dialogCancellation: cancellation.Token);
            return null;
        }
        catch (Exception e) {
            socket.Dispose();
            await IMessageDialogService.Instance.ShowMessage("Error", "Error while connecting: " + e.Message, icon: MessageBoxIcons.ErrorIcon, dialogCancellation: cancellation.Token);
            return null;
        }

        PsRpcClient client = new PsRpcClient(socket, endPoint);
        ulong gameBase;
        bool hasSafeMemoryAccess;
        try {
            // A PRX built with a different RPC_MAGIC ignores requests rather than answering,
            // so a timeout here usually means a magic mismatch
            gameBase = await client.GetModuleBase("");
            hasSafeMemoryAccess = await client.SupportsSafeMemoryAccess();
        }
        catch (TimeoutException) {
            client.Disconnect();
            await IMessageDialogService.Instance.ShowMessage("Timeout", "Connected, but PSRPC did not answer. Make sure the PRX uses port " + info.Port + " and magic 0x" + PsRpcClient.Magic.ToString("X8"), icon: MessageBoxIcons.ErrorIcon, dialogCancellation: cancellation.Token);
            return null;
        }
        catch (Exception e) {
            client.Disconnect();
            await IMessageDialogService.Instance.ShowMessage("Error", "PSRPC error: " + e.Message, icon: MessageBoxIcons.ErrorIcon, dialogCancellation: cancellation.Token);
            return null;
        }

        if (!hasSafeMemoryAccess) {
            await IMessageDialogService.Instance.ShowMessage(
                "Old PSRPC PRX",
                "This PSRPC PRX predates the memory map opcodes",
                "Reading or writing memory that is not mapped will crash the game, and memory regions are unavailable, so scan only ranges you know are valid.\n\n" +
                "Rebuild the PRX from the latest source to make scanning safe.",
                icon: MessageBoxIcons.WarningIcon, dialogCancellation: cancellation.Token);
        }

        return new ConsoleConnectionPSRPC(client, hasSafeMemoryAccess, gameBase);
    }
}
