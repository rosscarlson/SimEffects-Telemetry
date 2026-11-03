using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;
using GtTelemetry.Core.Speech;

namespace GtTelemetry.Core.Sessions;

public sealed class SessionOptions
{
    public uint Start { get; set; }

    public uint Length { get; set; } = Pcsx2Connection.RamSize;

    /// <summary>Upper bound on snapshots per second (process memory can go much faster than is useful).</summary>
    public double MaxSampleHz { get; set; } = 20;

    /// <summary>Where to write results (and optional raw snapshots). Null = don't write anything.</summary>
    public string? CaptureRoot { get; set; }

    /// <summary>Write each step's final 32 MB snapshot for offline analysis.</summary>
    public bool SaveSnapshots { get; set; }

    public int MaxResults { get; set; } = 400;
}

public enum SessionPhase { Intro, Speaking, Settling, Sampling, StepDone, Analyzing, Done }

public sealed record SessionProgress(SessionPhase Phase, int StepIndex, int StepCount, string Message, int Samples, IReadOnlyDictionary<ValueKind, long> Survivors);

public sealed record Candidate(uint Address, ValueKind Kind, double[] StepValues, double Score, string Notes)
{
    public string AddressText => ValueKinds.FormatAddress(Address);
}

public sealed record SessionResult(
    SessionScript Script,
    IReadOnlyDictionary<ValueKind, long> Survivors,
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<int> SamplesPerStep,
    TimeSpan Duration,
    string? CaptureDir)
{
    public long TotalSurvivors => Survivors.Values.Sum();
}

/// <summary>
/// Runs a guided capture: speaks each step, samples full RAM snapshots while the driver performs it, and narrows
/// candidates with the step's expectations plus cross-step label ordering. See docs/scanning.md.
/// </summary>
public sealed class SessionRunner(ISnapshotSource connection, IAnnouncer announcer, SessionOptions options)
{
    public event Action<SessionProgress>? Progress;

