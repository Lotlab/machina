// Copyright © 2026 Lotlab
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY. without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see<http://www.gnu.org/licenses/>.

using System;

namespace Machina.FFXIV.Memory
{
    /// <summary>
    /// The part of <see cref="MemoryScanner"/> that the network monitor needs: making sure a
    /// client can be read, and reading the Oodle state of one network channel and direction.
    /// Kept separate so the monitor's recovery policy can be exercised without a game client.
    /// </summary>
    internal interface IOodleStateReader : IDisposable
    {
        /// <summary>
        /// Opens the supplied process for reading.  Throws when it cannot be opened, which the
        /// caller reports and treats as "no client to read".
        /// </summary>
        void Open(int processId);

        /// <summary>
        /// Opens the first client found by the default process name, returning false when none is
        /// running.
        /// </summary>
        bool Open();

        /// <summary>
        /// Ensures the client can be read, opening the process through
        /// <paramref name="openProcess"/> when it has to be opened again.
        /// </summary>
        bool EnsureReady(Func<bool> openProcess);

        /// <summary>
        /// Reads one direction of the zone network channel's Oodle state, or null when
        /// unavailable.
        /// </summary>
        OodleState? GetZoneState(OodleStateLayout layout, bool isTx);

        /// <summary>
        /// Reads one direction of the chat network channel's Oodle state, or null when
        /// unavailable.
        /// </summary>
        OodleState? GetChatState(OodleStateLayout layout, bool isTx);
    }
}
