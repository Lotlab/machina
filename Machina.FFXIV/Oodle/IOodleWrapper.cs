// Copyright © 2021 Ravahn - All Rights Reserved
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

using Machina.FFXIV.Memory;

namespace Machina.FFXIV.Oodle
{
    public interface IOodleWrapper
    {
        /// <summary>
        /// Sizes of the state buffers held by this wrapper.  They are needed to copy a
        /// matching state out of the game process, see <see cref="UpdateFrom"/>.
        /// </summary>
        OodleStateLayout Layout { get; }

        unsafe bool Decompress(byte[] payload, int offset, int compressedLength, byte[] plaintext, int decompressedLength);

        /// <summary>
        /// Replaces the decompression state with a snapshot taken from another process,
        /// for example via <see cref="MemoryScanner"/>.
        /// </summary>
        void UpdateFrom(byte[] state, byte[] shared, byte[] window);
    }
}
