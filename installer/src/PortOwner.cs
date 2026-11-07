// Which process listens on a TCP port (IPv4 and IPv6), via iphlpapi GetExtendedTcpTable: no admin rights needed and,
// unlike netstat, no localized text to parse. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GTAVRidingCompanion
{
    sealed class PortUser
    {
        public int Pid;
        public string Name;
        public string Path;
        public string Describe() { return Name + " (PID " + Pid + ")" + (String.IsNullOrEmpty(Path) ? "" : "\n" + Path); }
    }

    static class PortOwner
    {
        const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3, ErrorInsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, int reserved);

        /// <summary>Processes (other than this one) listening on [port]; empty when the port is free.</summary>
        internal static List<PortUser> Find(int port)
        {
            var pids = new HashSet<int>(Listeners(AfInet, 24, 8, 20, port).Concat(Listeners(AfInet6, 56, 20, 52, port)));
            pids.Remove(0);
            pids.Remove(Process.GetCurrentProcess().Id);
            return pids.Select(Describe).ToList();
        }

        /// <summary>Rows of MIB_TCPROW_OWNER_PID (24 bytes) or MIB_TCP6ROW_OWNER_PID (56 bytes): port and PID offsets per family.</summary>
        static IEnumerable<int> Listeners(int family, int rowSize, int portOffset, int pidOffset, int port)
        {
            var found = new List<int>();
            int size = 0;
            int result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
            if (result != ErrorInsufficientBuffer && result != 0) return found;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                    if (result == ErrorInsufficientBuffer) continue;
                    if (result != 0) return found;
                    int count = Marshal.ReadInt32(buffer);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = IntPtr.Add(buffer, 4 + i * rowSize);
                        int raw = Marshal.ReadInt32(row, portOffset);
                        int local = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                        if (local == port) found.Add(Marshal.ReadInt32(row, pidOffset));
                    }
                    return found;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return found;
        }

        static PortUser Describe(int pid)
        {
            var user = new PortUser { Pid = pid, Name = "process" };
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    user.Name = process.ProcessName + ".exe";
                    try { user.Path = process.MainModule.FileName; } catch (Exception) { /* Elevated or protected process. */ }
                }
            }
            catch (Exception) { }
            return user;
        }

        /// <summary>Ends those processes and waits up to 5 s for the port to be free. Returns an error text, or null.</summary>
        internal static string Free(int port, List<PortUser> users)
        {
            var problems = new List<string>();
            foreach (var user in users)
            {
                try
                {
                    using (var process = Process.GetProcessById(user.Pid))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
                catch (ArgumentException) { /* Already gone. */ }
                catch (Exception error) { problems.Add(user.Name + " (PID " + user.Pid + "): " + error.Message); }
            }
            for (int i = 0; i < 25 && Find(port).Count > 0; i++) Thread.Sleep(200);
            if (Find(port).Count > 0) problems.Add("TCP port " + port + " is still in use.");
            return problems.Count == 0 ? null : String.Join("\n", problems.ToArray());
        }
    }
}
