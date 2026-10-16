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
using System.Runtime.InteropServices;

namespace Machina.FFXIV.Tests.Oodle
{
    /// <summary>
    /// Unmanaged, zero initialized buffer used to call the Oodle native functions.
    /// </summary>
    internal sealed class NativeBuffer : IDisposable
    {
        public IntPtr Pointer { get; }
        public int Length { get; }

        public NativeBuffer(int length)
        {
            Length = length;
            Pointer = Marshal.AllocHGlobal(length);
            Marshal.Copy(new byte[length], 0, Pointer, length);
        }

        public byte this[int index] => Marshal.ReadByte(Pointer, index);

        public static implicit operator IntPtr(NativeBuffer buffer) => buffer.Pointer;

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
