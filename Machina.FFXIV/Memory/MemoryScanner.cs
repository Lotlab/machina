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
using System.Diagnostics;

namespace Machina.FFXIV.Memory
{
    /// <summary>
    /// Reads the live Oodle network decompression state out of a running FFXIV client.
    /// <para>
    /// The game keeps the send and receive Oodle states of both the zone and the chat
    /// network channel in its process memory.  When Machina loses sync with the Oodle
    /// stream it can copy that state into its own decoder, which allows decoding to
    /// continue without waiting for the game to re-train the dictionary.
    /// </para>
    /// <para>
    /// Reading another process requires sufficient privileges, so Machina typically has
    /// to run as administrator for this to work.
    /// </para>
    /// </summary>
    public sealed class MemoryScanner : IDisposable
    {
        /// <summary>
        /// Process name of the DX11 game client.
        /// </summary>
        public const string DefaultProcessName = "ffxiv_dx11";

        private const string FrameworkSignature = "48 8B 1D ?? ?? ?? ?? 8B 7C 24";

        // Offsets into the game's framework / networking objects.
        private const int FrameworkNetworkModuleProxyOffset = 0x1678;
        private const int NetworkModuleProxyValueOffset = 0x8;
        private const int ZoneClientOffset = 0xA70;
        private const int ChatClientOffset = 0xA78;
        private const int ZoneConnectionOffset = 0x98;
        private const int ChatConnectionOffset = 0xA0;
        private const int SessionOffset = 0x10;

        // Offsets into a network session and its oodle state.
        private const int TxSessionOffset = 0x190;
        private const int RxSessionOffset = 0x200;
        private const int SessionSharedOffset = 0x28;
        private const int SessionRootStateOffset = 0x30;
        private const int SessionWindowOffset = 0x58;
        private const int RootStateTcpOffset = 0x0;
        private const int RootStateUdpOffset = 0x8;

        private readonly ProcessMemoryReader _reader = new ProcessMemoryReader();
        private IntPtr _networkModule = IntPtr.Zero;
        private bool _disposed;

        public bool IsOpen => _reader.IsOpen;
        public int ProcessId => _reader.ProcessId;
        public IntPtr ModuleBase => _reader.ModuleBase;

        /// <summary>
        /// True when both the zone and the chat client were found in the game's network module.
        /// </summary>
        public bool NetworkConnected { get; private set; }

        /// <summary>
        /// Opens the first running game client, returning false when none is running.
        /// </summary>
        public bool Open(string processName = DefaultProcessName)
        {
            IReadOnlyList<Process> processes = ProcessMemoryReader.GetProcesses(processName);
            if (processes.Count == 0)
            {
                Close();
                return false;
            }

            try
            {
                Open(processes[0]);
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }

            return true;
        }

        public void Open(Process process)
        {
            _reader.Open(process);
            SearchNetworkingModule();
        }

        public void Open(int processId)
        {
            using (Process process = Process.GetProcessById(processId))
                Open(process);
        }

        public void Close()
        {
            _reader.Close();
            _networkModule = IntPtr.Zero;
            NetworkConnected = false;
        }

        /// <summary>
        /// Locates the game's network module.  This has to be repeated whenever the module
        /// may have been recreated, for example after a zone change.
        /// </summary>
        public bool SearchNetworkingModule()
        {
            NetworkConnected = false;
            _networkModule = IntPtr.Zero;

            if (!_reader.IsOpen)
                return false;

            ProcessMemoryReader.SignatureResult frameworkSignature = _reader.FindSignature(FrameworkSignature);
            if (!frameworkSignature.IsValid)
            {
                Trace.WriteLine($"{nameof(MemoryScanner)}.{nameof(SearchNetworkingModule)}: Failed to find the framework instance.", "DEBUG-MACHINA");
                return false;
            }

            IntPtr frameworkAddress = frameworkSignature.GetRelativeAddress(0);
            IntPtr framework = _reader.GetIntPtr(frameworkAddress);
            IntPtr networkModuleProxy = _reader.GetIntPtr(framework, FrameworkNetworkModuleProxyOffset);
            _networkModule = _reader.GetIntPtr(networkModuleProxy, NetworkModuleProxyValueOffset);

            if (_networkModule == IntPtr.Zero)
                return false;

            NetworkConnected = _reader.GetIntPtr(_networkModule, ZoneClientOffset) != IntPtr.Zero
                && _reader.GetIntPtr(_networkModule, ChatClientOffset) != IntPtr.Zero;

            return NetworkConnected;
        }

