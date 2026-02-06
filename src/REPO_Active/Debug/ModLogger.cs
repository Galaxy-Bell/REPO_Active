using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace REPO_Active.Debug
{
    public sealed class ModLogger
    {
        private readonly object _lock = new object();
        private string _filePath = "";

        public bool Enabled { get; set; }

        public ModLogger(ManualLogSource log, bool enabled)
        {
            Enabled = enabled;
            if (Enabled) InitFile();
        }

        private void InitFile()
        {
            try
            {
                string dir = Path.Combine(Paths.ConfigPath, "REPO_Active", "logs");
                Directory.CreateDirectory(dir);
                string name = $"REPO_Active_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                _filePath = Path.Combine(dir, name);
                lock (_lock)
                {
                    File.AppendAllText(_filePath, $"[FileLog] {_filePath}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // file logging is optional; don't crash if it fails
            }
        }

        public void Log(string message)
        {
            if (!Enabled) return;
            if (string.IsNullOrEmpty(_filePath))
            {
                InitFile();
                if (string.IsNullOrEmpty(_filePath)) return;
            }
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_filePath, message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // swallow logging errors to avoid breaking gameplay
            }
        }
    }
}
