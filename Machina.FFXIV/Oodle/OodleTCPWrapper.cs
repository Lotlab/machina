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

using System;

namespace Machina.FFXIV.Oodle
{
    public class OodleTCPWrapper : OodleWrapper
    {
        public OodleTCPWrapper(IOodleNative native)
            : base(native, native.OodleNetwork1TCP_State_Size(), isUdp: false)
        {
            Initialize();
        }

        protected override void Train(IntPtr state, IntPtr shared)
        {
            Native.OodleNetwork1TCP_Train(state, shared, IntPtr.Zero, IntPtr.Zero, 0);
        }

        protected override bool Decode(IntPtr state, IntPtr shared, IntPtr compressed, int compressedSize, byte[] raw, int rawSize)
        {
            return Native.OodleNetwork1TCP_Decode(state, shared, compressed, compressedSize, raw, rawSize);
        }
    }
}
