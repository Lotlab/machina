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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Machina.FFXIV.Headers;
using Machina.FFXIV.Memory;
using Machina.Infrastructure;

namespace Machina.FFXIV
{
    public class FFXIVBundleDecoder
    {
        private byte[] _bundleBuffer;
        private readonly byte[] _decompressionBuffer = new byte[1024 * 128];
        private int _allocated;

        /// <summary>
        /// First word of the bundle magic, the byte sequence 52 52 A0 41.  Read back through a
        /// little endian uint dereference it is 0x41a05252; as the network order hex that shows
        /// up in packet dumps it is 0x5252a041.  Both comparisons that look for the magic have
        /// to use this value.
        /// </summary>
        private const uint BundleMagic = 0x41a05252;

        /// <summary>
        /// Bundles to try against a state read out of the game client before giving up on it.
        /// </summary>
        private const int MaxResyncBundles = 32;

        /// <summary>
        /// Bounds on how often that state is read: it is several megabytes out of another process,
        /// and a stream that keeps failing must not ask for it once per packet.
        /// </summary>
        private static readonly TimeSpan MinRecoveryInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan MaxRecoveryInterval = TimeSpan.FromSeconds(8);

        private Oodle.IOodleWrapper _oodle;

        /// <summary>
        /// The state read out of the game client that the stream is resynchronizing against, or
        /// null when the decoder is decoding with its own state.
        /// </summary>
        private OodleState? _snapshot;

        private int _resyncBundles;
        private int _recoveryAttempts;
        private DateTime _nextRecoveryAttempt = DateTime.MinValue;
        private bool _decodedMessages;

        public Queue<Tuple<long, byte[]>> Messages = new Queue<Tuple<long, byte[]>>(20);

        /// <summary>
        /// Raised when the decoder detects that it lost sync with the bundle stream, for example
        /// because the Oodle decompression state is out of date.  Returning true means a fresh
        /// state was installed, and the bundle that failed is tried once more before it is
        /// dropped.
        /// </summary>
        internal event Func<FFXIVBundleDecoder, bool> DecodeFailedHandler;

        /// <summary>
        /// True once a bundle was decoded successfully.  This tells a direction that is still
        /// waiting for its first bundle apart from one that is in sync.
        /// </summary>
        internal bool HasDecodedMessages => _decodedMessages;

        /// <summary>
        /// Sizes of the Oodle state buffers, or null when no Oodle state has been created yet.
        /// </summary>
        internal OodleStateLayout? OodleLayout => _oodle?.Layout;

        public DateTime LastMessageTimestamp
        { get; set; } = DateTime.MinValue;