    public async Task<SessionResult> RunAsync(SessionScript script, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        uint start = options.Start & ~3u;
        int length = (int)(Math.Min(options.Length, Pcsx2Connection.RamSize - start) & ~3u);
        var kinds = script.Kinds();
        var bits = kinds.ToDictionary(k => k, k => new BitSet(length / k.Size(), allSet: true));
        var pos = kinds.ToDictionary(k => k, k => new BitSet(length / k.Size(), allSet: false));
        var neg = kinds.ToDictionary(k => k, k => new BitSet(length / k.Size(), allSet: false));
        var survivors = kinds.ToDictionary(k => k, k => bits[k].Count());

        var first = new byte[length];
        var prev = new byte[length];
        var cur = new byte[length];
        var stepFinal = new List<byte[]>();               // last snapshot of each step
        var labelRep = new Dictionary<double, int>();     // label → index of the first step that carried it
        var samplesPerStep = new List<int>();
        int n = script.Steps.Count;

        string? captureDir = null;
        if (options.CaptureRoot != null)
        {
            captureDir = Path.Combine(options.CaptureRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{script.Id}");
            Directory.CreateDirectory(captureDir);
        }

        void Report(SessionPhase phase, int step, string msg, int samples = 0) =>
            Progress?.Invoke(new SessionProgress(phase, step, n, msg, samples, new Dictionary<ValueKind, long>(survivors)));

        Report(SessionPhase.Intro, -1, script.Intro);
        await announcer.SayAsync(script.Intro, ct);
        await Task.Delay(800, ct);

        for (int si = 0; si < n; si++)
        {
            var step = script.Steps[si];
            Report(SessionPhase.Speaking, si, step.Say);
            await announcer.SayAsync(step.Say, ct);
            Report(SessionPhase.Settling, si, step.Say);
            await Task.Delay(TimeSpan.FromSeconds(step.Settle), ct);

            announcer.Play(Cue.Watching);
            int samples = 0;
            var window = Stopwatch.StartNew();
            double minInterval = 1000.0 / options.MaxSampleHz;
            while (samples < 2 || window.Elapsed.TotalSeconds < step.Sample)
            {
                ct.ThrowIfCancellationRequested();
                var t0 = window.Elapsed.TotalMilliseconds;
                await Task.Run(() => connection.ReadBulk(start, cur), ct);

                if (samples == 0)
                {
                    Buffer.BlockCopy(cur, 0, first, 0, length);
                }
                else
                {
                    await Task.Run(() => ApplyWithinStep(step, kinds, bits, survivors, cur, first, prev), ct);
                }
                (prev, cur) = (cur, prev);
                samples++;
                Report(SessionPhase.Sampling, si, step.Say, samples);

                double wait = minInterval - (window.Elapsed.TotalMilliseconds - t0);
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            }
            samplesPerStep.Add(samples);

            // prev now holds the step's last snapshot.
            var final = prev.ToArray();
            stepFinal.Add(final);
            await Task.Run(() => ApplyEndOfStep(script, step, si, kinds, bits, pos, neg, survivors, final, first, stepFinal, labelRep), ct);
            if (options.SaveSnapshots && captureDir != null)
                await File.WriteAllBytesAsync(Path.Combine(captureDir, $"step{si + 1:00}.bin"), final, ct);

            long total = survivors.Values.Sum();
            Report(SessionPhase.StepDone, si, $"{total:N0} candidates left", samples);
        }

        Report(SessionPhase.Analyzing, n, "Ranking candidates");
        announcer.Play(Cue.Done);
        var sayOutro = announcer.SayAsync(script.Outro, ct);
        var candidates = await Task.Run(() => Rank(script, kinds, bits, stepFinal, start, options.MaxResults), ct);
        await sayOutro;

        var result = new SessionResult(script, new Dictionary<ValueKind, long>(survivors), candidates, samplesPerStep, clock.Elapsed, captureDir);
        if (captureDir != null) WriteResults(result, captureDir, start, length);
        Report(SessionPhase.Done, n, $"{result.TotalSurvivors:N0} candidates");
        return result;
    }

    private static void ApplyWithinStep(SessionStep step, ValueKind[] kinds, Dictionary<ValueKind, BitSet> bits, Dictionary<ValueKind, long> survivors,
        byte[] cur, byte[] first, byte[] prev)
    {
        foreach (var k in kinds)
        {
            double eps = Math.Max(k.IsFloat() ? 1e-6 : 0, step.ToleranceAbs);
            survivors[k] = step.Expect switch
            {
                StepExpect.Stable => Kernels.Filter(k, bits[k], cur, first, new WithinTolerance(step.Tolerance, eps)),
                StepExpect.Rising => Kernels.Filter(k, bits[k], cur, prev, new NotBelow(step.Tolerance, eps)),
                StepExpect.Falling => Kernels.Filter(k, bits[k], cur, prev, new NotAbove(step.Tolerance, eps)),
                _ => survivors[k],
            };
        }
    }

    private static void ApplyEndOfStep(SessionScript script, SessionStep step, int si, ValueKind[] kinds,
        Dictionary<ValueKind, BitSet> bits, Dictionary<ValueKind, BitSet> pos, Dictionary<ValueKind, BitSet> neg, Dictionary<ValueKind, long> survivors,
        byte[] final, byte[] first, List<byte[]> stepFinal, Dictionary<double, int> labelRep)
    {
        foreach (var k in kinds)
        {
            double eps = k.Epsilon();
            survivors[k] = step.Expect switch
            {
                StepExpect.Rising => Kernels.Filter(k, bits[k], final, first, new NetChange(+1, step.Tolerance, eps)),
                StepExpect.Falling => Kernels.Filter(k, bits[k], final, first, new NetChange(-1, step.Tolerance, eps)),
                StepExpect.Changing => Kernels.Filter(k, bits[k], final, first, new Differs(eps)),
                _ => survivors[k],
            };

            if (step.Label is not double label) continue;
            if (labelRep.TryGetValue(label, out int same))
            {
                survivors[k] = Kernels.Filter(k, bits[k], final, stepFinal[same],
                    new WithinTolerance(script.RepeatTolerance, Math.Max(k.IsFloat() ? 1e-6 : 0, step.ToleranceAbs)));
            }
            foreach (var (otherLabel, idx) in labelRep)
            {
                if (otherLabel == label) continue;
                survivors[k] = Kernels.Direction(k, bits[k], pos[k], neg[k], final, stepFinal[idx], label > otherLabel ? +1 : -1);
            }
        }
        if (step.Label is double l && !labelRep.ContainsKey(l)) labelRep[l] = si;
    }

    private static List<Candidate> Rank(SessionScript script, ValueKind[] kinds, Dictionary<ValueKind, BitSet> bits, List<byte[]> stepFinal, uint start, int max)
    {
        var labels = script.Steps.Select(s => s.Label).ToArray();
        bool labelled = labels.Where(l => l.HasValue).Distinct().Count() >= 2;
        var list = new List<Candidate>();
        var floatAddrs = new HashSet<uint>();
        if (kinds.Contains(ValueKind.F32))
            foreach (int i in bits[ValueKind.F32].Indices().Take(200_000)) floatAddrs.Add(start + (uint)i * 4);

        foreach (var k in kinds)
        {
            int size = k.Size();
            int taken = 0;
            foreach (int i in bits[k].Indices())
            {
                uint addr = start + (uint)(i * size);
                // A float's bytes also pass as integers (u32 at the same address, u16/u8 fragments inside it);
                // keep the float reading only.
                if (!k.IsFloat() && floatAddrs.Contains(addr & ~3u)) continue;
                var values = stepFinal.Select(s => k.Read(s.AsSpan(i * size))).ToArray();
                var (score, notes) = Score(k, values, labels, labelled);
                list.Add(new Candidate(addr, k, values, score, notes));
                if (++taken >= 50_000) break;
            }
        }
        // The same small integer reads identically as u8, u16 and u32 when the upper bytes are static; keep the narrowest.
        var deduped = list
            .GroupBy(c => (c.Address, Signature: string.Join(",", c.StepValues), IsFloat: c.Kind.IsFloat()))
            .Select(g => g.OrderBy(c => c.Kind.Size()).ThenBy(c => c.Kind).First());
        return deduped.OrderByDescending(c => c.Score).ThenBy(c => c.Address).Take(max).ToList();
    }

    private static (double Score, string Notes) Score(ValueKind k, double[] values, double?[] labels, bool labelled)
    {
        var notes = new List<string>();
        double score = 0;
        if (labelled)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            for (int i = 0; i < values.Length; i++)
                if (labels[i] is double l) { xs.Add(l); ys.Add(values[i]); }
            double r = Pearson(xs, ys);
            score = Math.Abs(r);
            if (r < 0) notes.Add("inverted");
            if (xs.Zip(ys).All(p => Math.Abs(p.First - p.Second) < 1e-4)) { score += 0.5; notes.Add("equals label"); }
            else if (xs.Zip(ys).All(p => Math.Abs(p.First + 1 - p.Second) < 1e-4)) { score += 0.3; notes.Add("label + 1"); }
            else if (CleanSwing(xs, ys)) { score += 0.4; notes.Add("clean swing"); }
        }
        double range = values.Max() - values.Min();
        double magnitude = values.Max(Math.Abs);
        // Real inputs swing across much of their scale; values that barely move (relative to their size) are noise.
        bool tinySwing = range < 0.02 * magnitude;
        if (tinySwing) { score *= 0.5; notes.Add("tiny swing"); }
        if (k.IsFloat() && !tinySwing && values.All(v => Math.Abs(v) <= 1.0001) && values.Any(v => v != 0)) { score += 0.2; notes.Add("normalized"); }
        notes.Add($"range {k.Format(range)}");
        return (score, string.Join(", ", notes));
    }

