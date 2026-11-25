using UnityEngine;

namespace ByYourSight.Audio
{
    /// <summary>
    /// Runtime software gain stage + peak limiter for one AudioSource (via OnAudioFilterRead).
    /// AudioSource.volume is clamped to 1.0, so it cannot make audio louder than the source
    /// material. This filter applies gain above unity (e.g. +6 dB for the HoloLens built-in
    /// speaker or a noisy room) and then uses a fast-attack peak limiter and a soft clip so the
    /// boosted signal never hard-clips or crackles.
    /// Must live on the same GameObject as the AudioSource it processes.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class VoiceOutputLimiter : MonoBehaviour
    {
        [Tooltip("Gain applied before the limiter, in dB (set by PriorityAudioManager).")]
        [Range(-12f, 18f)]
        public float gainDb = 0f;

        [Tooltip("Limiter ceiling (linear, 0-1).")]
        [Range(0.5f, 1f)]
        public float ceiling = 0.97f;

        [Tooltip("Limiter release time in ms.")]
        [Range(10f, 500f)]
        public float releaseMs = 90f;

        // Written on the main thread, read on the audio thread (float writes are atomic).
        private volatile float _targetGain = 1f;
        private float _smoothedGain = 1f;
        private float _limiterGain = 1f;
        private float _releaseCoef = 0.999f;
        private float _gainSmoothCoef = 0.999f;
        private int _sampleRate = 48000;

        private void Awake()
        {
            _sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            UpdateCoefficients();
        }

        private void Update()
        {
            _targetGain = Mathf.Pow(10f, gainDb / 20f);
        }

        private void OnValidate()
        {
            UpdateCoefficients();
        }

        public void ResetAfterDeviceChange()
        {
            _sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            _limiterGain = 1f;
            UpdateCoefficients();
        }

        private void UpdateCoefficients()
        {
            int fs = Mathf.Max(8000, _sampleRate);
            _releaseCoef = Mathf.Exp(-1f / (releaseMs * 0.001f * fs));
            _gainSmoothCoef = Mathf.Exp(-1f / (0.030f * fs)); // 30 ms ramp, no zipper noise
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (channels <= 0) return;

            float target = _targetGain;
            float ceil = ceiling;
            float rel = _releaseCoef;
            float smooth = _gainSmoothCoef;
            float g = _smoothedGain;
            float lim = _limiterGain;

            for (int i = 0; i < data.Length; i += channels)
            {
                g = smooth * g + (1f - smooth) * target;

                float framePeak = 0f;
                for (int c = 0; c < channels && i + c < data.Length; c++)
                {
                    float a = Mathf.Abs(data[i + c] * g);
                    if (a > framePeak) framePeak = a;
                }

                // Instant attack, exponential release.
                float needed = framePeak > ceil ? ceil / framePeak : 1f;
                lim = needed < lim ? needed : rel * lim + (1f - rel) * needed;

                float total = g * lim;
                for (int c = 0; c < channels && i + c < data.Length; c++)
                    data[i + c] = TtsAudioProcessor.SoftClip(data[i + c] * total);
            }

            _smoothedGain = g;
            _limiterGain = lim;
        }
    }
}
