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
using System.Collections.Generic;
using System.Reflection;
using Machina.FFXIV.Memory;
using Machina.FFXIV.Oodle;

namespace Machina.FFXIV.Tests.Utility
{
    /// <summary>
    /// Stands in for the Oodle decoder in tests that only exercise how a decoder handles its
    /// state: it records the states it is given and never decodes anything.
    /// </summary>
    internal sealed class FakeOodleWrapper : IOodleWrapper
    {
        public static readonly OodleStateLayout DefaultLayout = new OodleStateLayout(16, 16, 16, false);

        public OodleStateLayout Layout { get; }

        /// <summary>Number of times a state was applied, an install and a restore alike.</summary>
        public int UpdateCount { get; private set; }

        public FakeOodleWrapper()
            : this(DefaultLayout)
        {
        }

        public FakeOodleWrapper(OodleStateLayout layout)
        {
            Layout = layout;
        }

        public bool Decompress(byte[] payload, int offset, int compressedLength, byte[] plaintext, int decompressedLength)
            => false;

        public void UpdateFrom(byte[] state, byte[] shared, byte[] window)
            => UpdateCount++;
    }

    /// <summary>
    /// Stands in for <see cref="MemoryScanner"/>, recording which channel and direction the
    /// monitor asks for.
    /// </summary>
    internal sealed class FakeOodleStateReader : IOodleStateReader
    {
        /// <summary>Requests in order, as (isChatChannel, isTx).</summary>
        public List<Tuple<bool, bool>> Requests { get; } = new List<Tuple<bool, bool>>();

        /// <summary>
        /// Throws for the directions this returns true for, the way a game client that cannot be
        /// read does.  The request is recorded first, so which direction was asked for can still be
        /// asserted.
        /// </summary>
        public Func<bool, bool> Unreadable { get; set; }

        public void Open(int processId)
        {
        }

        public bool Open() => true;

        public bool EnsureReady(Func<bool> openProcess) => true;

        public OodleState? GetZoneState(OodleStateLayout layout, bool isTx)
        {
            Requests.Add(new Tuple<bool, bool>(false, isTx));
            ThrowIfUnreadable(isTx);
            return MakeState(layout);
        }

        public OodleState? GetChatState(OodleStateLayout layout, bool isTx)
        {
            Requests.Add(new Tuple<bool, bool>(true, isTx));
            ThrowIfUnreadable(isTx);
            return MakeState(layout);
        }

        public void Dispose()
        {
        }

        private void ThrowIfUnreadable(bool isTx)
        {
            if (Unreadable != null && Unreadable(isTx))
                throw new InvalidOperationException("The game client could not be read.");
        }

        private static OodleState MakeState(OodleStateLayout layout) => new OodleState
        {
            State = new byte[layout.StateSize],
            Shared = new byte[layout.SharedSize],
            Window = new byte[layout.WindowSize],
        };
    }

    /// <summary>
    /// Builds uncompressed bundles the way the client writes them: a 40 byte header followed by
    /// length prefixed messages, each of which starts with its length and a padding word.
    /// </summary>
    internal static class TestBundles
    {
        public const int HeaderSize = 40;

        public static byte[] Uncompressed(ushort messageCount, params byte[][] messages)
        {
            int payloadLength = 0;
            foreach (byte[] message in messages)
                payloadLength += message.Length;

            byte[] bundle = new byte[HeaderSize + payloadLength];
            BitConverter.GetBytes(0x41a05252u).CopyTo(bundle, 0);
            BitConverter.GetBytes(0x11223344u).CopyTo(bundle, 4);
            BitConverter.GetBytes(0x11223344u).CopyTo(bundle, 8);
            BitConverter.GetBytes(0x11223344u).CopyTo(bundle, 12);
            BitConverter.GetBytes((uint)bundle.Length).CopyTo(bundle, 24);
            BitConverter.GetBytes((ushort)1).CopyTo(bundle, 28);
            BitConverter.GetBytes(messageCount).CopyTo(bundle, 30);
            bundle[32] = 1;
            bundle[33] = 0; // CompressionType.None
            BitConverter.GetBytes((uint)payloadLength).CopyTo(bundle, 36);

            int offset = HeaderSize;
            foreach (byte[] message in messages)
            {
                Array.Copy(message, 0, bundle, offset, message.Length);
                offset += message.Length;
            }

            return bundle;
        }

        /// <summary>
        /// Builds one message.  A non-zero padding word makes the whole bundle look like it was
        /// decompressed with the wrong dictionary.
        /// </summary>
        public static byte[] Message(byte fill, ushort padding = 0, ushort length = 16)
        {
            byte[] message = new byte[length];
            BitConverter.GetBytes(length).CopyTo(message, 0);
            BitConverter.GetBytes(padding).CopyTo(message, 2);
            for (int i = 4; i < length; i++)
                message[i] = fill;

            return message;
        }

        public static byte[] Concat(params byte[][] arrays)
        {
            int total = 0;
            foreach (byte[] array in arrays)
                total += array.Length;

            byte[] result = new byte[total];
            int offset = 0;
            foreach (byte[] array in arrays)
            {
                Array.Copy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }

            return result;
        }
    }

    /// <summary>
    /// Reaches into the parts of the decoder and the monitor that stay private: the decoder owns
    /// its Oodle wrapper, and the monitor owns the decoders of a connection.
    /// </summary>
    internal static class TestReflection
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        public static void InjectOodleWrapper(FFXIVBundleDecoder decoder, IOodleWrapper wrapper)
            => typeof(FFXIVBundleDecoder).GetField("_oodle", Instance).SetValue(decoder, wrapper);

        public static FFXIVBundleDecoder GetDecoder(FFXIVNetworkMonitor monitor, string connectionId, bool isTx)
        {
            var decoders = (Dictionary<string, FFXIVBundleDecoder>)typeof(FFXIVNetworkMonitor)
                .GetField(isTx ? "_sentDecoders" : "_receivedDecoders", Instance)
                .GetValue(monitor);

            return decoders.TryGetValue(connectionId, out FFXIVBundleDecoder decoder) ? decoder : null;
        }
    }
}
