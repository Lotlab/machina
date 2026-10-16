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

namespace Machina.FFXIV.Memory
{
    /// <summary>
    /// Describes the buffer sizes of an Oodle network state.  These sizes must match the
    /// local Oodle implementation so a state read out of the game process can be applied
    /// to a local <see cref="Oodle.IOodleWrapper"/>.
    /// </summary>
    public struct OodleStateLayout
    {
        public int StateSize { get; set; }
        public int SharedSize { get; set; }
        public int WindowSize { get; set; }

        /// <summary>
        /// True when the state belongs to an UDP network channel, false for TCP.
        /// </summary>
        public bool IsUdp { get; set; }

        public OodleStateLayout(int stateSize, int sharedSize, int windowSize, bool isUdp = false)
        {
            StateSize = stateSize;
            SharedSize = sharedSize;
            WindowSize = windowSize;
            IsUdp = isUdp;
        }

        public bool IsValid => StateSize > 0 && SharedSize > 0 && WindowSize > 0;
    }

    /// <summary>
    /// A snapshot of a single Oodle network state, as read out of the game process.
    /// </summary>
    public struct OodleState
    {
        public byte[] State { get; set; }
        public byte[] Shared { get; set; }
        public byte[] Window { get; set; }
    }

    /// <summary>
    /// The send and receive Oodle states of a single network channel.
    /// </summary>
    public struct ClientState
    {
        public OodleState Tx { get; set; }
        public OodleState Rx { get; set; }
    }
}
