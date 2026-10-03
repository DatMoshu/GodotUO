// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Utility;
using GUO.Utility.Collections;
using GUO.Utility.Logging;
using GUO.Localization;

namespace GUO.Game.Managers
{
    internal sealed class JournalManager
    {
        private StreamWriter _fileWriter;
        private bool _writerHasException;
        // PORT DEVIATION (GUO): opt-in journal translation; the logic lives in GUO.Localization.
        private static long _nextEntryId;
        private readonly JournalTranslationHost _translation = new();
        public static int TranslationRevision => JournalTranslationHost.Revision;
        public string TranslationStatus => _translation.Status;
        public long LastTranslationMilliseconds => _translation.LastMilliseconds;

        public static Deque<JournalEntry> Entries { get; } = new Deque<JournalEntry>(Constants.MAX_JOURNAL_HISTORY_COUNT);

        public event EventHandler<JournalEntry> EntryAdded;


        public void Add(string text, ushort hue, string name, uint? serial, TextType type, bool isunicode = true, MessageType messageType = MessageType.Regular, bool translate = false)
        {
            JournalEntry entry = Entries.Count >= Constants.MAX_JOURNAL_HISTORY_COUNT ? Entries.RemoveFromFront() : new JournalEntry();

            byte font = (byte) (isunicode ? 0 : 9);

            if (ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.OverrideAllFonts)
            {
                font = ProfileManager.CurrentProfile.ChatFont;
                isunicode = ProfileManager.CurrentProfile.OverrideAllFontsIsUnicode;
            }

            DateTime timeNow = DateTime.Now;

            entry.Text = text;
            // PORT DEVIATION (GUO): id lets a late translation find its recycled entry.
            entry.Id = ++_nextEntryId;
            entry.Translation = null;
            entry.Font = font;
            entry.Hue = hue;
            entry.Name = name;
            entry.IsUnicode = isunicode;
            entry.Time = timeNow;
            entry.TextType = type;
            entry.MessageType = messageType;

            if (ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.ForceUnicodeJournal)
            {
                entry.Font = 0;
                entry.IsUnicode = true;
            }

            Entries.AddToBack(entry);
            EntryAdded.Raise(entry);

            // PORT DEVIATION (GUO): hand eligible public speech to the off-thread translator.
            if (translate)
                _translation.Queue(entry);

            if (_fileWriter == null && !_writerHasException)
            {
                CreateWriter();
            }

            bool saveSerial = ProfileManager.GlobalProfile != null && ProfileManager.GlobalProfile.JournalFileWithSerial;
            string serialText = saveSerial && serial.HasValue ? $"<0x{serial.Value:X8}> " : string.Empty;

            string output = $"[{timeNow:G}]  {serialText}{name}: {text}";

            if (string.IsNullOrWhiteSpace(name))
            {
                output = $"[{timeNow:G}]  {serialText}{text}";
            }

            _fileWriter?.WriteLine(output);
        }

        private void CreateWriter()
        {
            if (_fileWriter == null && ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.SaveJournalToFile)
            {
                try
                {
                    string path = FileSystemHelper.CreateFolderIfNotExists(Path.Combine(CUOEnviroment.ExecutablePath, "Data"), "Client", "JournalLogs");

                    int maxJournalFiles = ProfileManager.GlobalProfile?.MaxJournalFiles ?? -1;

                    if (maxJournalFiles >= 0)
                    {
                        try
                        {
                            string[] files = Directory.GetFiles(path, "*_journal.txt");
                            Array.Sort(files);
                            Array.Reverse(files);

                            for (int i = files.Length - 1; i >= maxJournalFiles; --i)
                            {
                                File.Delete(files[i]);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Failed to delete old journal files. Original Error: {ex.Message}");
                        }
                    }

                    _fileWriter = new StreamWriter(File.Open(Path.Combine(path, $"{DateTime.Now:yyyy_MM_dd_HH_mm_ss}_journal.txt"), FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true
                    };
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                    // we don't want to wast time.
                    _writerHasException = true;
                }
            }
        }

        public void CloseWriter()
        {
            _fileWriter?.Flush();
            _fileWriter?.Dispose();
            _fileWriter = null;
        }

        public void Clear()
        {
            //Entries.Clear();
            ResetTranslation(); // PORT DEVIATION (GUO)
            CloseWriter();
        }

        // PORT DEVIATION (GUO): World.Update calls this on the game thread.
        public void UpdateTranslation(System.Collections.Generic.ISet<string> ignored) => _translation.Update(Entries, ignored);

        public void ResetTranslation() => _translation.Reset();
    }

    internal class JournalEntry
    {
        // PORT DEVIATION (GUO): translation shown under the untouched original text.
        public long Id;
        public string Translation;
        public string DisplayText => Translation == null ? Text : Text + "\n" + Translation;
        public byte Font;
        public ushort Hue;

        public bool IsUnicode;
        public string Name;
        public string Text;

        public TextType TextType;
        public DateTime Time;

        public MessageType MessageType;
    }
}