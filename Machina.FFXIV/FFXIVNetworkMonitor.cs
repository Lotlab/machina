﻿// Copyright © 2021 Ravahn - All Rights Reserved
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
using System.Net;
using Machina.FFXIV.Deucalion;
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
        public enum ConnectionType
        {
            Game,
            Lobby
        }
        
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

        public Oodle.OodleImplementation OodleImplementation
        { get; set; } = Oodle.OodleImplementation.FfxivTcp;

        public bool UseDeucalion
        { get; set; }

        public string OodlePath
        { get; set; } = @"C:\Program Files (x86)\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe";

        #region Message Delegates section
        public delegate void MessageReceived2(TCPConnection connection, long epoch, byte[] message, int set, ConnectionType connectionType);

        /// <summary>
        /// Specifies the delegate that is called when data is received and successfully decoded.
        /// </summary>
        public MessageReceived2 MessageReceivedEventHandler;

        public void OnMessageReceived(TCPConnection connection, long epoch, byte[] message, int set, ConnectionType connectionType)
        {
            MessageReceivedEventHandler?.Invoke(connection, epoch, message, set, connectionType);
        }

        public delegate void MessageSent2(TCPConnection connection, long epoch, byte[] message, int set, ConnectionType connectionType);

        public MessageSent2 MessageSentEventHandler;

        public void OnMessageSent(TCPConnection connection, long epoch, byte[] message, int set, ConnectionType connectionType)
        {
            MessageSentEventHandler?.Invoke(connection, epoch, message, set, connectionType);
        }

        #endregion

        private TCPNetworkMonitor _monitor;
        private DeucalionClient _deucalionClient;
        private bool _disposedValue;
        private bool _deucalionSet = false;

        private readonly Dictionary<string, FFXIVBundleDecoder> _sentDecoders = new Dictionary<string, FFXIVBundleDecoder>();
        private readonly Dictionary<string, FFXIVBundleDecoder> _receivedDecoders = new Dictionary<string, FFXIVBundleDecoder>();

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

                string library = DeucalionInjector.ExtractLibrary();

                // Note: if InjectLibrary fails, continue attempting to read from the game.  it is possible the library was already injected.
                _ = DeucalionInjector.InjectLibrary((int)ProcessID, library);

                _deucalionClient = new DeucalionClient();
                _deucalionClient.MessageSent = (message, conn) => ProcessDeucalionMessage(message, true, conn);
                _deucalionClient.MessageReceived = (message, conn) => ProcessDeucalionMessage(message, false, conn);
                _deucalionClient.Connect((int)ProcessID);
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

        public void ProcessSentMessage(TCPConnection connection, byte[] data)
        {
            Tuple<long, byte[], int> message;
            if (!_sentDecoders.ContainsKey(connection.ID))
                _sentDecoders.Add(connection.ID, new FFXIVBundleDecoder());

            _sentDecoders[connection.ID].StoreData(data);
            ConnectionType connType = ConnectionType.Game;
            if (connection.RemotePort == 54994)
                connType = ConnectionType.Lobby;
            
            while ((message = _sentDecoders[connection.ID].GetNextFFXIVMessage()) != null)
            {
                OnMessageSent(connection, message.Item1, message.Item2, message.Item3, connType);
            }
        }

        public void ProcessReceivedMessage(TCPConnection connection, byte[] data)
        {
            Tuple<long, byte[], int> message;
            if (!_receivedDecoders.ContainsKey(connection.ID))
                _receivedDecoders.Add(connection.ID, new FFXIVBundleDecoder());

            _receivedDecoders[connection.ID].StoreData(data);
            ConnectionType connType = ConnectionType.Game;
            if (connection.RemotePort == 54994)
                connType = ConnectionType.Lobby;
            
            while ((message = _receivedDecoders[connection.ID].GetNextFFXIVMessage()) != null)
            {
                OnMessageReceived(connection, message.Item1, message.Item2, message.Item3, connType);
            }

        }

        public void ProcessDeucalionMessage(byte[] data, bool isSend, ConnectionType connectionType)
        {
            // TCP Connection is irrelevent for this, but needed by interface, so make new one.
            TCPConnection connection = new TCPConnection();
            connection.ProcessId = ProcessID;

            int set = _deucalionSet ? 0 : 1;
            _deucalionSet = !_deucalionSet;

            (long epoch, byte[] packet) = DeucalionClient.ConvertDeucalionFormatToPacketFormat(data);

            if (isSend)
            {
                OnMessageSent(connection, epoch, packet, set, connectionType);
            }
            else
            {
                OnMessageReceived(connection, epoch, packet, set, connectionType);
            }
        }


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _monitor?.Dispose();
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
