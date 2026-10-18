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

using Machina.FFXIV.Oodle;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Machina.FFXIV.Tests.Oodle
{
    [TestClass()]
    public class OodleNative_LibraryTests
    {
        private OodleNative_Library _sut;

        [TestInitialize()]
        public void TestInitialize()
        {
            const string path = @"c:\temp\oo2net_9_win64.dll";
            _sut = new OodleNative_Library();
            _sut.Initialize(path);
        }


        [TestCleanup()]
        public void TestCleanup()
        {
            _sut.UnInitialize();
        }

        [TestMethod()]
        public void InitializeTest()
        {
            bool result = _sut.Initialized;

            Assert.IsTrue(result);
        }

        [TestMethod()]
        public void UnInitializeTest()
        {
            _sut.UnInitialize();

            bool result = _sut.Initialized;

            Assert.IsFalse(result);
        }

        [TestMethod()]
        public void OodleNetwork1UDP_State_SizeTest()
        {
            int result = _sut.OodleNetwork1UDP_State_Size();

            Assert.IsGreaterThan(0, result);
        }

        [TestMethod()]
        public void OodleNetwork1_Shared_SizeTest()
        {
            const int htbits = 0x13;
            int result = _sut.OodleNetwork1_Shared_Size(htbits);

            Assert.IsGreaterThan(0, result);
        }

        [TestMethod()]
        public void OodleNetwork1_Shared_SetWindowTest()
        {
            const int htbits = 0x13;
            int sharedSize = _sut.OodleNetwork1_Shared_Size(htbits);

            using NativeBuffer window = new NativeBuffer(0x8000);
            using NativeBuffer shared = new NativeBuffer(sharedSize);

            _sut.OodleNetwork1_Shared_SetWindow(shared, htbits, window, window.Length);

            Assert.AreNotEqual(0, shared[0]);
        }

        [TestMethod()]
        public void OodleNetwork1UDP_TrainTest()
        {
            const int htbits = 0x13;
            int stateSize = _sut.OodleNetwork1UDP_State_Size();
            int sharedSize = _sut.OodleNetwork1_Shared_Size(htbits);

            using NativeBuffer window = new NativeBuffer(0x8000);
            using NativeBuffer shared = new NativeBuffer(sharedSize);
            using NativeBuffer state = new NativeBuffer(stateSize);

            _sut.OodleNetwork1_Shared_SetWindow(shared, htbits, window, window.Length);
            _sut.OodleNetwork1UDP_Train(state, shared, IntPtr.Zero, IntPtr.Zero, 0);

            Assert.AreNotEqual(0, state[4]);
        }

        [TestMethod()]
        public unsafe void OodleNetwork1UDP_DecodeTest()
        {
            const int htbits = 0x13;
            int stateSize = _sut.OodleNetwork1UDP_State_Size();
            int sharedSize = _sut.OodleNetwork1_Shared_Size(htbits);

            using NativeBuffer window = new NativeBuffer(0x8000);
            using NativeBuffer shared = new NativeBuffer(sharedSize);
            using NativeBuffer state = new NativeBuffer(stateSize);

            _sut.OodleNetwork1_Shared_SetWindow(shared, htbits, window, window.Length);
            _sut.OodleNetwork1UDP_Train(state, shared, IntPtr.Zero, IntPtr.Zero, 0);

            byte[] source = new byte[255];
            byte[] uncompressed = new byte[255];
            for (byte i = 0; i < source.Length; i++) source[i] = i;
            byte[] compressed = new byte[255];
            bool result = _sut.OodleNetwork1UDP_Encode(state, shared, source, source.Length, compressed);
            Assert.IsTrue(result);
            fixed (byte* ptr = compressed)
            {
                result = _sut.OodleNetwork1UDP_Decode(state, shared, new IntPtr(ptr), compressed.Length, uncompressed, uncompressed.Length);
            }
            Assert.IsTrue(result);
            result = true;

            // compare each byte
            for (byte i = 0; i < source.Length; i++)
                if (source[i] != uncompressed[i])
                    result = false;

            Assert.IsTrue(result);
        }

        [TestMethod()]
        public unsafe void OodleNetwork1TCP_EncodeDecodeTest()
        {
            if (!_sut.Initialized)
                Assert.Inconclusive("oo2net library is not available.");

            const int htbits = 17;
            int stateSize = _sut.OodleNetwork1TCP_State_Size();
            int sharedSize = _sut.OodleNetwork1_Shared_Size(htbits);

            // The encoder and the decoder each own a state, the way the game and Machina do.
            using NativeBuffer encoderWindow = new NativeBuffer(0x100000);
            using NativeBuffer encoderShared = new NativeBuffer(sharedSize);
            using NativeBuffer encoderState = new NativeBuffer(stateSize);
            using NativeBuffer decoderWindow = new NativeBuffer(0x100000);
            using NativeBuffer decoderShared = new NativeBuffer(sharedSize);
            using NativeBuffer decoderState = new NativeBuffer(stateSize);

            _sut.OodleNetwork1_Shared_SetWindow(encoderShared, htbits, encoderWindow, encoderWindow.Length);
            _sut.OodleNetwork1TCP_Train(encoderState, encoderShared, IntPtr.Zero, IntPtr.Zero, 0);
            _sut.OodleNetwork1_Shared_SetWindow(decoderShared, htbits, decoderWindow, decoderWindow.Length);
            _sut.OodleNetwork1TCP_Train(decoderState, decoderShared, IntPtr.Zero, IntPtr.Zero, 0);

            byte[] source = new byte[255];
            byte[] uncompressed = new byte[255];
            for (byte i = 0; i < source.Length; i++) source[i] = i;
            byte[] compressed = new byte[source.Length];

            // The TCP encode slot used to be bound to the UDP export, so this call handed a TCP
            // state to the UDP encoder: it crashed the process instead of encoding anything.
            bool result = _sut.OodleNetwork1TCP_Encode(encoderState, encoderShared, source, source.Length, compressed);
            Assert.IsTrue(result);

            fixed (byte* ptr = compressed)
            {
                result = _sut.OodleNetwork1TCP_Decode(decoderState, decoderShared, new IntPtr(ptr), compressed.Length, uncompressed, uncompressed.Length);
            }
            Assert.IsTrue(result);

            // compare each byte
            for (byte i = 0; i < source.Length; i++)
                if (source[i] != uncompressed[i])
                    result = false;

            Assert.IsTrue(result);
        }
    }
}
