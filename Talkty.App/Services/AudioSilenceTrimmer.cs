namespace Talkty.App.Services;

/// <summary>One sample-aligned trim for both early and final full-context recognition.</summary>
public static class AudioSilenceTrimmer
{
    public static float[] Trim(float[] samples)
    {
        var window = Constants.SilenceWindowSamples;
        if (samples.Length < window * 3) return samples;
        var first = -1;
        var last = 0;
        // Anchor both ends to the recording start. Appending silence must not shift
        // the analysis windows (the old backwards scan shifted by every new sample).
        for (var start = 0; start < samples.Length; start += window)
        {
            var count = Math.Min(window, samples.Length - start);
            if (IsQuiet(samples.AsSpan(start, count))) continue;
            if (first < 0) first = start;
            last = start + count;
        }
        // Keep the established no-speech behavior; do not manufacture an empty take.
        if (first < 0) return samples;
        first = Math.Max(0, first - Constants.SilenceMarginSamples);
        last = Math.Min(samples.Length, last + Constants.SilenceMarginSamples);
        return first == 0 && last == samples.Length ? samples : samples[first..last];
    }

    internal static bool IsQuiet(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return false;
        double energy = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) return false;
            energy += sample * sample;
        }
        return Math.Sqrt(energy / samples.Length) <= Constants.SilenceThreshold;
    }

    internal static bool IsPause(float[] tail, int requiredSamples)
    {
        if (tail.Length < requiredSamples) return false;
        // A quiet average alone could hide a short final word inside a long pause.
        for (var i = 0; i < tail.Length; i += Constants.SilenceWindowSamples)
            if (!IsQuiet(tail.AsSpan(i, Math.Min(Constants.SilenceWindowSamples, tail.Length - i)))) return false;
        return true;
    }
}
