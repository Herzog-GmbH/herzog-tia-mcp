// Bild und Video aus dem TIA-Fenster, damit Claude Ergebnisse selbst zeigen kann. Liest nur Pixel, spricht kein Openness an.
// PrintWindow funktioniert auch, wenn TIA hinter einem anderen Fenster liegt. Ist TIA minimiert, gibt es nichts zu sehen.
// Video: Einzelbilder, daraus mit ffmpeg ein MP4, falls ffmpeg im Pfad liegt. Sonst ein GIF ueber den eigenen Schreiber unten.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Herzog.TiaHmi
{
    internal sealed class VideoResult
    {
        public string File;
        public string Format;
        public int Frames;
        public double Seconds;
        public long Bytes;
        public string Note;
    }

    internal static class TiaWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        private const uint RenderFullContent = 2;

        public static Bitmap Capture(int processId)
        {
            SetProcessDPIAware();
            Process p = Process.GetProcessById(processId);
            IntPtr hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero) throw new ToolException("TIA hat kein sichtbares Fenster.");
            if (IsIconic(hwnd)) throw new ToolException("TIA ist minimiert. Bitte das Fenster wiederherstellen, ein minimiertes Fenster liefert kein Bild.");
            Rect r;
            if (!GetWindowRect(hwnd, out r)) throw new ToolException("Fenstergroesse von TIA nicht lesbar.");
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) throw new ToolException("TIA-Fenster hat keine Groesse.");
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, RenderFullContent)) throw new ToolException("Windows liefert kein Bild des TIA-Fensters.");
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            return bmp;
        }

        public static Bitmap Scale(Bitmap source, int maxWidth)
        {
            if (maxWidth <= 0 || source.Width <= maxWidth) return source;
            int h = (int)Math.Round(source.Height * (double)maxWidth / source.Width);
            var scaled = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, maxWidth, h);
            }
            return scaled;
        }

        public static void SaveJpeg(Bitmap bmp, string file, long quality)
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var p = new EncoderParameters(1))
            {
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bmp.Save(file, codec, p);
            }
        }

        // ---------- Aufnahme ----------

        // Nimmt in einem eigenen Thread auf, solange der Hauptablauf laeuft (z. B. ein Widget abspielen).
        public sealed class Recorder : IDisposable
        {
            private readonly int _pid;
            private readonly int _fps;
            private readonly int _maxWidth;
            private readonly string _dir;
            private readonly Thread _thread;
            private volatile bool _stop;
            private readonly Stopwatch _watch = new Stopwatch();
            private Exception _error;

            public List<string> Frames = new List<string>();
            public double Seconds { get { return _watch.Elapsed.TotalSeconds; } }

            public Recorder(int pid, int fps, int maxWidth, string dir)
            {
                _pid = pid;
                _fps = Math.Max(1, Math.Min(30, fps));
                _maxWidth = maxWidth;
                _dir = dir;
                Directory.CreateDirectory(dir);
                _thread = new Thread(Loop) { IsBackground = true, Name = "Aufnahme" };
            }

            public void Start()
            {
                _watch.Start();
                _thread.Start();
            }

            private void Loop()
            {
                try
                {
                    double period = 1000.0 / _fps;
                    int n = 0;
                    while (!_stop)
                    {
                        using (Bitmap raw = Capture(_pid))
                        {
                            Bitmap scaled = Scale(raw, _maxWidth);
                            string f = Path.Combine(_dir, "f" + n.ToString("000000") + ".jpg");
                            SaveJpeg(scaled, f, 88);
                            if (!ReferenceEquals(scaled, raw)) scaled.Dispose();
                            Frames.Add(f);
                        }
                        n++;
                        double wait = n * period - _watch.Elapsed.TotalMilliseconds;
                        if (wait > 1) Thread.Sleep((int)wait);
                    }
                }
                catch (Exception ex)
                {
                    _error = ex;
                }
            }

            public void Stop()
            {
                _stop = true;
                _thread.Join(10000);
                _watch.Stop();
                if (_error != null && Frames.Count == 0) throw new ToolException("Aufnahme gescheitert: " + _error.Message);
            }

            public void Dispose()
            {
                _stop = true;
            }
        }

        // Aus den Einzelbildern das Video. format: mp4 oder gif, leer = mp4, wenn ffmpeg da ist.
        public static VideoResult Encode(List<string> frames, double seconds, string target, string format)
        {
            if (frames.Count == 0) throw new ToolException("Keine Einzelbilder aufgenommen.");
            double fps = Math.Max(1, frames.Count / Math.Max(0.1, seconds));
            string ffmpeg = FindFfmpeg();
            bool gif = string.Equals(format, "gif", StringComparison.OrdinalIgnoreCase) || (ffmpeg == null && !string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase));
            var result = new VideoResult { Frames = frames.Count, Seconds = Math.Round(seconds, 1) };
            if (!gif)
            {
                if (ffmpeg == null) throw new ToolException("Fuer MP4 fehlt ffmpeg im Pfad. Format gif geht ohne.");
                string list = Path.Combine(Path.GetDirectoryName(frames[0]), "f%06d.jpg");
                result.File = Path.ChangeExtension(target, ".mp4");
                var psi = new ProcessStartInfo(ffmpeg, "-y -loglevel error -framerate " + fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                    " -i \"" + list + "\" -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -c:v libx264 -preset medium -crf 26 -pix_fmt yuv420p -movflags +faststart \"" + result.File + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                };
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new ToolException("ffmpeg meldet: " + err);
                }
                result.Format = "mp4";
            }
            else
            {
                result.File = Path.ChangeExtension(target, ".gif");
                WriteGif(frames, result.File, (int)Math.Round(100 / fps));
                result.Format = "gif";
                if (ffmpeg == null) result.Note = "ffmpeg liegt nicht im Pfad, darum GIF statt MP4.";
            }
            result.Bytes = new FileInfo(result.File).Length;
            return result;
        }

        private static string FindFfmpeg()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(';'))
            {
                try
                {
                    string f = Path.Combine(d.Trim(), "ffmpeg.exe");
                    if (File.Exists(f)) return f;
                }
                catch (ArgumentException)
                {
                    // Ungueltiger Eintrag im Pfad.
                }
            }
            return null;
        }

        // Animiertes GIF aus Einzelbildern: Jedes Bild speichert GDI+ als GIF, hier werden sie zusammengesetzt (Netscape-Schleife, lokale Farbtabelle).
        private static void WriteGif(List<string> frames, string file, int delayHundredths)
        {
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                bool first = true;
                foreach (string f in frames)
                {
                    byte[] gif;
                    using (var bmp = new Bitmap(f))
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Gif);
                        gif = ms.ToArray();
                    }
                    int colorFlag = gif[10];
                    int tableSize = (colorFlag & 0x80) != 0 ? 3 << ((colorFlag & 7) + 1) : 0;
                    int pos = 13 + tableSize;
                    if (first)
                    {
                        w.Write(gif, 0, 6);
                        w.Write(gif, 6, 4);       // Breite, Hoehe
                        w.Write((byte)(colorFlag & 0x70));   // keine globale Farbtabelle
                        w.Write((byte)0);
                        w.Write((byte)0);
                        w.Write(new byte[] { 0x21, 0xFF, 0x0B });
                        w.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                        w.Write(new byte[] { 3, 1, 0, 0, 0 });
                        first = false;
                    }
                    // Vorhandene Erweiterungen ueberspringen.
                    while (gif[pos] == 0x21)
                    {
                        pos += 2;
                        while (gif[pos] != 0) pos += gif[pos] + 1;
                        pos++;
                    }
                    if (gif[pos] != 0x2C) throw new ToolException("GIF-Einzelbild unerwartet aufgebaut.");
                    int delay = Math.Max(2, delayHundredths);
                    w.Write(new byte[] { 0x21, 0xF9, 4, 4, (byte)(delay & 0xFF), (byte)(delay >> 8), 0, 0 });
                    w.Write(gif, pos, 9);      // Bildbeschreibung ohne Flags
                    int bits = (colorFlag & 7);
                    w.Write((byte)(0x80 | bits));
                    w.Write(gif, 13, tableSize);
                    int data = pos + 10;
                    int end = data + 1;
                    while (gif[end] != 0) end += gif[end] + 1;
                    w.Write(gif, data, end - data + 1);
                }
                w.Write((byte)0x3B);
            }
        }
    }
}
