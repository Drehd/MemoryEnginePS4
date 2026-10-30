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

using MemEngine360.BaseFrontEnd.Services.Connectivity;
using MemEngine360.Connections;
using MemEngine360.Engine;
using MemEngine360.PS4.Commands;
using MemEngine360.PS4.PSRPC;
using PFXToolKitUI;
using PFXToolKitUI.CommandSystem;
using PFXToolKitUI.Notifications;
using PFXToolKitUI.Plugins;

namespace MemEngine360.PS4;

public class PluginPS4 : Plugin {
    protected override void OnInitialize() {
        base.OnInitialize();

        CommandManager.Instance.Register("commands.ps4.SetMemoryWindowCommand", new SetMemoryWindowCommand());
        CommandManager.Instance.Register("commands.ps4.SetMemoryWindowToGameCommand", new SetMemoryWindowToGameCommand());
    }

    protected override Task OnApplicationFullyLoaded() {
        ConsoleConnectionManager manager = ApplicationPFX.GetComponent<ConsoleConnectionManager>();
        manager.Register(ConnectionTypePS4PSRPC.TheID, ConnectionTypePS4PSRPC.Instance);
        OpenConnectionView.Registry.RegisterType<ConnectToPSRPCInfo>(() => new OpenPSRPCConnectionView());

        MemoryEngineManager.Instance.ProvidePostConnectionActions += OnProvidePostConnectionActions;
        return Task.CompletedTask;
    }

    private static void OnProvidePostConnectionActions(object? sender, ProvidePostConnectionActionsEventArgs e) {
        if (e.Connection is ConsoleConnectionPSRPC) {
            e.Notification.Actions.Add(new CommandNotificationAction("Set Memory Window", "commands.ps4.SetMemoryWindowCommand") {
                ToolTip = "Choose which 4 GB of the PS4's 64-bit memory MemoryEngine addresses refer to. It starts on the game's executable"
            });
        }
    }
}
