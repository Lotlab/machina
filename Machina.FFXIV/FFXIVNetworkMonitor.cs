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
using System.Net;
using Machina.FFXIV.Deucalion;
using Machina.FFXIV.Memory;
using Machina.Infrastructure;

namespace Machina.FFXIV
{
    /// <summary>
    /// FFXIVNetworkMonitor is configured through the following properties after it is constructed:
    ///   MonitorType: Specifies whether it should use a winsock raw socket, or use WinPCap (requires separate kernel driver installation).  Default is a raw socket.
    ///   ProcessID: (optional) Specifies the process ID to record traffic from
    ///   ProcessIDList: Specifies a collection of process IDs to record traffic from
    ///     
    /// This class uses the Machina.TCPNetworkMonitor class to find and monitor the communication from Final Fantasy XIV.  It decodes the data thaat adheres to the
    ///   FFXIV network packet format and calls the message delegate when data is received.
    /// </summary>
    public class FFXIVNetworkMonitor : IDisposable
    {

        /// <summary>
        /// Specifies the type of monitor to use - Raw socket or WinPCap
        /// </summary>
        public NetworkMonitorType MonitorType
        { get; set; } = NetworkMonitorType.RawSocket;

        /// <summary>
        /// Specifies the Process ID that is generating or receiving the traffic.  Either ProcessID, ProcessIDList, or WindowName must be specified.
        /// </summary>
        public uint ProcessID
        { get; set; }

        public ICollection<uint> ProcessIDList
        { get; } = new List<uint>();

        /// <summary>
        /// Specifies the local IP address to override the detected IP
        /// </summary>
        public IPAddress LocalIP
        { get; set; } = IPAddress.None;

        /// <summary>
        /// Specifies whether to use Winsock/WinPcap server IP filtering instead of filtering in code
        ///   This has a small chance of losing data when new TCP sockets connect, but significantly reduces data processing overhead.
        /// </summary>
        public bool UseRemoteIpFilter
        { get; set; }

        /// <summary>
        /// The window name to use for game detection.
        /// </summary>
        public string WindowName
        { get; set; } = "FINAL FANTASY XIV";

        /// <summary>
        /// This class keeps the information needed to authenticate the user on a remote machine or read a local file via PCap.
        /// The remote machine can either grant or refuse the access according to the information provided. In case the NULL authentication is required, both 'username' and 'password' can be NULL pointers.
        /// </summary>
        public TCPNetworkMonitorConfig.RPCapConf RPCap
        { get; set; } = new TCPNetworkMonitorConfig.RPCapConf();

        /// <summary>
        /// the type of Oodle implementation to use - for most cases it should be FfxivTcp
        /// </summary>
        public Oodle.OodleImplementation OodleImplementation
        { get; set; } = Oodle.OodleImplementation.FfxivTcp;

        public string OodlePath
        { get; set; } = @"C:\Program Files (x86)\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe";

        /// <summary>
        /// Inject and use deucalion dll to receive game network data.  Deucalion is distributed here:
        ///   https://github.com/ff14wed/deucalion
        /// </summary>
        public bool UseDeucalion
        { get; set; }

        #region Message Delegates section
        public delegate void MessageReceived2(TCPConnection connection, long epoch, byte[] message);

        /// <summary>
        /// Specifies the delegate that is called when data is received and successfully decoded.
        /// </summary>
        public MessageReceived2 MessageReceivedEventHandler;

        public void OnMessageReceived(TCPConnection connection, long epoch, byte[] message)
        {
            MessageReceivedEventHandler?.Invoke(connection, epoch, message);
        }

        public delegate void MessageSent2(TCPConnection connection, long epoch, byte[] message);

        public MessageSent2 MessageSentEventHandler;

        public void OnMessageSent(TCPConnection connection, long epoch, byte[] message)
        {
            MessageSentEventHandler?.Invoke(connection, epoch, message);
        }

        #endregion

        private TCPNetworkMonitor _monitor;
        private DeucalionClient _deucalionClient;
        private bool _disposedValue;

