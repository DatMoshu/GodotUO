// Stand-ins for the few ModernUO types AccountPrompt.cs touches, so the
// patched file can be compiled and run on its own (test_account_prompt.py).
// Only what the patch uses; passwords are kept in plain text here.
using System;
using System.Collections.Generic;

namespace Server
{
    public enum AccessLevel { Player, Counselor, GameMaster, Seer, Administrator, Developer, Owner }

    public static class Core
    {
        public static bool Headless = true;
    }

    public static class ConsoleInputHandler
    {
        public static string ReadLine() => Console.ReadLine();
    }

    public static class StringExtensions
    {
        public static bool InsensitiveEquals(this string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public class Mobile
    {
        public string Name;
        public AccessLevel AccessLevel;
    }
}

namespace Server.Logging
{
    public interface ILogger
    {
        void Warning(string message, params object[] args);
        void Information(string message, params object[] args);
    }

    public static class LogFactory
    {
        public static readonly List<string> Lines = new();

        public static ILogger GetLogger(Type type) => new Logger();

        private class Logger : ILogger
        {
            public void Warning(string message, params object[] args) => Lines.Add("warn " + Format(message, args));
            public void Information(string message, params object[] args) => Lines.Add("info " + Format(message, args));

            private static string Format(string message, object[] args)
            {
                foreach (var arg in args)
                {
                    var open = message.IndexOf('{');
                    var close = open < 0 ? -1 : message.IndexOf('}', open);
                    if (close < 0)
                    {
                        break;
                    }
                    message = message[..open] + arg + message[(close + 1)..];
                }
                return message;
            }
        }
    }
}

namespace Server.Accounting
{
    public class Account
    {
        private string _password;
        private readonly List<Mobile> _mobiles = new();

        public Account(string username, string password)
        {
            Username = username;
            _password = password;
            Accounts.All[username] = this;
        }

        public string Username { get; }
        public AccessLevel AccessLevel { get; set; }
        public int Length => _mobiles.Count;
        public Mobile this[int index] => _mobiles[index];
        public string PlainPassword => _password;

        public bool CheckPassword(string password) => _password == password;
        public void SetPassword(string password) => _password = password;
        public void Add(Mobile m) => _mobiles.Add(m);
    }

    public static class Accounts
    {
        public static readonly Dictionary<string, Account> All = new(StringComparer.OrdinalIgnoreCase);

        public static int Count => All.Count;
        public static Account GetAccount(string username) => All.TryGetValue(username, out var a) ? a : null;
    }
}

namespace Server.Misc
{
    public static class ServerAccess
    {
        public static readonly List<string> Protected = new();

        public static void AddProtectedAccount(Server.Accounting.Account account, bool save) => Protected.Add(account.Username);
    }
}