    /// <summary>Lowest-label steps read identically, highest-label steps read identically, and the two clearly differ:
    /// typical of a pedal that rests at a hard stop (0) and saturates at full (255, 4096, 1.0...).</summary>
    private static bool CleanSwing(List<double> labels, List<double> values)
    {
        double lo = labels.Min(), hi = labels.Max();
        var atLo = values.Where((_, i) => labels[i] == lo).ToList();
        var atHi = values.Where((_, i) => labels[i] == hi).ToList();
        return atLo.Count > 1 && atHi.Count > 1 && atLo.Distinct().Count() == 1 && atHi.Distinct().Count() == 1
            && Math.Abs(atHi[0] - atLo[0]) >= 0.2 * Math.Max(Math.Abs(atHi[0]), Math.Abs(atLo[0]));
    }

    private static double Pearson(List<double> x, List<double> y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Count; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxx == 0 || syy == 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    private static void WriteResults(SessionResult r, string dir, uint start, int length)
    {
        var json = new
        {
            session = r.Script.Id,
            name = r.Script.Name,
            when = DateTime.Now,
            durationSeconds = Math.Round(r.Duration.TotalSeconds, 1),
            region = new { start = ValueKinds.FormatAddress(start), length },
            steps = r.Script.Steps.Select((s, i) => new { s.Say, s.Label, expect = s.Expect.ToString(), samples = r.SamplesPerStep[i] }),
            survivors = r.Survivors.ToDictionary(p => p.Key.Label(), p => p.Value),
            candidates = r.Candidates.Select(c => new { address = c.AddressText, type = c.Kind.Label(), values = c.StepValues, score = Math.Round(c.Score, 3), c.Notes }),
        };
        File.WriteAllText(Path.Combine(dir, "results.json"), JsonSerializer.Serialize(json, SessionScript.Json));

        var csv = new StringBuilder();
        csv.AppendLine("address,type,score,notes," + string.Join(",", r.Script.Steps.Select((s, i) => $"step{i + 1}" + (s.Label is double l ? $"[{l}]" : ""))));
        foreach (var c in r.Candidates)
            csv.AppendLine($"{c.AddressText},{c.Kind.Label()},{c.Score:0.###},\"{c.Notes}\"," + string.Join(",", c.StepValues.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        File.WriteAllText(Path.Combine(dir, "results.csv"), csv.ToString());
    }
}
