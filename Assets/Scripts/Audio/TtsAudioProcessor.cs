using System;
using UnityEngine;

namespace ByYourSight.Audio
{
    /// <summary>
    /// Offline DSP chain applied once to every TTS clip before it is cached and played:
    ///   1. DC removal + high-pass (~140 Hz): the HoloLens visor speakers cannot reproduce low
    ///      frequencies, so that energy only wastes headroom. Removing it lets speech get louder.
    ///   2. Presence boost (~2.5-4 kHz shelf): consonant clarity in noisy rooms.
    ///   3. Feed-forward RMS compressor (soft knee): evens out quiet/loud syllables so the
    ///      whole sentence stays intelligible at the same perceived level.
    ///   4. Automatic make-up gain to a target peak, followed by a soft-clip safety stage.
    /// Pure C#, no Unity audio mixer assets required (works in Editor, Remoting and UWP builds).
    /// </summary>
    public static class TtsAudioProcessor
    {
        [Serializable]
        public class Settings
        {
            [Tooltip("High-pass cutoff in Hz. Removes low-frequency energy small speakers cannot play.")]
            public float highPassHz = 140f;

            [Tooltip("High-shelf boost (dB) above ~2.5 kHz for consonant clarity.")]
            public float presenceBoostDb = 3f;

            [Tooltip("Compressor threshold in dBFS.")]
            public float thresholdDb = -32f;

            [Tooltip("Compression ratio (e.g. 4 = 4:1).")]
            public float ratio = 4f;

            [Tooltip("Soft-knee width in dB.")]
            public float kneeDb = 8f;

            [Tooltip("Attack time in ms.")]
            public float attackMs = 4f;

            [Tooltip("Release time in ms.")]
            public float releaseMs = 120f;

            [Tooltip("Peak level (dBFS) the processed clip is normalized to.")]
            public float targetPeakDb = -0.8f;

            [Tooltip("Maximum make-up gain (dB) so near-silent clips are not blown up into noise.")]
            public float maxMakeupGainDb = 18f;
        }

        public static readonly Settings Default = new Settings();

        /// <summary>
        /// Returns a processed copy of <paramref name="source"/>, or the original clip if its sample
        /// data cannot be read (e.g. streamed/compressed clips).
        /// </summary>
        public static AudioClip ProcessClip(AudioClip source, Settings s = null)
        {
            if (source == null)
                return null;

            s = s ?? Default;
            try
            {
                int channels = Mathf.Max(1, source.channels);
                float[] data = new float[source.samples * channels];
                if (data.Length == 0 || !source.GetData(data, 0))
                    return source;

                ProcessInPlace(data, channels, source.frequency, s);

                AudioClip clip = AudioClip.Create(source.name + "_proc", source.samples, channels, source.frequency, false);
                clip.SetData(data, 0);
                return clip;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TtsAudioProcessor] Processing skipped: " + e.Message);
                return source;
            }
        }

        public static void ProcessInPlace(float[] data, int channels, int sampleRate, Settings s = null)
        {
            if (data == null || data.Length == 0)
                return;

            s = s ?? Default;
            channels = Mathf.Max(1, channels);
            sampleRate = Mathf.Max(8000, sampleRate);

            for (int ch = 0; ch < channels; ch++)
            {
                HighPass(data, ch, channels, sampleRate, s.highPassHz);
                if (s.presenceBoostDb > 0.05f)
                    HighShelf(data, ch, channels, sampleRate, 2500f, s.presenceBoostDb);
            }

            Compress(data, channels, sampleRate, s);
            NormalizeAndSoftClip(data, s);
        }

        // ---- Biquad filters (RBJ cookbook) --------------------------------------------------

        private static void HighPass(float[] d, int ch, int stride, int fs, float fc)
        {
            if (fc <= 0f) return;
            float w0 = 2f * Mathf.PI * Mathf.Min(fc, fs * 0.45f) / fs;
            float cos = Mathf.Cos(w0);
            float alpha = Mathf.Sin(w0) / (2f * 0.7071f);
            float a0 = 1f + alpha;
            Biquad(d, ch, stride,
                (1f + cos) / 2f / a0, -(1f + cos) / a0, (1f + cos) / 2f / a0,
                -2f * cos / a0, (1f - alpha) / a0);
        }

