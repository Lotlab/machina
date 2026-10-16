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
    /// Read-only access to the memory of another process, plus a simple x86-64 byte
    /// signature scanner for its main module.  This is used to recover Oodle network
    /// state directly from a running game client.
    /// </summary>
    internal sealed class ProcessMemoryReader : IDisposable
    {
        private IntPtr _handle = IntPtr.Zero;
        private byte[] _moduleData;
        private bool _disposed;

        public bool IsOpen => _handle != IntPtr.Zero;
        public int ProcessId { get; private set; }
        public IntPtr ModuleBase { get; private set; }
        public int ModuleSize { get; private set; }

        public static IReadOnlyList<Process> GetProcesses(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return Array.Empty<Process>();

            return Process.GetProcessesByName(processName);
        }

        /// <summary>
        /// Opens the supplied process and snapshots its main module for signature scanning.
        /// </summary>
        public void Open(Process process)
        {
            if (process == null)
                throw new ArgumentNullException(nameof(process));

            Close();

            ProcessId = process.Id;
            _handle = MemoryNativeMethods.OpenProcess(
                MemoryNativeMethods.PROCESS_VM_READ | MemoryNativeMethods.PROCESS_QUERY_INFORMATION,
                false, (uint)process.Id);

            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"Unable to open process {process.Id} for reading. Machina may need to run elevated.");

            try
            {
                ProcessModule module = process.MainModule;
                if (module == null)
                    throw new InvalidOperationException($"Unable to query the main module of process {process.Id}.");

                ModuleBase = module.BaseAddress;
                ModuleSize = module.ModuleMemorySize;

                // Best effort: keep whatever could be read, unread pages remain zero.
                _moduleData = new byte[ModuleSize];
                int read = Read(ModuleBase, _moduleData);
                if (read <= 0)
                    throw new InvalidOperationException($"Unable to read the main module of process {process.Id}.");
            }
            catch
            {
                Close();
                throw;
            }
        }

        public void Close()
        {
            if (_handle != IntPtr.Zero)
            {
                MemoryNativeMethods.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }

            ProcessId = 0;
            ModuleBase = IntPtr.Zero;
            ModuleSize = 0;
            _moduleData = null;
        }

        /// <summary>
        /// Reads memory into <paramref name="buffer"/>, returning the number of bytes actually read.
        /// </summary>
        public int Read(IntPtr address, byte[] buffer)
        {
            if (!IsOpen || buffer == null || buffer.Length == 0)
                return 0;

            if (!MemoryNativeMethods.ReadProcessMemory(_handle, address, buffer, (IntPtr)buffer.Length, out IntPtr bytesRead))
                return 0;

            return (int)bytesRead;
        }

        /// <summary>
        /// Reads <paramref name="length"/> bytes, or returns null when the whole range could not be read.
        /// </summary>
        public byte[] Read(IntPtr address, int length)
        {
            if (length <= 0)
                return Array.Empty<byte>();

            var buffer = new byte[length];
            return Read(address, buffer) == length ? buffer : null;
        }

        public IntPtr GetIntPtr(IntPtr address, int offset = 0)
        {
            byte[] data = Read(IntPtr.Add(address, offset), 8);
            return data == null ? IntPtr.Zero : new IntPtr(BitConverter.ToInt64(data, 0));
        }

        public int GetInt32(IntPtr address, int offset = 0)
        {
            byte[] data = Read(IntPtr.Add(address, offset), 4);
            return data == null ? 0 : BitConverter.ToInt32(data, 0);
        }

        #region signature scanning

        /// <summary>
        /// Scans the main module of the process for a byte signature.  Both <c>??</c> and
        /// <c>**</c> are accepted as wildcard characters.
        /// </summary>
        public SignatureResult FindSignature(string signature, int offset = 0)
        {
            if (!IsOpen || _moduleData == null)
                return default;

            var pattern = SignaturePattern.Parse(signature);
            if (pattern.Bytes.Length == 0 || pattern.Bytes.Length > _moduleData.Length)
                return default;

            int end = _moduleData.Length - pattern.Bytes.Length;
            for (int i = Math.Max(offset, 0); i <= end; i++)
            {
                if (!pattern.Matches(_moduleData, i))
                    continue;

                var bytes = new byte[pattern.Bytes.Length];
                Array.Copy(_moduleData, i, bytes, 0, bytes.Length);
                return new SignatureResult(IntPtr.Add(ModuleBase, i), bytes, pattern);
            }

            return default;
        }

        public SignatureResult FindSignature(string signature, IntPtr searchStart)
        {
            if (searchStart == IntPtr.Zero)
                return FindSignature(signature);

            return FindSignature(signature, (int)(searchStart.ToInt64() - ModuleBase.ToInt64()));
        }

        internal struct SignaturePattern
        {
            public byte[] Bytes { get; private set; }
            public bool[] Mask { get; private set; }

            public static SignaturePattern Parse(string signature)
            {
                if (string.IsNullOrWhiteSpace(signature))
                    return default;

                string[] parts = signature.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var bytes = new byte[parts.Length];
                var mask = new bool[parts.Length];

                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "??" || parts[i] == "**")
                    {
                        bytes[i] = 0;
                        mask[i] = false;
                    }
                    else
                    {
                        bytes[i] = Convert.ToByte(parts[i], 16);
                        mask[i] = true;
                    }
                }

                return new SignaturePattern { Bytes = bytes, Mask = mask };
            }

            public bool Matches(byte[] data, int offset)
            {
                for (int i = 0; i < Bytes.Length; i++)
                {
                    if (Mask[i] && data[offset + i] != Bytes[i])
                        return false;
                }

                return true;
            }

            /// <summary>
            /// Returns the start and end offset of the n-th run of wildcard bytes.
            /// </summary>
            public (int Start, int End) GetNthWildcardRange(int n)
            {
                int count = 0;
                for (int i = 0; i < Mask.Length; i++)
                {
                    if (Mask[i])
                        continue;

                    int j = i;
                    while (j < Mask.Length && !Mask[j])
                        j++;

                    if (count == n)
                        return (i, j);

                    count++;
                    i = j;
                }

                return (0, 0);
            }
        }

        internal readonly struct SignatureResult
        {
            private readonly SignaturePattern _pattern;

            public IntPtr Address { get; }
            public byte[] Bytes { get; }

            public bool IsValid => Address != IntPtr.Zero && Bytes != null;

            internal SignatureResult(IntPtr address, byte[] bytes, SignaturePattern pattern)
            {
                Address = address;
                Bytes = bytes;
                _pattern = pattern;
            }

            public int GetInt32(int wildcardIndex)
            {
                (int start, int end) = _pattern.GetNthWildcardRange(wildcardIndex);
                return end - start == 4 && Bytes != null ? BitConverter.ToInt32(Bytes, start) : 0;
            }

            /// <summary>
            /// Resolves the RIP-relative address referenced by the n-th wildcard group.
            /// </summary>
            public IntPtr GetRelativeAddress(int wildcardIndex)
            {
                (int start, int end) = _pattern.GetNthWildcardRange(wildcardIndex);
                if (end - start != 4 || Bytes == null)
                    return IntPtr.Zero;

                int offset = BitConverter.ToInt32(Bytes, start);
                return IntPtr.Add(Address, offset + start + sizeof(int));
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            Close();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~ProcessMemoryReader()
        {
            Close();
        }
    }
}