        private readonly Dictionary<string, FFXIVBundleDecoder> _sentDecoders = new Dictionary<string, FFXIVBundleDecoder>();
        private readonly Dictionary<string, FFXIVBundleDecoder> _receivedDecoders = new Dictionary<string, FFXIVBundleDecoder>();

        private IOodleStateReader _memoryScanner;

        /// <summary>
        /// Creates the reader that copies Oodle state out of the game client.  It is only called
        /// when no reader has been created yet, and exists so the recovery policy can be tested
        /// without a game client.
        /// </summary>
        internal Func<IOodleStateReader> MemoryScannerFactory { get; set; } = () => new MemoryScanner();

        /// <summary>
        /// Validates the parameters and starts the monitor.
        /// </summary>
        public void Start()
        {
            if (_monitor != null)
            {
                _monitor.Stop();
                _monitor = null;
            }

            if (MessageReceivedEventHandler == null)
                throw new ArgumentException("MessageReceived delegate must be specified.");

            if (UseDeucalion)
            {
                if (ProcessID == 0)
                    throw new ArgumentException("ProcessID must be specified for Deucalion.");

                // Always wait 1 second before injecting Deucalion, in case it is shutting down.
                System.Threading.Thread.Sleep(1000);

                DeucalionInjector.LastInjectionError = string.Empty;
                bool isValid = DeucalionInjector.ValidateLibraryChecksum();
                if (isValid)
                {
                    // Note: if InjectLibrary fails, continue attempting to read from the game.  it is possible the library was already injected.
                    _ = DeucalionInjector.InjectLibrary((int)ProcessID);

                    _deucalionClient = new DeucalionClient();
                    _deucalionClient.MessageSent = (message) => ProcessDeucalionMessage(message, true);
                    _deucalionClient.MessageReceived = (message) => ProcessDeucalionMessage(message, false);
                    _deucalionClient.Connect((int)ProcessID);
                }
            }
            else
            {
                _monitor = new TCPNetworkMonitor();
                _monitor.Config.ProcessID = ProcessID;
                _monitor.Config.ProcessIDList = ProcessIDList;
                if (_monitor.Config.ProcessID == 0)
                    _monitor.Config.WindowName = WindowName;
                _monitor.Config.MonitorType = MonitorType;
                _monitor.Config.LocalIP = LocalIP;
                _monitor.Config.UseRemoteIpFilter = UseRemoteIpFilter;
                _monitor.Config.RPCap = RPCap;

                _monitor.DataSentEventHandler = (TCPConnection connection, byte[] data) => ProcessSentMessage(connection, data);
                _monitor.DataReceivedEventHandler = (TCPConnection connection, byte[] data) => ProcessReceivedMessage(connection, data);

                Oodle.OodleFactory.SetImplementation(OodleImplementation, OodlePath);
                _monitor.Start();
            }
        }

        /// <summary>
        /// Stops the monitor if it is active.
        /// </summary>
        public void Stop()
        {
            if (_monitor != null)
            {
                _monitor.DataSentEventHandler = null;
                _monitor.DataReceivedEventHandler = null;
                _monitor.Stop();
                _monitor.Dispose();
                _monitor = null;
            }

            if (_deucalionClient != null)
            {
                _deucalionClient.MessageSent = null;
                _deucalionClient.MessageReceived = null;
                _deucalionClient.Disconnect();
                _deucalionClient.Dispose();
                _deucalionClient = null;
            }

            _sentDecoders.Clear();
            _receivedDecoders.Clear();
        }

        /// <summary>
        /// Applies an Oodle decompression state to the decoder of the supplied connection and
        /// direction, creating that decoder when needed.  The send and the receive direction own
        /// separate states, so each is set on its own.
        /// </summary>
        private void SetOodleState(TCPConnection connection, bool isTx, byte[] state, byte[] shared, byte[] window)
        {
            GetOrCreateDecoder(connection, isTx).SetOodleState(state, shared, window);
        }

