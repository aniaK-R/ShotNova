using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShotNova;

/// <summary>
/// Grava uma região da tela em MP4. O ShotNova captura os quadros (em pixels físicos, com o cursor)
/// e os entrega crus ao ffmpeg, que só codifica (H.264). O ffmpeg roda como processo separado.
/// </summary>
public sealed class ScreenRecorder
{
    public const int Fps = 30;

    private readonly Func<Rectangle> _region;
    private readonly Size _size;
    private readonly StringBuilder _log = new();
    private readonly AudioMixer? _audio;
    private readonly Stopwatch _clock = new();   // relógio comum a vídeo e áudio (mantém a sincronia)
    private Process? _ff;
    private Thread? _thread;
    private Thread? _audioThread;
    private NamedPipeServerStream? _pipe;
    private volatile bool _stop;
    private volatile string? _loopError;

    public string Output { get; }

    public ScreenRecorder(Func<Rectangle> region, Size size, string output, AudioMixer? audio = null)
    {
        _region = region;
        _size = size;
        Output = output;
        _audio = audio;
    }

    /// <summary>Procura o ffmpeg ao lado do app, no PATH (atual, do usuário e do sistema) e no atalho do winget.</summary>
    public static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };
        foreach (var list in paths)
        {
            foreach (var dir in (list ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { /* entrada de PATH inválida */ }
            }
        }

        var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(links) ? links : null;
    }

    public void Start(string ffmpegPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        var args = new System.Collections.Generic.List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-thread_queue_size", "512",
            "-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", $"{_size.Width}x{_size.Height}",
            "-framerate", Fps.ToString(), "-i", "-"
        };

        var withAudio = _audio is { HasSources: true };
        if (withAudio)
        {
            var name = "shotnova_audio_" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte);
            args.AddRange(new[]
            {
                "-thread_queue_size", "512",
                "-f", "s16le", "-ar", AudioMixer.Rate.ToString(), "-ac", AudioMixer.Channels.ToString(),
                "-i", @"\\.\pipe\" + name,
                "-map", "0:v:0", "-map", "1:a:0"
            });
        }

        args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" });
        if (withAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
        args.AddRange(new[] { "-movflags", "+faststart", Output });
        foreach (var a in args) psi.ArgumentList.Add(a);

        _ff = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        _ff.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_log) { _log.AppendLine(e.Data); if (_log.Length > 4000) _log.Remove(0, _log.Length - 4000); }
        };
        _ff.BeginErrorReadLine();

        _clock.Restart();
        _thread = new Thread(Loop) { IsBackground = true, Name = "ShotNova.Recorder" };
        _thread.Start();

        if (withAudio)
        {
            // o ffmpeg só abre o canal de áudio depois de sondar o vídeo; por isso o áudio roda em outra thread
            _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "ShotNova.Audio" };
            _audioThread.Start();
        }
    }

    private void AudioLoop()
    {
        try
        {
            _pipe!.WaitForConnection();
            _audio!.Pump(_pipe, _clock, () => _stop);
        }
        catch (Exception ex)
        {
            if (!_stop) _loopError = "Falha no áudio: " + ex.Message;
        }
    }

    /// <summary>Encerra a gravação e finaliza o arquivo. Devolve null se deu certo, ou o motivo da falha.</summary>
    public string? Stop()
    {
        _stop = true;
        _thread?.Join(5000);
        _audioThread?.Join(3000);
        try { _pipe?.Dispose(); } catch { }   // fecha o canal: o ffmpeg vê o fim do áudio
        try { _audio?.Dispose(); } catch { }
        try { _ff?.StandardInput.Close(); } catch { }
        if (_ff is not null && !_ff.WaitForExit(30000))
        {
            try { _ff.Kill(); } catch { }
            return "O ffmpeg demorou demais para finalizar o vídeo.";
        }

        string log;
        lock (_log) log = _log.ToString().Trim();
        if (_ff is { ExitCode: not 0 }) return string.IsNullOrEmpty(log) ? "O ffmpeg terminou com erro." : log;
        if (!File.Exists(Output) || new FileInfo(Output).Length == 0) return "O vídeo não foi gerado.";
        return _loopError;
    }

    private void Loop()
    {
        var w = _size.Width;
        var h = _size.Height;
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(bmp);
            var buf = new byte[w * h * 4];
            var pipe = _ff!.StandardInput.BaseStream;
            var sw = _clock;
            long written = 0;

            while (!_stop)
            {
                var r = _region();
                g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                DrawCursor(g, r.X, r.Y);

                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(data.Scan0, buf, 0, buf.Length); // stride = w * 4
                bmp.UnlockBits(data);

                // mantém a duração real: se a captura atrasar, repete o quadro para compensar
                var target = (long)(sw.Elapsed.TotalSeconds * Fps) + 1;
                while (written < target)
                {
                    pipe.Write(buf, 0, buf.Length);
                    written++;
                }

                var wait = written * 1000.0 / Fps - sw.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
            }
            pipe.Flush();
        }
        catch (Exception ex)
        {
            _loopError = "Falha durante a gravação: " + ex.Message;
        }
    }

    private static void DrawCursor(Graphics g, int originX, int originY)
    {
        var ci = new Win.CURSORINFO { cbSize = Marshal.SizeOf<Win.CURSORINFO>() };
        if (!Win.GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return; // 1 = cursor visível
        if (!Win.GetIconInfo(ci.hCursor, out var ii)) return;
        try
        {
            var hdc = g.GetHdc();
            try
            {
                Win.DrawIconEx(hdc, ci.ptScreenPos.X - ii.xHotspot - originX, ci.ptScreenPos.Y - ii.yHotspot - originY,
                    ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); // DI_NORMAL
            }
            finally { g.ReleaseHdc(hdc); }
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) Win.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Win.DeleteObject(ii.hbmColor);
        }
    }
}
