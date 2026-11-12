// Protokoll: eine Datei pro Tag im Arbeitsordner, zusaetzlich stderr (landet im MCP-Log von Claude Code).

using System;
using System.IO;
using System.Text;

namespace Herzog.TiaHmi
{
    internal sealed class Log
    {
        private readonly string _dir;
        private readonly object _lock = new object();

        public Log(string dir)
        {
            _dir = dir;
        }

        public void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(Path.Combine(_dir, "tia-hmi-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (IOException)
                {
                    // Protokoll darf den Server nicht anhalten.
                }
                catch (UnauthorizedAccessException)
                {
                }
                Console.Error.WriteLine(line);
            }
        }
    }
}
