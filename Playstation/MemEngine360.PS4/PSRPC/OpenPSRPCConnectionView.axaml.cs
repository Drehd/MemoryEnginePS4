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

using Avalonia.Controls;
using MemEngine360.BaseFrontEnd.Services.Connectivity;
using MemEngine360.Connections;
using PFXToolKitUI.Avalonia.Bindings;
using PFXToolKitUI.Avalonia.Bindings.TextBoxes;
using PFXToolKitUI.Services.Messaging;

namespace MemEngine360.PS4.PSRPC;

public partial class OpenPSRPCConnectionView : UserControl, IConsoleConnectivityControl {
    private readonly IBinder<ConnectToPSRPCInfo> ipBinder =
        new AvaloniaPropertyToEventPropertyBinder<ConnectToPSRPCInfo>(
            TextBox.TextProperty,
            nameof(ConnectToPSRPCInfo.IpAddressChanged),
            b => ((TextBox) b.Control).Text = b.Model.IpAddress,
            b => b.Model.IpAddress = ((TextBox) b.Control).Text ?? "");

    private readonly IBinder<ConnectToPSRPCInfo> portBinder =
        new TextBoxToEventPropertyBinder<ConnectToPSRPCInfo>(
            nameof(ConnectToPSRPCInfo.PortChanged),
            b => ((TextBox) b.Control).Text = b.Model.Port.ToString(),
            async (b, text) => {
                if (int.TryParse(text, out int port)) {
                    b.Model.Port = port;
                    return true;
                }
                else {
                    await IMessageDialogService.Instance.ShowMessage("Invalid port", "Invalid port value");
                    return false;
                }
            });

    public ConnectToPSRPCInfo? ConnectionInfo { get; private set; }

    public OpenPSRPCConnectionView() {
        this.InitializeComponent();
    }

    public void OnConnected(OpenConnectionView dialog, UserConnectionInfo info) {
        this.ipBinder.Attach(this.PART_IpAddressTextBox, this.ConnectionInfo = (ConnectToPSRPCInfo) info);
        this.portBinder.Attach(this.PART_PortTextBox, this.ConnectionInfo);
    }

    public void OnDisconnected() {
        this.ipBinder.Detach();
        this.portBinder.Detach();
        this.ConnectionInfo = null;
    }
}
