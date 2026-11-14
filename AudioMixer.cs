using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ShotNova;

/// <summary>
/// Captura o som do computador (loopback do dispositivo de saída padrão) e/ou o microfone padrão,
/// mistura tudo em PCM 16 bits, 48 kHz, estéreo, e entrega em tempo real (preenchendo silêncio).
/// Cada fonte pode ser ligada e desligada a qualquer momento, inclusive durante a gravação;
/// desligada, o dispositivo é realmente fechado (o indicador de microfone do Windows apaga).
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int Rate = 48000;
    public const int Channels = 2;

    private sealed class Source
    {
        public bool Want;
        public WasapiCapture? Capture;
        public ISampleProvider? Input;
    }

    private readonly MixingSampleProvider _mix;
    private readonly object _gate = new();
    private readonly Source _system = new();
    private readonly Source _mic = new();
    private bool _running;
    private bool _disposed;

    /// <summary>Chamado quando uma fonte não pôde ser aberta (qualquer thread).</summary>
    public event Action<string>? Warn;

    public AudioMixer(bool systemSound, bool microphone)
    {
        _mix = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels)) { ReadFully = true };
        _system.Want = systemSound;
        _mic.Want = microphone;
    }

    public bool SystemOn { get { lock (_gate) return _system.Want; } }
    public bool MicOn { get { lock (_gate) return _mic.Want; } }

    public void SetSystem(bool on) { lock (_gate) { _system.Want = on; if (_running) Apply(_system, () => new WasapiLoopbackCapture(), "Som do computador"); } }
    public void SetMic(bool on) { lock (_gate) { _mic.Want = on; if (_running) Apply(_mic, () => new WasapiCapture(), "Microfone"); } }

    /// <summary>Abre ou fecha o dispositivo conforme o estado desejado. Chamar com _gate travado.</summary>
    private void Apply(Source s, Func<WasapiCapture> make, string label)
    {
        if (_disposed || s.Want == (s.Capture is not null)) return;
        if (!s.Want) { Close(s); return; }

        WasapiCapture? c = null;
        ISampleProvider? input = null;
        try
        {
            c = make();
            var buffer = new BufferedWaveProvider(c.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,                     // sem dados = silêncio (o loopback não envia nada quando está mudo)
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            c.DataAvailable += (_, e) => { try { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { } };
            input = ToMixFormat(buffer);
            _mix.AddMixerInput(input);
            c.StartRecording();
            s.Capture = c;
            s.Input = input;
        }
        catch (Exception ex)
        {
            if (input is not null) _mix.RemoveMixerInput(input);
            try { c?.Dispose(); } catch { }
            s.Want = false;
            Warn?.Invoke($"{label} indisponível: {ex.Message}");
        }
    }

    private void Close(Source s)
    {
        try { s.Capture?.StopRecording(); } catch { }
        try { s.Capture?.Dispose(); } catch { }
        s.Capture = null;
        if (s.Input is not null) { _mix.RemoveMixerInput(s.Input); s.Input = null; }
    }

    private static ISampleProvider ToMixFormat(IWaveProvider source)
    {
        var sp = source.ToSampleProvider();
        if (sp.WaveFormat.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
        if (sp.WaveFormat.Channels == 1) return new MonoToStereoSampleProvider(sp);
        if (sp.WaveFormat.Channels == 2) return sp;

        // 5.1 / 7.1: usa só o par frontal
        var m = new MultiplexingSampleProvider(new[] { sp }, 2);
        m.ConnectInputToOutput(0, 0);
        m.ConnectInputToOutput(1, 1);
        return m;
    }

    /// <summary>
    /// Começa a capturar e escreve áudio em tempo real no fluxo até <paramref name="stop"/> ficar verdadeiro.
    /// O relógio é o mesmo do vídeo: se o fluxo só abrir depois, o atraso é recuperado de uma vez.
    /// </summary>
    public void Pump(Stream output, Stopwatch clock, Func<bool> stop)
    {
        lock (_gate)
        {
            _running = true;
            Apply(_system, () => new WasapiLoopbackCapture(), "Som do computador");
            Apply(_mic, () => new WasapiCapture(), "Microfone");
        }

        long frames = 0;
        while (!stop())
        {
            var due = (long)(clock.Elapsed.TotalSeconds * Rate);
            var need = due - frames;
            if (need < Rate / 100) { Thread.Sleep(5); continue; } // pelo menos 10 ms

            var n = (int)Math.Min(need, Rate);
            var floats = new float[n * Channels];
            _mix.Read(floats, 0, floats.Length);

            var bytes = new byte[floats.Length * 2];
            for (var i = 0; i < floats.Length; i++)
            {
                var v = (short)Math.Clamp((int)Math.Round(floats[i] * 32767f), short.MinValue, short.MaxValue);
                bytes[2 * i] = (byte)(v & 0xFF);
                bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
            }
            output.Write(bytes, 0, bytes.Length);
            frames += n;
        }
        output.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Close(_system);
            Close(_mic);
        }
    }
}