        public unsafe void StoreData(byte[] buffer)
        {
            // append buffer data
            if (_bundleBuffer == null)
            {
                _bundleBuffer = buffer;
                _allocated = buffer.Length;
            }
            else
            {
                // resize buffer if there is a remaining amount with a different length than the incoming buffer.
                if (_bundleBuffer.Length - _allocated != buffer.Length)
                    Array.Resize(ref _bundleBuffer, buffer.Length + _allocated);

                Array.Copy(buffer, 0, _bundleBuffer, _allocated, buffer.Length);
                _allocated += buffer.Length;
            }

            int offset = 0;
            while (offset < _allocated)
            {
                // Wait until a whole bundle header is available.
                if (_allocated - offset < sizeof(Server_BundleHeader))
                    break;

                Server_BundleHeader header = ReadHeader(offset);

                if (header.magic0 != BundleMagic &&
                    header.magic0 != 0 && header.magic1 != 0 && header.magic2 != 0 && header.magic3 != 0)
                {
                    if (LastMessageTimestamp != DateTime.MinValue)
                        Trace.WriteLine("FFXIVBundleDecoder: Invalid magic # in header:" + ConversionUtility.ByteArrayToHexString(_bundleBuffer, offset, 36), "DEBUG-MACHINA");

                    int nextBundle = GetNextMagicNumberPos(_bundleBuffer, offset);
                    offset = nextBundle < 0 ? _allocated : nextBundle;
                    continue;
                }

                // Wait until the whole bundle has arrived.
                if (header.length > _allocated - offset)
                    break;

                // Do not process if there is no payload.
                if (header.length == sizeof(Server_BundleHeader))
                {
                    offset += sizeof(Server_BundleHeader);
                    continue;
                }

                if (header.length > sizeof(Server_BundleHeader))
                {
                    // While resynchronizing, every bundle is decoded from the state that was read
                    // out of the game client: a bundle that goes wrong already moved - and possibly
                    // damaged - the decoder's own state, and the bundles that follow need the state
                    // the client had when it decoded them.
                    if (_snapshot != null)
                        RestoreOodleState();

                    if (TryDecodeBundle(offset, out int bundleLength, out List<Tuple<long, byte[]>> messages))
                    {
                        foreach (Tuple<long, byte[]> message in messages)
                            Messages.Enqueue(message);

                        _decodedMessages = true;
                        LastMessageTimestamp = DateTime.UtcNow;
                        ResetRecoveryBackoff();
                        ClearSnapshot();
                        offset += bundleLength;
                        continue;
                    }
                }

                offset = RecoverLostBundle(offset);
            }

            if (offset >= _allocated)
            {
                _bundleBuffer = null;
                _allocated = 0;
            }
            else if (offset > 0)
            {
                Array.Copy(_bundleBuffer, offset, _bundleBuffer, 0, _allocated - offset);
                _allocated -= offset;
            }
        }

        /// <summary>
        /// Handles a bundle that could not be decoded, returning the offset to carry on from.
        /// </summary>
        private int RecoverLostBundle(int offset)
        {
            if (_snapshot != null)
            {
                // A state is already loaded and it has passed this bundle, so walk forward to the
                // first bundle it can decode.  A state read at a point in time can only decode the
                // bundles sent after it: everything older is dropped instead of being retried.
                if (++_resyncBundles > MaxResyncBundles)
                {
                    Trace.WriteLine("FFXIVBundleDecoder: The restored oodle state does not decode the data on hand, waiting for new data.", "DEBUG-MACHINA");

                    // The snapshot is the state the decoder keeps once it stops resynchronizing, so
                    // put it back before dropping it: the attempt that just failed may have left the
                    // native state half updated, and the next bundle has to start from a state that
                    // is known to be usable.
                    RestoreOodleState();
                    ClearSnapshot();
                }

                return SkipBundle(offset);
            }

            if (RequestOodleState())
            {
                // The listener may have installed exactly the state this bundle was missing, so try
                // it once more before throwing it away.
                return offset;
            }

            return SkipBundle(offset);
        }

        /// <summary>
        /// Asks the listener to install a fresh Oodle decompression state, at an interval that
        /// grows while a stream keeps failing.
        /// </summary>
        private bool RequestOodleState()
        {
            if (DecodeFailedHandler == null || DateTime.UtcNow < _nextRecoveryAttempt)
                return false;

            _nextRecoveryAttempt = DateTime.UtcNow + RecoveryInterval(++_recoveryAttempts);

            return DecodeFailedHandler(this);
        }

        /// <summary>
        /// Forgets the failures so far.  The stream is in sync again, so the next loss of sync
        /// starts from the shortest interval instead of waiting out the schedule the previous one
        /// left behind, which would drop the bundles that arrive in the meantime.
        /// </summary>
        private void ResetRecoveryBackoff()
        {
            _recoveryAttempts = 0;
            _nextRecoveryAttempt = DateTime.MinValue;
        }

        private static TimeSpan RecoveryInterval(int attempts)
        {
            double interval = MinRecoveryInterval.TotalMilliseconds * Math.Pow(2, Math.Min(attempts - 1, 5));
            return TimeSpan.FromMilliseconds(Math.Min(interval, MaxRecoveryInterval.TotalMilliseconds));
        }

