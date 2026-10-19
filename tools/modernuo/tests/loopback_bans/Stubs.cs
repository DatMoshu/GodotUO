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
    public static class AddressExtensions
    {
        public static UInt128 ToUInt128(this IPAddress ip)
        {
            UInt128 value = 0;
            foreach (byte b in ip.MapToIPv6().GetAddressBytes()) value = (value << 8) | b;
            return value;
        }
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
