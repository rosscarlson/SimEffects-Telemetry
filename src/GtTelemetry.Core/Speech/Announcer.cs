using System.Runtime.InteropServices;
using System.Speech.Synthesis;

namespace GtTelemetry.Core.Speech;

public enum Cue { Watching, Done }

/// <summary>Talks to the driver during guided captures, so they never need to look away from the road.</summary>
public interface IAnnouncer
{
    Task SayAsync(string text, CancellationToken ct = default);

    void Play(Cue cue);
}

/// <summary>Windows SAPI voice plus short synthesized tones for "watching now" and "done".</summary>
public sealed class SpeechAnnouncer : IAnnouncer, IDisposable
{
    private readonly SpeechSynthesizer synth = new();
    private static readonly byte[] WatchTone = Tone([(1046, 90)]);
    private static readonly byte[] DoneTone = Tone([(784, 110), (0, 40), (1046, 160)]);

    public SpeechAnnouncer(string? voice = null, int rate = 1)
    {
        synth.SetOutputToDefaultAudioDevice();
        Voice = voice;
        Rate = rate;
    }

    public bool Enabled { get; set; } = true;

    public string? Voice
    {
        get => synth.Voice.Name;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            try { synth.SelectVoice(value); } catch (ArgumentException) { /* voice not installed: keep default */ }
        }
    }

    /// <summary>−10 (slow) to 10 (fast).</summary>
    public int Rate { get => synth.Rate; set => synth.Rate = Math.Clamp(value, -10, 10); }

    public static IReadOnlyList<string> InstalledVoices()
    {
        using var s = new SpeechSynthesizer();
        return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
    }

    public async Task SayAsync(string text, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Prompt? prompt = null;
        EventHandler<SpeakCompletedEventArgs> done = (_, e) => { if (e.Prompt == prompt) tcs.TrySetResult(); };
        synth.SpeakCompleted += done;
        try
        {
            prompt = new Prompt(text);
            synth.SpeakAsync(prompt);
            using (ct.Register(() => { synth.SpeakAsyncCancel(prompt); tcs.TrySetCanceled(ct); }))
                await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            synth.SpeakCompleted -= done;
        }
    }

    public void Play(Cue cue)
    {
        if (!Enabled) return;
        var wav = cue == Cue.Watching ? WatchTone : DoneTone;
        // SND_MEMORY with SND_ASYNC needs the buffer kept alive; the tones are static, so that holds.
        PlaySound(wav, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
    }

    public void Dispose() => synth.Dispose();

    private const uint SndAsync = 0x0001, SndMemory = 0x0004, SndNoDefault = 0x0002;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);

    /// <summary>Builds a 16-bit mono WAV of sine segments (frequency 0 = silence) with short fades.</summary>
    private static byte[] Tone((int Hz, int Ms)[] segments)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (hz, ms) in segments)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - i) / (rate * 0.008));
                samples.Add(hz == 0 ? (short)0 : (short)(Math.Sin(2 * Math.PI * hz * i / rate) * 9000 * env));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int dataLen = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms2.ToArray();
    }
}
