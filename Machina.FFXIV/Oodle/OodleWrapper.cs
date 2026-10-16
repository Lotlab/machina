// Copyright © 2021 Ravahn - All Rights Reserved
// Modifications Copyright © 2026 Lotlab
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
using Machina.FFXIV.Memory;

namespace Machina.FFXIV.Oodle
{
    /// <summary>
    /// Common implementation for the Oodle wrappers.  The state buffers are allocated
    /// outside of the managed heap because the native Oodle code keeps pointers into them.
    /// </summary>
    public abstract class OodleWrapper : IOodleWrapper, IDisposable
    {
        protected const byte HashtableBits = 17; // 19 for UDP on older clients
        private const int WindowSize = 0x100000; // 0x16000 on older clients

        // Offsets of the window start/end pointers inside the shared dictionary.  They are
        // restored after a foreign state is copied in, since its pointers only refer to the
        // address space of the process the state was read from.
        private const int SharedWindowStartOffset = 8;
        private const int SharedWindowEndOffset = 16;

        protected IOodleNative Native { get; }

        private readonly IntPtr _state;
        private readonly IntPtr _shared;
        private readonly IntPtr _window;
        private readonly object _stateLock = new object();
        private bool _disposed;

        public OodleStateLayout Layout { get; }

        protected OodleWrapper(IOodleNative native, int stateSize, bool isUdp)
        {
            Native = native ?? throw new ArgumentNullException(nameof(native));

            int sharedSize = native.OodleNetwork1_Shared_Size(HashtableBits);
            Layout = new OodleStateLayout(stateSize, sharedSize, WindowSize, isUdp);

            _state = Marshal.AllocHGlobal(stateSize);
            _shared = Marshal.AllocHGlobal(sharedSize);
            _window = Marshal.AllocHGlobal(WindowSize);

            native.OodleNetwork1_Shared_SetWindow(_shared, HashtableBits, _window, WindowSize);
        }

        /// <summary>
        /// Trains the freshly allocated state.  Must be called by derived constructors.
        /// </summary>
        protected void Initialize()
        {
            Train(_state, _shared);
        }

        protected abstract void Train(IntPtr state, IntPtr shared);

        protected abstract bool Decode(IntPtr state, IntPtr shared, IntPtr compressed, int compressedSize, byte[] raw, int rawSize);

        public unsafe bool Decompress(byte[] payload, int offset, int compressedLength, byte[] plaintext, int decompressedLength)
        {
            lock (_stateLock)
            {
                fixed (byte* pPayload = payload)
                {
                    return Decode(_state, _shared, new IntPtr(pPayload + offset), compressedLength, plaintext, decompressedLength);
                }
            }
        }

        public void UpdateFrom(byte[] state, byte[] shared, byte[] window)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (shared == null)
                throw new ArgumentNullException(nameof(shared));
            if (window == null)
                throw new ArgumentNullException(nameof(window));

            if (state.Length != Layout.StateSize || shared.Length != Layout.SharedSize || window.Length != Layout.WindowSize)
                throw new ArgumentException(
                    $"Oodle state size mismatch. Expected state {Layout.StateSize}, shared {Layout.SharedSize}, window {Layout.WindowSize}. " +
                    $"Received state {state.Length}, shared {shared.Length}, window {window.Length}.");

            lock (_stateLock)
            {
                Marshal.Copy(state, 0, _state, Layout.StateSize);
                Marshal.Copy(shared, 0, _shared, Layout.SharedSize);
                Marshal.Copy(window, 0, _window, Layout.WindowSize);

                WritePointer(_shared, SharedWindowStartOffset, _window);
                WritePointer(_shared, SharedWindowEndOffset, IntPtr.Add(_window, Layout.WindowSize));
            }
        }

        private static void WritePointer(IntPtr buffer, int offset, IntPtr value)
        {
            Marshal.Copy(BitConverter.GetBytes(value.ToInt64()), 0, IntPtr.Add(buffer, offset), sizeof(long));
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            Marshal.FreeHGlobal(_state);
            Marshal.FreeHGlobal(_shared);
            Marshal.FreeHGlobal(_window);
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~OodleWrapper()
        {
            Dispose(false);
        }
    }
}