        /// <summary>
        /// Moves past a bundle that cannot be used, trusting the length the bundle declares when it
        /// points at another bundle, and scanning for the next magic when the header is damaged.
        /// </summary>
        private unsafe int SkipBundle(int offset)
        {
            if (_allocated - offset >= sizeof(Server_BundleHeader))
            {
                Server_BundleHeader header = ReadHeader(offset);
                if (header.length > sizeof(Server_BundleHeader) && header.length <= _allocated - offset)
                    return offset + (int)header.length;
            }

            int nextBundle = GetNextMagicNumberPos(_bundleBuffer, offset);
            return nextBundle < 0 ? _allocated : nextBundle;
        }

        /// <summary>
        /// Decompresses one bundle and validates every message in it.  Nothing is handed out until
        /// the whole bundle is known to be consistent, so a bundle decoded with a stale dictionary
        /// cannot deliver part of its messages before the loss of sync is noticed.
        /// </summary>
        private unsafe bool TryDecodeBundle(int offset, out int bundleLength, out List<Tuple<long, byte[]>> messages)
        {
            messages = null;

            Server_BundleHeader header = ReadHeader(offset);
            bundleLength = (int)header.length;

            byte[] buffer = DecompressFFXIVMessage(ref header, _bundleBuffer, offset, out int messageBufferSize);
            if (buffer == null || messageBufferSize <= 0)
            {
                Trace.WriteLine($"FFXIVBundleDecode() - Resetting stream. Message Null:{buffer == null}, Buffer Size:{messageBufferSize}");
                return false;
            }

            // Every message needs at least its length and its padding word.
            if (header.message_count > messageBufferSize / (2 * sizeof(ushort)))
                return false;

            var parsed = new List<Tuple<long, byte[]>>(header.message_count);
            long epoch = (long)ConversionUtility.ntohq(header.epoch);
            int messageOffset = 0;

            fixed (byte* msgPtr = buffer)
            {
                for (int i = 0; i < header.message_count; i++)
                {
                    if (messageOffset + 2 * sizeof(ushort) > messageBufferSize)
                        return false;

                    ushort messageLength = ((ushort*)(msgPtr + messageOffset))[0];

                    // A non-zero padding word means the bundle was decompressed with the wrong
                    // dictionary: the messages in it are not where the stream says they are.
                    if (((ushort*)(msgPtr + messageOffset))[1] != 0)
                    {
                        Trace.WriteLine("FFXIVBundleDecoder: Decode failed, discarding the bundle data.", "DEBUG-MACHINA");
                        return false;
                    }

                    if (messageOffset + messageLength > messageBufferSize)
                    {
                        Trace.WriteLine($"FFXIVBundleDecoder: Bad message offset - offset={messageOffset + messageLength}, bufferSize={messageBufferSize}", "DEBUG-MACHINA");
                        return false;
                    }

                    byte[] data = new byte[messageLength];
                    Array.Copy(buffer, messageOffset, data, 0, data.Length);
                    parsed.Add(new Tuple<long, byte[]>(epoch, data));

                    messageOffset += messageLength;
                }
            }

            if (messageOffset != messageBufferSize)
            {
                // The messages normally account for every decompressed byte.  Report the bundles
                // that do not instead of dropping them: the padding check above is what detects a
                // bundle decoded with the wrong dictionary.
                Trace.WriteLine($"FFXIVBundleDecoder: Bundle messages do not fill the payload - messages {messageOffset}, buffer {messageBufferSize}.", "DEBUG-MACHINA");
            }

            messages = parsed;
            return true;
        }

        private unsafe Server_BundleHeader ReadHeader(int offset)
        {
            fixed (byte* ptr = _bundleBuffer)
            {
                return *(Server_BundleHeader*)(ptr + offset);
            }
        }

        public unsafe Tuple<long, byte[]> GetNextFFXIVMessage()
        {
            return Messages.Count > 0 ? Messages.Dequeue() : null;
        }

        private bool _encodingError;

