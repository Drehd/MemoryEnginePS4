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

using MemEngine360.Commands;
using MemEngine360.Engine;
using MemEngine360.Engine.View;
using MemEngine360.PS4.PSRPC;
using PFXToolKitUI.CommandSystem;
using PFXToolKitUI.Services.Messaging;

namespace MemEngine360.PS4.Commands;

/// <summary>
/// Moves the PSRPC connection's memory window to the one holding the game's main executable
/// </summary>
public class SetMemoryWindowToGameCommand : BaseMemoryEngineCommand {
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

        using IBusyToken? token = await engine.BusyLock.BeginBusyOperationUsingActivity(new BusyTokenRequestUsingActivity() {
            Progress = { Caption = "Memory Window", Text = BusyLock.WaitingMessage },
            QuickReleaseIntention = true
        });

        if (token == null) {
            return;
        }

        ulong gameBase;
        try {
            gameBase = await connection.GetGameExecutableBase();
        }
        catch (Exception ex) when (ex is TimeoutException || ex is IOException) {
            engine.CheckConnection(token, ConnectionChangeCause.ConnectionError);
            await IMessageDialogService.Instance.ShowMessage("Error", "Failed to find the game executable: " + ex.Message);
            return;
        }

        if (gameBase == 0) {
            await IMessageDialogService.Instance.ShowMessage("No game", "PSRPC could not find the game's executable");
            return;
        }

        connection.WindowBase = gameBase;
    }
}