        /// <summary>
        /// Reads the current Oodle state of the zone network channel, or null when unavailable.
        /// </summary>
        public ClientState? GetZoneState(OodleStateLayout layout) => GetState(GetZoneSession(), layout);

        /// <summary>
        /// Reads the current Oodle state of the chat network channel, or null when unavailable.
        /// </summary>
        public ClientState? GetChatState(OodleStateLayout layout) => GetState(GetChatSession(), layout);

        private IntPtr GetZoneSession()
        {
            IntPtr client = _reader.GetIntPtr(_networkModule, ZoneClientOffset);
            if (client == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr connection = _reader.GetIntPtr(client, ZoneConnectionOffset); // astruct_8*
            return connection == IntPtr.Zero ? IntPtr.Zero : _reader.GetIntPtr(connection, SessionOffset); // SessionBase
        }

        private IntPtr GetChatSession()
        {
            IntPtr client = _reader.GetIntPtr(_networkModule, ChatClientOffset);
            if (client == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr connection = _reader.GetIntPtr(client, ChatConnectionOffset); // astruct_8*
            return connection == IntPtr.Zero ? IntPtr.Zero : _reader.GetIntPtr(connection, SessionOffset); // SessionBase
        }

        private ClientState? GetState(IntPtr session, OodleStateLayout layout)
        {
            if (session == IntPtr.Zero || !layout.IsValid)
                return null;

            OodleState? tx = ReadState(IntPtr.Add(session, TxSessionOffset), layout);
            OodleState? rx = ReadState(IntPtr.Add(session, RxSessionOffset), layout);
            if (tx == null || rx == null)
                return null;

            return new ClientState { Tx = tx.Value, Rx = rx.Value };
        }

        private OodleState? ReadState(IntPtr statePointer, OodleStateLayout layout)
        {
            IntPtr shared = _reader.GetIntPtr(statePointer, SessionSharedOffset);
            IntPtr rootState = _reader.GetIntPtr(statePointer, SessionRootStateOffset);
            IntPtr window = _reader.GetIntPtr(statePointer, SessionWindowOffset);
            IntPtr state = _reader.GetIntPtr(rootState, layout.IsUdp ? RootStateUdpOffset : RootStateTcpOffset);

            if (shared == IntPtr.Zero || window == IntPtr.Zero || state == IntPtr.Zero)
                return null;

            // The shared dictionary starts with the window bounds (m_lzp_window and
            // m_lzp_window_end), so its first 16 bytes have to point at the window buffer.
            // When they do not, the state is not laid out the way the local decoder expects:
            // give up rather than inject something that would corrupt the decoder.
            long windowAddress = window.ToInt64();
            long windowStart = _reader.GetIntPtr(shared, 0).ToInt64();
            long windowEnd = _reader.GetIntPtr(shared, 8).ToInt64();
            if (windowStart != windowAddress || windowEnd != windowAddress + layout.WindowSize)
            {
                Trace.WriteLine($"{nameof(MemoryScanner)}: Oodle shared data first 16 bytes do not match. " +
                    $"window {windowAddress:X16}, shared[0] {windowStart:X16}, shared[8] {windowEnd:X16}", "DEBUG-MACHINA");
                return null;
            }

            byte[] sharedData = _reader.Read(shared, layout.SharedSize);
            byte[] windowData = _reader.Read(window, layout.WindowSize);
            byte[] stateData = _reader.Read(state, layout.StateSize);

            if (sharedData == null || windowData == null || stateData == null)
                return null;

            return new OodleState
            {
                State = stateData,
                Shared = sharedData,
                Window = windowData,
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _reader.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