        private unsafe byte[] DecompressFFXIVMessage(ref Server_BundleHeader header, byte[] buffer, int offset, out int ffxivMessageSize)
        {
            ffxivMessageSize = 0;

            switch (header.compression)
            {
                case CompressionType.None:
                    // uncompressed - copy to output buffer
                    ffxivMessageSize = (int)header.length - sizeof(Server_BundleHeader);
                    for (int i = 0; i < ffxivMessageSize / 4; i++)
                    {
                        // todo: use unsafe pointer operations
                        uint value = BitConverter.ToUInt32(buffer, offset + (i * 4) + sizeof(Server_BundleHeader));
                        Array.Copy(BitConverter.GetBytes(value), 0, _decompressionBuffer, i * 4, 4);
                    }

                    break;
                case CompressionType.Zlib:
                    try
                    {
                        // inflate the packet using built-in .net function.  Note that the first two bytes of the data are skipped, since this 
                        //  appears to be a standard zlib deflated buffer
                        System.IO.MemoryStream ms = new System.IO.MemoryStream(buffer, offset + 42, (int)header.length - 42);
                        using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                        {
                            // todo: need more graceful way of determing decompressed size!
                            ffxivMessageSize = ds.Read(_decompressionBuffer, 0, _decompressionBuffer.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("FFXIVBundleDecoder: Decompression error: " + ex.ToString(), "DEBUG-MACHINA");
                        return null;
                    }

                    break;
                case CompressionType.Oodle:
                    try
                    {
                        if (_oodle == null)
                        {
                            _oodle = Oodle.OodleFactory.Create();
                        }

                        bool success = _oodle.Decompress(
                            buffer,
                            offset + sizeof(Server_BundleHeader),
                            (int)header.length - sizeof(Server_BundleHeader),
                            _decompressionBuffer,
                            (int)header.uncompressed_length);
                        if (success)
                        {
                            ffxivMessageSize = (int)header.uncompressed_length;
                        }
                        else
                        {
                            Trace.WriteLine("FFXIVBundleDecoder: Oodle Decompression failure.", "DEBUG-MACHINA");
                            return null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("FFXIVBundleDecoder: Oodle Decompression error: " + ex.ToString(), "DEBUG-MACHINA");
                        return null;
                    }
                    break;
                default:
                    if (!_encodingError)
                        Trace.WriteLine($"FFXIVBundleDecoder: unknown bundle: version - {header.version.ToString("X2", CultureInfo.InvariantCulture)}; compression - {header.compression}", "DEBUG-MACHINA");
                    _encodingError = true;
                    return null;
            }

            return _decompressionBuffer;
        }

        /// <summary>
        /// Creates the Oodle decoder, so a state can be applied before the first bundle arrives.
        /// </summary>
        private void CreateOodle()
        {
            if (_oodle == null)
                _oodle = Oodle.OodleFactory.Create();
        }

        /// <summary>
        /// Applies an Oodle decompression state read from another process by
        /// <see cref="MemoryScanner"/>, and keeps it: a state is read at a point in time, so the
        /// bundles on hand can only be tried against it, never decoded by carrying on from where
        /// the decoder was.  See <see cref="RecoverLostBundle"/>.
        /// </summary>
        internal void SetOodleState(byte[] state, byte[] shared, byte[] window)
        {
            CreateOodle();

            // Apply it before keeping it: a state the decoder cannot take must not arm a
            // resynchronization.
            _oodle?.UpdateFrom(state, shared, window);

            _snapshot = new OodleState { State = state, Shared = shared, Window = window };
            _resyncBundles = 0;
        }

        private void RestoreOodleState()
        {
            if (_oodle != null && _snapshot != null)
                _oodle.UpdateFrom(_snapshot.Value.State, _snapshot.Value.Shared, _snapshot.Value.Window);
        }

        private void ClearSnapshot()
        {
            _snapshot = null;
            _resyncBundles = 0;
        }

        private static unsafe int GetNextMagicNumberPos(byte[] buffer, int currentOffset)
        {
            fixed (byte* ptr = buffer)
            {
                for (int nextOffset = currentOffset + 1; nextOffset <= buffer.Length - 4; nextOffset++)
                {
                    if (((uint*)(ptr + nextOffset))[0] == BundleMagic)
                        return nextOffset;
                }
            }

            return -1;
        }
    }
}