        private FFXIVBundleDecoder GetOrCreateDecoder(TCPConnection connection, bool isTx)
        {
            Dictionary<string, FFXIVBundleDecoder> decoders = isTx ? _sentDecoders : _receivedDecoders;
            if (!decoders.TryGetValue(connection.ID, out FFXIVBundleDecoder decoder))
            {
                decoder = CreateDecoder(connection, isTx);
                decoders[connection.ID] = decoder;
            }

            return decoder;
        }

        public void ProcessSentMessage(TCPConnection connection, byte[] data)
        {
            Tuple<long, byte[]> message;
            FFXIVBundleDecoder decoder = GetOrCreateDecoder(connection, true);

            decoder.StoreData(data);
            while ((message = decoder.GetNextFFXIVMessage()) != null)
            {
                OnMessageSent(connection, message.Item1, message.Item2);
            }
        }

        public void ProcessReceivedMessage(TCPConnection connection, byte[] data)
        {
            Tuple<long, byte[]> message;
            FFXIVBundleDecoder decoder = GetOrCreateDecoder(connection, false);

            decoder.StoreData(data);
            while ((message = decoder.GetNextFFXIVMessage()) != null)
            {
                OnMessageReceived(connection, message.Item1, message.Item2);
            }
        }

        private FFXIVBundleDecoder CreateDecoder(TCPConnection connection, bool isTx)
        {
            var decoder = new FFXIVBundleDecoder();
            decoder.DecodeFailedHandler += (dec) =>
            {
                // Deucalion already delivers decoded packets, so there is nothing to resync.
                if (UseDeucalion)
                    return false;

                return RecoverOodleState(dec, connection, isTx);
            };
            return decoder;
        }

        /// <summary>
        /// Copies the current Oodle state of the direction that lost sync out of the game process
        /// and applies it to its decoder.  Returns true when a state was installed, so the
        /// decoder can try the bundle that failed once more before dropping it.
        /// </summary>
        private bool RecoverOodleState(FFXIVBundleDecoder decoder, TCPConnection connection, bool isTx)
        {
            try
            {
                OodleStateLayout? layout = decoder.OodleLayout;
                if (layout == null || !layout.Value.IsValid)
                    return false;

                IOodleStateReader scanner = EnsureMemoryScanner();
                if (scanner == null)
                    return false;

                bool isChatChannel = IsChatChannel(connection);
                OodleState? state = isChatChannel
                    ? scanner.GetChatState(layout.Value, isTx)
                    : scanner.GetZoneState(layout.Value, isTx);

                if (state == null)
                {
                    Trace.WriteLine($"FFXIVNetworkMonitor: Unable to read the {Direction(isTx)} oodle state of {connection} from game memory.", "DEBUG-MACHINA");
                    return false;
                }

                decoder.SetOodleState(state.Value.State, state.Value.Shared, state.Value.Window);
                Trace.WriteLine($"FFXIVNetworkMonitor: {Direction(isTx)} oodle state of {connection} was restored from game memory.", "DEBUG-MACHINA");

                // Recovery itself only needs the direction that lost sync.  The other direction is
                // primed as well while it has never decoded anything, so a connection that just
                // started does not have to wait for its own first failure, while a direction that
                // is already in sync keeps its state.
                PrimeOtherDirection(connection, isChatChannel, layout.Value, isTx, scanner);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"FFXIVNetworkMonitor: Oodle state recovery for {connection} failed: {ex}", "DEBUG-MACHINA");
                return false;
            }
        }

