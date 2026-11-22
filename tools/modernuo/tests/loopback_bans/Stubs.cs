// SPDX-License-Identifier: BSD-2-Clause
// Only runtime services are stand-ins. Ban policy, channel and denylist are real pinned sources.
using System;
using System.Net;

namespace Server
{
    public static class Core { public static long TickCount; }
    public class Timer
    {
        public static Timer DelayCall(TimeSpan delay, TimeSpan interval, Action action) => new();
        public void Stop() { }
    }
    // The two Utility members IPAddressUtility (compiled for real) needs since the pin's IPv6 bans
    // (modernuo/ModernUO#2747). WriteMappedIPv6To is copied from Utility.cs; CIDR parsing is not exercised.
    public static class Utility
    {
        public static void WriteMappedIPv6To(this IPAddress ipAddress, Span<byte> destination)
        {
            if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                ipAddress.TryWriteBytes(destination, out _);
                return;
            }

            destination[..8].Clear();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), 0xFFFF);
            ipAddress.TryWriteBytes(destination.Slice(12, 4), out _);
        }

        public static UInt128 CreateCidrAddress(ReadOnlySpan<byte> bytes, int prefixLength, bool isMax) =>
            throw new NotSupportedException("CIDR parsing is outside this test");
    }
}
namespace Server.Logging
{
    public interface ILogger
    {
        void Information(string message, params object[] args);
        void Warning(string message, params object[] args);
        void Warning(Exception error, string message, params object[] args);
        void Error(Exception error, string message, params object[] args);
        void Debug(string message, params object[] args);
    }
    public static class LogFactory
    {
        public static ILogger GetLogger(Type type) => new NullLogger();
        sealed class NullLogger : ILogger
        {
            public void Information(string message, params object[] args) { }
            public void Warning(string message, params object[] args) { }
            public void Warning(Exception error, string message, params object[] args) { }
            public void Error(Exception error, string message, params object[] args) { }
            public void Debug(string message, params object[] args) { }
        }
    }
}
namespace Server.Network
{
    public static class ConnectionFilters { public static void Register(IConnectionFilter filter) { } }
    public static class ManualAllowlist { public static bool Contains(IPAddress address) => false; }
    public static class LoginAllowlist
    {
        public static bool IsExemptFromEscalation(IPAddress address, string reason) => false;
    }
    public static class AutoDenylistConfiguration
    {
        public static void Load() { }
        public static SettingsValue Settings { get; } = new();
        public sealed class SettingsValue
        {
            public bool Enabled => true;
            public TimeSpan Duration => TimeSpan.FromMinutes(15);
            public int MaxEntries => 100;
        }
    }
}
