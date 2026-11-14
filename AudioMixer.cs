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
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int Rate = 48000;
    public const int Channels = 2;

    private readonly MixingSampleProvider _mix;
    private WasapiCapture? _system;
    private WasapiCapture? _mic;

    /// <summary>Há pelo menos uma fonte de áudio funcionando.</summary>
    public bool HasSources { get; private set; }

    /// <summary>Avisos de fontes que não puderam ser abertas (ex.: sem microfone).</summary>
    public string? Warning { get; private set; }

    public AudioMixer(bool systemSound, bool microphone)
    {
        _mix = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels)) { ReadFully = true };

        if (systemSound)
        {
            try { _system = Open(new WasapiLoopbackCapture()); HasSources = true; }
            catch (Exception ex) { Warning += "Som do computador indisponível: " + ex.Message + " "; }
        }
        if (microphone)
        {
            try { _mic = Open(new WasapiCapture()); HasSources = true; }
            catch (Exception ex) { Warning += "Microfone indisponível: " + ex.Message + " "; }
        }
    }

    private WasapiCapture Open(WasapiCapture capture)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,                       // sem dados = silêncio (o loopback não envia nada quando está mudo)
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _mix.AddMixerInput(ToMixFormat(buffer));
        return capture;
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
        _system?.StartRecording();
        _mic?.StartRecording();

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
        try { _system?.StopRecording(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        _system?.Dispose();
        _mic?.Dispose();
    }
}