        /// <summary>
        /// Gives the direction that did not fail a state while it has not decoded anything yet.
        /// The state is at or after the client's position, so the decoder validates its first
        /// bundles against it instead of trusting them.
        /// <para>
        /// Priming is a bonus on top of the recovery that was asked for, so nothing it does may
        /// escape: the direction that lost sync already has its state, and reporting the whole
        /// recovery as failed because the other direction could not be read would make the decoder
        /// throw away the bundle it was about to retry.
        /// </para>
        /// </summary>
        private void PrimeOtherDirection(TCPConnection connection, bool isChatChannel, OodleStateLayout layout, bool isTx, IOodleStateReader scanner)
        {
            try
            {
                Dictionary<string, FFXIVBundleDecoder> decoders = isTx ? _receivedDecoders : _sentDecoders;
                if (decoders.TryGetValue(connection.ID, out FFXIVBundleDecoder other) && other.HasDecodedMessages)
                    return;

                OodleState? state = isChatChannel
                    ? scanner.GetChatState(layout, !isTx)
                    : scanner.GetZoneState(layout, !isTx);
                if (state == null)
                    return;

                SetOodleState(connection, !isTx, state.Value.State, state.Value.Shared, state.Value.Window);
                Trace.WriteLine($"FFXIVNetworkMonitor: {Direction(!isTx)} oodle state of {connection} was primed from game memory.", "DEBUG-MACHINA");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"FFXIVNetworkMonitor: Priming the {Direction(!isTx)} oodle state of {connection} failed: {ex}", "DEBUG-MACHINA");
            }
        }

        private static string Direction(bool isTx) => isTx ? "send" : "receive";

        private IOodleStateReader EnsureMemoryScanner()
        {
            if (_memoryScanner == null)
                _memoryScanner = MemoryScannerFactory();

            return _memoryScanner.EnsureReady(TryOpenGameProcess) ? _memoryScanner : null;
        }

        private bool TryOpenGameProcess()
        {
            try
            {
                if (ProcessID != 0)
                {
                    _memoryScanner.Open((int)ProcessID);
                    return true;
                }

                // Resolve the window exactly like the packet capture does, so both ends read
                // the same client when several are running.  ProcessIDList is deliberately not
                // used: an Oodle state belongs to one process, and picking one of several would
                // be a guess.
                if (!string.IsNullOrWhiteSpace(WindowName))
                {
                    IList<uint> processIds = ProcessTCPInfo.GetProcessIDByWindow(null, WindowName);
                    if (processIds.Count > 0)
                    {
                        _memoryScanner.Open((int)processIds[0]);
                        return true;
                    }

                    Trace.WriteLine($"FFXIVNetworkMonitor: No process found for window '{WindowName}', falling back to the process name.", "DEBUG-MACHINA");
                }

                return _memoryScanner.Open();
            }
            catch (Exception ex)
            {
                // The game not being there, or not being readable, is expected: the client may
                // be starting or shutting down.  Report it instead of letting it escape.
                Trace.WriteLine($"FFXIVNetworkMonitor: Unable to open the game process for oodle state recovery: {ex.Message}", "DEBUG-MACHINA");
                return false;
            }
        }

        /// <summary>
        /// The chat channel is opened after the zone channel, so its local port is higher.
        /// </summary>
        private bool IsChatChannel(TCPConnection connection)
        {
            IList<TCPConnection> connections = _monitor?.Connections;
            if (connections == null)
                return false;

            bool isChatChannel = false;

            foreach (TCPConnection item in connections)
            {
                if (item.RemoteIP != connection.RemoteIP || item.RemotePort != connection.RemotePort)
                    continue;

                if (item.LocalPort < connection.LocalPort)
                    isChatChannel = true;
            }

            return isChatChannel;
        }

        public void ProcessDeucalionMessage(byte[] data, bool isSend)
        {
            // TCP Connection is irrelevent for this, but needed by interface, so make new one.
            TCPConnection connection = new TCPConnection();
            connection.ProcessId = ProcessID;

            (long epoch, byte[] packet) = DeucalionClient.ConvertDeucalionFormatToPacketFormat(data);

            if (isSend)
            {
                OnMessageSent(connection, epoch, packet);
            }
            else
            {
                OnMessageReceived(connection, epoch, packet);
            }
        }


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _monitor?.Dispose();
                    _memoryScanner?.Dispose();
                    _memoryScanner = null;
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