        private static void HighShelf(float[] d, int ch, int stride, int fs, float fc, float gainDb)
        {
            float A = Mathf.Pow(10f, gainDb / 40f);
            float w0 = 2f * Mathf.PI * Mathf.Min(fc, fs * 0.45f) / fs;
            float cos = Mathf.Cos(w0);
            float sin = Mathf.Sin(w0);
            float alpha = sin / 2f * Mathf.Sqrt(2f); // shelf slope S = 1
            float sqA = 2f * Mathf.Sqrt(A) * alpha;

            float b0 = A * ((A + 1f) + (A - 1f) * cos + sqA);
            float b1 = -2f * A * ((A - 1f) + (A + 1f) * cos);
            float b2 = A * ((A + 1f) + (A - 1f) * cos - sqA);
            float a0 = (A + 1f) - (A - 1f) * cos + sqA;
            float a1 = 2f * ((A - 1f) - (A + 1f) * cos);
            float a2 = (A + 1f) - (A - 1f) * cos - sqA;

            Biquad(d, ch, stride, b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        private static void Biquad(float[] d, int ch, int stride, float b0, float b1, float b2, float a1, float a2)
        {
            float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f;
            for (int i = ch; i < d.Length; i += stride)
            {
                float x = d[i];
                float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                d[i] = y;
            }
        }

        // ---- Dynamics -----------------------------------------------------------------------

        private static void Compress(float[] d, int channels, int fs, Settings s)
        {
            float attackCoef = Mathf.Exp(-1f / (Mathf.Max(0.1f, s.attackMs) * 0.001f * fs));
            float releaseCoef = Mathf.Exp(-1f / (Mathf.Max(1f, s.releaseMs) * 0.001f * fs));
            float rmsCoef = Mathf.Exp(-1f / (0.010f * fs)); // 10 ms RMS window
            float ratio = Mathf.Max(1f, s.ratio);
            float knee = Mathf.Max(0f, s.kneeDb);

            float meanSq = 0f;
            float gainReductionDb = 0f;
            int frames = d.Length / channels;

            for (int f = 0; f < frames; f++)
            {
                int idx = f * channels;
                float peakSq = 0f;
                for (int c = 0; c < channels; c++)
                {
                    float v = d[idx + c];
                    peakSq = Mathf.Max(peakSq, v * v);
                }

                meanSq = rmsCoef * meanSq + (1f - rmsCoef) * peakSq;
                float levelDb = 10f * Mathf.Log10(meanSq + 1e-12f);

                float over = levelDb - s.thresholdDb;
                float targetGrDb;
                if (knee > 0f && 2f * Mathf.Abs(over) <= knee)
                {
                    float k = over + knee / 2f;
                    targetGrDb = (1f / ratio - 1f) * k * k / (2f * knee);
                }
                else if (over > 0f)
                {
                    targetGrDb = (1f / ratio - 1f) * over;
                }
                else
                {
                    targetGrDb = 0f;
                }

                // targetGrDb <= 0. More negative = more reduction -> use attack.
                float coef = targetGrDb < gainReductionDb ? attackCoef : releaseCoef;
                gainReductionDb = coef * gainReductionDb + (1f - coef) * targetGrDb;

                float g = Mathf.Pow(10f, gainReductionDb / 20f);
                for (int c = 0; c < channels; c++)
                    d[idx + c] *= g;
            }
        }

        private static void NormalizeAndSoftClip(float[] d, Settings s)
        {
            float peak = 0f;
            for (int i = 0; i < d.Length; i++)
                peak = Mathf.Max(peak, Mathf.Abs(d[i]));
            if (peak < 1e-5f)
                return;

            float target = Mathf.Pow(10f, s.targetPeakDb / 20f);
            float maxGain = Mathf.Pow(10f, s.maxMakeupGainDb / 20f);
            float gain = Mathf.Min(target / peak, maxGain);

            for (int i = 0; i < d.Length; i++)
                d[i] = SoftClip(d[i] * gain);
        }

        /// <summary>Transparent below 0.85, smoothly saturates towards +/-1 above it.</summary>
        public static float SoftClip(float x)
        {
            const float t = 0.85f;
            float a = Mathf.Abs(x);
            if (a <= t) return x;
            float over = (a - t) / (1f - t);
            float y = t + (1f - t) * (float)Math.Tanh(over);
            return x < 0f ? -y : y;
        }
    }
}
