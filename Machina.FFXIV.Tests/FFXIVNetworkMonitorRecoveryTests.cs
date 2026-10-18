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

using System.Collections.Generic;
using Machina.FFXIV.Tests.Utility;
using Machina.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Machina.FFXIV.Tests
{
    /// <summary>
    /// Covers which Oodle state the monitor reads when a decoder loses sync.  A client is replaced
    /// by <see cref="FakeOodleStateReader"/> and the Oodle decoder by
    /// <see cref="FakeOodleWrapper"/>, so no game client or Oodle library is needed.
    /// </summary>
    [TestClass]
    public class FFXIVNetworkMonitorRecoveryTests
    {
        private static TCPConnection CreateConnection()
        {
            return new TCPConnection
            {
                LocalIP = 0x0100007F,
                LocalPort = 50000,
                RemoteIP = 0x0200007F,
                RemotePort = 54992,
                ProcessId = 1,
            };
        }

        /// <summary>
        /// Feeds the failing direction a bundle that cannot decode, so its decoder asks for a
        /// state.  The first part of the bundle only makes the decoder exist, which is what lets
        /// the test install a stand-in Oodle decoder before the failure.
        /// </summary>
        private static void ProvokeSendFailure(FFXIVNetworkMonitor monitor, TCPConnection connection, FakeOodleWrapper wrapper)
        {
            byte[] damaged = TestBundles.Uncompressed(2, TestBundles.Message(0xAA), TestBundles.Message(0xBB, padding: 0x1234));

            byte[] head = new byte[TestBundles.HeaderSize / 2];
            byte[] tail = new byte[damaged.Length - head.Length];
            System.Array.Copy(damaged, 0, head, 0, head.Length);
            System.Array.Copy(damaged, head.Length, tail, 0, tail.Length);

            // The first part only makes the decoder exist, which is what lets the test install a
            // stand-in Oodle decoder before the bundle fails.
            monitor.ProcessSentMessage(connection, head);
            TestReflection.InjectOodleWrapper(TestReflection.GetDecoder(monitor, connection.ID, true), wrapper);
            monitor.ProcessSentMessage(connection, tail);
        }

        [TestMethod]
        public void RecoveryReadsTheFailedDirectionAndPrimesTheIdleOne()
        {
            FakeOodleStateReader reader = new();
            FakeOodleWrapper wrapper = new();
            TCPConnection connection = CreateConnection();

            FFXIVNetworkMonitor sut = new() { ProcessID = connection.ProcessId, MemoryScannerFactory = () => reader };

            ProvokeSendFailure(sut, connection, wrapper);

            // The failed direction, and - because it has not decoded anything yet - the other one.
            CollectionAssert.AreEqual(
                new List<string> { "zone send", "zone receive" },
                Describe(reader.Requests));

            Assert.IsGreaterThanOrEqualTo(1, wrapper.UpdateCount, "The state that was read has to be applied to the decoder that lost sync.");
            Assert.IsNotNull(TestReflection.GetDecoder(sut, connection.ID, false), "The idle direction is expected to be primed with a decoder of its own.");
        }

        [TestMethod]
        public void RecoveryLeavesADirectionThatIsInSyncAlone()
        {
            FakeOodleStateReader reader = new();
            FakeOodleWrapper wrapper = new();
            TCPConnection connection = CreateConnection();

            FFXIVNetworkMonitor sut = new() { ProcessID = connection.ProcessId, MemoryScannerFactory = () => reader };

            // The receive direction decodes a bundle, so it is in sync and keeps its state.
            sut.ProcessReceivedMessage(connection, TestBundles.Uncompressed(1, TestBundles.Message(0xCC)));

            ProvokeSendFailure(sut, connection, wrapper);

            CollectionAssert.AreEqual(new List<string> { "zone send" }, Describe(reader.Requests));
        }

        /// <summary>
        /// Priming the direction that did not fail is extra, so a client that cannot be read for it
        /// must not turn the recovery that was asked for into a failure: the state read for the
        /// direction that lost sync is installed either way, and the bundle that failed is retried
        /// with it.
        /// </summary>
        [TestMethod]
        public void RecoveryStillAppliesTheFailedDirectionWhenPrimingFails()
        {
            // Only the primed direction is unreadable: the read the recovery asked for succeeds.
            FakeOodleStateReader reader = new() { Unreadable = isTx => !isTx };
            FakeOodleWrapper wrapper = new();
            TCPConnection connection = CreateConnection();

            FFXIVNetworkMonitor sut = new() { ProcessID = connection.ProcessId, MemoryScannerFactory = () => reader };

            ProvokeSendFailure(sut, connection, wrapper);

            CollectionAssert.AreEqual(
                new List<string> { "zone send", "zone receive" },
                Describe(reader.Requests));

            // One install for the direction that lost sync, then one restore as it tries the bundle
            // that failed again.
            Assert.IsGreaterThanOrEqualTo(2, wrapper.UpdateCount, $"Expected the failed bundle to be retried with the state that was read, the state was applied {wrapper.UpdateCount} time(s).");
        }

        private static List<string> Describe(List<System.Tuple<bool, bool>> requests)
        {
            List<string> result = new();
            foreach (System.Tuple<bool, bool> request in requests)
                result.Add($"{(request.Item1 ? "chat" : "zone")} {(request.Item2 ? "send" : "receive")}");

            return result;
        }
    }
}
