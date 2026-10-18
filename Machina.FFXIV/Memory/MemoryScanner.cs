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
    public enum NetworkModuleStatus
    {
        Unavailable,

        /// <summary>
        /// The cached image of the main module no longer contains the framework code, so the
        /// game either restarted or was patched and the process has to be opened again.
        /// </summary>
        SignatureMissing,

        /// <summary>Transient while the client is connecting.</summary>
        ModuleNotReady,

        /// <summary>Transient during login: the zone and chat clients are not both there yet.</summary>
        ClientsNotReady,

        Connected,
    }

    /// <summary>
    /// Reads the live Oodle network decompression state out of a running FFXIV client.
    /// <para>
    /// The game keeps the send and receive Oodle states of both the zone and the chat
    /// network channel in its process memory.  When Machina loses sync with the Oodle
    /// stream it can copy that state into its own decoder, which allows decoding to
    /// continue without waiting for the game to re-train the dictionary.
    /// </para>
    /// <para>
    /// Locating the network module is split in two: the framework is looked up in the cached
    /// module image once per open, while the module and client pointers are re-read on every
    /// refresh, since the game recreates them on a zone change.
    /// </para>
    /// <para>
    /// Reading another process requires sufficient privileges, so Machina typically has
    /// to run as administrator for this to work.
    /// </para>
    /// </summary>
    public sealed class MemoryScanner : IOodleStateReader
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

        private IntPtr _frameworkAddress = IntPtr.Zero;
        private IntPtr _networkModule = IntPtr.Zero;
        private bool _disposed;

        public bool IsOpen => _reader.IsOpen;
        public int ProcessId => _reader.ProcessId;
        public IntPtr ModuleBase => _reader.ModuleBase;

        public NetworkModuleStatus ModuleStatus { get; private set; } = NetworkModuleStatus.Unavailable;

        /// <summary>
        /// True when both the zone and the chat client were found in the game's network module.
        /// </summary>
        public bool NetworkConnected => ModuleStatus == NetworkModuleStatus.Connected;

        /// <summary>
        /// Opens the first running game client, returning false when none is running.
        /// </summary>
        public bool Open() => Open(DefaultProcessName);

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

            // A new open means a new module image, so anything resolved for the previous one
            // has to go.
            _frameworkAddress = IntPtr.Zero;
            _networkModule = IntPtr.Zero;

            RefreshNetworkModule();
        }

        public void Open(int processId)
        {
            using (Process process = Process.GetProcessById(processId))
                Open(process);
        }

        public void Close()
        {
            _reader.Close();
            _frameworkAddress = IntPtr.Zero;
            _networkModule = IntPtr.Zero;
            SetStatus(NetworkModuleStatus.Unavailable);
        }

        public NetworkModuleStatus RefreshNetworkModule()
        {
            _networkModule = IntPtr.Zero;

            if (!_reader.IsOpen)
                return SetStatus(NetworkModuleStatus.Unavailable);

            if (_frameworkAddress == IntPtr.Zero && !ResolveFrameworkAddress())
                return SetStatus(NetworkModuleStatus.SignatureMissing);

            // The framework instance itself is read on every refresh, since the game may
            // replace it; only the address of the pointer is worth caching.
            if (!_reader.TryGetIntPtr(_frameworkAddress, 0, out IntPtr framework))
                return SetStatus(NetworkModuleStatus.SignatureMissing);

            if (framework == IntPtr.Zero)
                return SetStatus(NetworkModuleStatus.ModuleNotReady);

            if (!_reader.TryGetIntPtr(framework, FrameworkNetworkModuleProxyOffset, out IntPtr networkModuleProxy))
                return SetStatus(NetworkModuleStatus.SignatureMissing);

            if (networkModuleProxy == IntPtr.Zero)
                return SetStatus(NetworkModuleStatus.ModuleNotReady);

            if (!_reader.TryGetIntPtr(networkModuleProxy, NetworkModuleProxyValueOffset, out IntPtr networkModule))
                return SetStatus(NetworkModuleStatus.SignatureMissing);

            if (networkModule == IntPtr.Zero)
                return SetStatus(NetworkModuleStatus.ModuleNotReady);

            _networkModule = networkModule;

            if (!_reader.TryGetIntPtr(networkModule, ZoneClientOffset, out IntPtr zoneClient) ||
                !_reader.TryGetIntPtr(networkModule, ChatClientOffset, out IntPtr chatClient))
                return SetStatus(NetworkModuleStatus.SignatureMissing);

            return SetStatus(zoneClient != IntPtr.Zero && chatClient != IntPtr.Zero
                ? NetworkModuleStatus.Connected
                : NetworkModuleStatus.ClientsNotReady);
        }

        /// <summary>
        /// <paramref name="openProcess"/> decides which client to read, so that policy stays on
        /// the capture side; the scanner decides when it has to be called.  A network module
        /// that does not exist yet is not a reason to reopen, and reopening is deliberately not
        /// rate limited: a stale module image means the game restarted or was patched, where
        /// waiting only delays recovery.
        /// </summary>
        public bool EnsureReady(Func<bool> openProcess)
        {
            if (openProcess == null)
                throw new ArgumentNullException(nameof(openProcess));

            if (!IsOpen)
                return openProcess() && ModuleStatus == NetworkModuleStatus.Connected;

            switch (RefreshNetworkModule())
            {
                case NetworkModuleStatus.Connected:
                    return true;

                case NetworkModuleStatus.SignatureMissing:
                    Close();
                    return openProcess() && ModuleStatus == NetworkModuleStatus.Connected;

                default:
                    // Reopening would not help here, and it is the only expensive part.
                    return false;
            }
        }

        private bool ResolveFrameworkAddress()
        {
            ProcessMemoryReader.SignatureResult frameworkSignature = _reader.FindSignature(FrameworkSignature);
            if (!frameworkSignature.IsValid)
            {
                Trace.WriteLine($"{nameof(MemoryScanner)}: Failed to find the framework signature in the module image.", "DEBUG-MACHINA");
                return false;
            }

            _frameworkAddress = frameworkSignature.GetRelativeAddress(0);
            return true;
        }

        /// <summary>
        /// Reports a status change, and nothing when it repeats, so a stream that keeps failing
        /// to decode does not flood the trace.
        /// </summary>
        private NetworkModuleStatus SetStatus(NetworkModuleStatus status)
        {
            if (status == ModuleStatus)
                return status;

            ModuleStatus = status;
            Trace.WriteLine($"{nameof(MemoryScanner)}: Network module status is {status}.", "DEBUG-MACHINA");
            return status;
        }

        /// <summary>
        /// Reads the current Oodle state of the zone network channel, or null when unavailable.
        /// </summary>
        public ClientState? GetZoneState(OodleStateLayout layout) => GetState(GetZoneSession(), layout);

        /// <summary>
        /// Reads the current Oodle state of the chat network channel, or null when unavailable.
        /// </summary>
        public ClientState? GetChatState(OodleStateLayout layout) => GetState(GetChatSession(), layout);

        /// <summary>
        /// Reads one direction of the zone network channel's Oodle state, or null when
        /// unavailable.  Recovery only needs the direction that lost sync, and the state of a
        /// direction is a few megabytes, so it is worth asking for one at a time.
        /// </summary>
        public OodleState? GetZoneState(OodleStateLayout layout, bool isTx) => GetState(GetZoneSession(), layout, isTx);

        /// <summary>
        /// Reads one direction of the chat network channel's Oodle state, or null when
        /// unavailable.
        /// </summary>
        public OodleState? GetChatState(OodleStateLayout layout, bool isTx) => GetState(GetChatSession(), layout, isTx);

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

        private OodleState? GetState(IntPtr session, OodleStateLayout layout, bool isTx)
        {
            if (session == IntPtr.Zero || !layout.IsValid)
                return null;

            return ReadState(IntPtr.Add(session, isTx ? TxSessionOffset : RxSessionOffset), layout);
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
