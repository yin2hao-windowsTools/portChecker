using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using PortChecker.Models;

namespace PortChecker.Services;

internal sealed class NativePortScanner
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int ErrorInsufficientBuffer = 122;
    private const int NoError = 0;

    public IReadOnlyList<PortSnapshot> GetActivePorts()
    {
        var ports = new List<PortSnapshot>();
        ports.AddRange(GetTcp4Rows());
        ports.AddRange(GetTcp6Rows());
        ports.AddRange(GetUdp4Rows());
        ports.AddRange(GetUdp6Rows());

        return ports;
    }

    private static IReadOnlyList<PortSnapshot> GetTcp4Rows()
    {
        var buffer = AllocateTableBuffer(
            (IntPtr table, ref int size) => GetExtendedTcpTable(table, ref size, true, AfInet, TcpTableClass.OwnerPidAll, 0));
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rows = new List<PortSnapshot>(rowCount);

            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, index * rowSize));
                rows.Add(new PortSnapshot(
                    PortProtocol.Tcp,
                    FormatIPv4(row.LocalAddr),
                    ConvertPort(row.LocalPort),
                    FormatIPv4(row.RemoteAddr),
                    ConvertPort(row.RemotePort),
                    FormatTcpState(row.State),
                    (int)row.OwningPid));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<PortSnapshot> GetTcp6Rows()
    {
        var buffer = AllocateTableBuffer(
            (IntPtr table, ref int size) => GetExtendedTcpTable(table, ref size, true, AfInet6, TcpTableClass.OwnerPidAll, 0));
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            var rows = new List<PortSnapshot>(rowCount);

            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(IntPtr.Add(rowPtr, index * rowSize));
                rows.Add(new PortSnapshot(
                    PortProtocol.Tcp,
                    FormatIPv6(row.LocalAddr, row.LocalScopeId),
                    ConvertPort(row.LocalPort),
                    FormatIPv6(row.RemoteAddr, row.RemoteScopeId),
                    ConvertPort(row.RemotePort),
                    FormatTcpState(row.State),
                    (int)row.OwningPid));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<PortSnapshot> GetUdp4Rows()
    {
        var buffer = AllocateTableBuffer(
            (IntPtr table, ref int size) => GetExtendedUdpTable(table, ref size, true, AfInet, UdpTableClass.OwnerPid, 0));
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var rows = new List<PortSnapshot>(rowCount);

            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(IntPtr.Add(rowPtr, index * rowSize));
                rows.Add(new PortSnapshot(
                    PortProtocol.Udp,
                    FormatIPv4(row.LocalAddr),
                    ConvertPort(row.LocalPort),
                    "-",
                    null,
                    "-",
                    (int)row.OwningPid));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<PortSnapshot> GetUdp6Rows()
    {
        var buffer = AllocateTableBuffer(
            (IntPtr table, ref int size) => GetExtendedUdpTable(table, ref size, true, AfInet6, UdpTableClass.OwnerPid, 0));
        try
        {
            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
            var rows = new List<PortSnapshot>(rowCount);

            for (var index = 0; index < rowCount; index++)
            {
                var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(IntPtr.Add(rowPtr, index * rowSize));
                rows.Add(new PortSnapshot(
                    PortProtocol.Udp,
                    FormatIPv6(row.LocalAddr, row.LocalScopeId),
                    ConvertPort(row.LocalPort),
                    "-",
                    null,
                    "-",
                    (int)row.OwningPid));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr AllocateTableBuffer(TableReader fillTable)
    {
        var size = 0;
        var result = fillTable(IntPtr.Zero, ref size);
        if (result is not ErrorInsufficientBuffer and not NoError)
        {
            throw new Win32Exception((int)result);
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = fillTable(buffer, ref size);
            if (result != NoError)
            {
                throw new Win32Exception((int)result);
            }

            return buffer;
        }
        catch
        {
            Marshal.FreeHGlobal(buffer);
            throw;
        }
    }

    private delegate uint TableReader(IntPtr table, ref int size);

    private static int ConvertPort(uint port)
    {
        var bytes = BitConverter.GetBytes(port);
        return (bytes[0] << 8) + bytes[1];
    }

    private static string FormatIPv4(uint address)
    {
        return new IPAddress(address).ToString();
    }

    private static string FormatIPv6(byte[] address, uint scopeId)
    {
        var ipAddress = new IPAddress(address, scopeId);
        return ipAddress.ToString();
    }

    private static string FormatTcpState(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTENING",
        3 => "SYN_SENT",
        4 => "SYN_RECEIVED",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT_1",
        7 => "FIN_WAIT_2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => $"UNKNOWN({state})"
    };

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        UdpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    private enum UdpTableClass
    {
        OwnerPid = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;

        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }
}
