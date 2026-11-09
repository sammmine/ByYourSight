using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System.Diagnostics;
#endif
#if WINDOWS_UWP
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using System.Threading.Tasks;
#endif

namespace ByYourSight.Audio
{
    /// <summary>
    /// Audio Priority Hierarchy (T3 "By Your Sight"):
    /// P0: Danger Alarm (KF07) -> Interrupts all speech, cannot be muted.
    /// P1: Navigation Instructions (KF05) -> Delays P2-P3.
    /// P2: Social Info (KF08) -> Queued.
    /// P3: Object Identification & Confirmation (KF01, KF02) -> Queued, can be muted.
    /// </summary>
    public enum AudioPriority
    {
        P0_DangerAlarm = 0,
        P1_Navigation = 1,
        P2_SocialInfo = 2,
        P3_ObjectIdentity = 3
    }

    public enum EarconType
    {
        ListeningStart,
        ListeningEnd,
        Success,
        Failure,
        DangerAlarm,
        SpatialBeacon
    }

    [Serializable]
    public class SpeechRequest
    {
        public string Text;
        public string DetailedText;
        public AudioPriority Priority;
        public Vector3? WorldPosition;
        public float Timestamp;
    }

    public class PriorityAudioManager : MonoBehaviour
    {
        public static PriorityAudioManager Instance { get; private set; }

        [Header("Audio & Mute Status (KF06)")]
        [Tooltip("If true, P1-P3 speech is paused. P0 (Danger Alarm) CANNOT be muted.")]
        public bool isMuted = false;

        [Range(0.2f, 1.5f)]
        [Tooltip("Adaptive volume multiplier based on ambient noise (KF06).")]
        public float adaptiveVolumeMultiplier = 1.0f;

        [Header("Last Spoken Message Log (For 'Repeat' & 'Details')")]
        public string currentSpokenText = "";
        public string lastSpokenText = "";
        public string lastDetailedText = "";
        public AudioPriority currentPriority = AudioPriority.P3_ObjectIdentity;

        private readonly List<SpeechRequest> _queue = new List<SpeechRequest>();
        private AudioSource _earconSource;
        private AudioSource _spatialVoiceSource;
        private float _speechBusyUntil = 0f;
        private float _lastDangerAlertTime = -10f;
        private bool _lastDangerHadEarcon = false;

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        private Process _macSayProcess;
#endif
#if WINDOWS_UWP
        private SpeechSynthesizer _uwpSynthesizer;
#endif

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            _earconSource = gameObject.AddComponent<AudioSource>();
            _earconSource.spatialBlend = 0f; // 2D system earcon

            GameObject spatialObj = new GameObject("SpatialVoiceEmitter");
            spatialObj.transform.SetParent(transform);
            _spatialVoiceSource = spatialObj.AddComponent<AudioSource>();
            _spatialVoiceSource.spatialBlend = 1.0f; // 3D Spatial Audio
            _spatialVoiceSource.minDistance = 0.5f;
            _spatialVoiceSource.maxDistance = 12f;

#if WINDOWS_UWP
            _uwpSynthesizer = new SpeechSynthesizer();
#endif
        }

        private void Update()
        {
            if (Time.time < _speechBusyUntil)
                return;

            currentSpokenText = "";

            if (_queue.Count == 0)
                return;

            // Sort queue by highest priority (P0 -> P1 -> P2 -> P3), then arrival timestamp
            _queue.Sort((a, b) =>
            {
                int cmp = a.Priority.CompareTo(b.Priority);
                return cmp != 0 ? cmp : a.Timestamp.CompareTo(b.Timestamp);
            });

            SpeechRequest next = _queue[0];
            _queue.RemoveAt(0);

            // When muted, skip everything except P0_DangerAlarm
            if (isMuted && next.Priority != AudioPriority.P0_DangerAlarm)
                return;

            ExecuteSpeech(next);
        }

        /// <summary>
        /// Queue or immediately speak text according to P0-P3 priority.
        /// T3 sentence pattern: [What] + [Distance] + [Direction], max 6-8 words.
        /// </summary>
        public bool EnqueueSpeech(string shortText, AudioPriority priority, Vector3? worldPos = null, string detailedText = "", bool playAlarmEarcon = true)
        {
            if (string.IsNullOrWhiteSpace(shortText))
                return false;

            // P0 (Danger Alarm) interrupts all other audio and plays immediately
            if (priority == AudioPriority.P0_DangerAlarm)
            {
                // Cooldown so P0 alarms don't stack every frame and speech can finish.
                // A critical alarm (with earcon) may still escalate right after a calm hazard notice.
                bool escalation = playAlarmEarcon && !_lastDangerHadEarcon;
                if (Time.time - _lastDangerAlertTime < 3.5f && !escalation)
                    return false;

                _lastDangerAlertTime = Time.time;
                _lastDangerHadEarcon = playAlarmEarcon;
                StopCurrentSpeech();
                if (playAlarmEarcon)
                {
                    PlayEarcon(EarconType.DangerAlarm);
                }

                // Clear stale P2/P3 messages when a hazard occurs
                _queue.RemoveAll(q => q.Priority >= AudioPriority.P2_SocialInfo);

                ExecuteSpeech(new SpeechRequest
                {
                    Text = shortText,
                    DetailedText = string.IsNullOrEmpty(detailedText) ? shortText : detailedText,
                    Priority = priority,
                    WorldPosition = worldPos,
                    Timestamp = Time.time
                });
                return true;
            }

            if (isMuted)
                return false;

            // P1 (Navigation) replaces any older P1 instruction waiting in the queue
            if (priority == AudioPriority.P1_Navigation)
            {
                _queue.RemoveAll(q => q.Priority == AudioPriority.P1_Navigation);
            }

            // Avoid duplicate identical messages in the queue
            if (_queue.Exists(q => q.Text == shortText))
                return false;

            _queue.Add(new SpeechRequest
            {
                Text = shortText,
                DetailedText = string.IsNullOrEmpty(detailedText) ? shortText : detailedText,
                Priority = priority,
                WorldPosition = worldPos,
                Timestamp = Time.time
            });
            return true;
        }

        private void ExecuteSpeech(SpeechRequest req)
        {
            currentSpokenText = req.Text;
            currentPriority = req.Priority;
            lastSpokenText = req.Text;
            lastDetailedText = string.IsNullOrEmpty(req.DetailedText) ? req.Text : req.DetailedText;

            if (req.WorldPosition.HasValue)
            {
                _spatialVoiceSource.transform.position = req.WorldPosition.Value;
                // Play a soft spatial ping from the object's 3D position
                AudioClip ping = GenerateToneClip(660f, 660f, 0.08f, 0.25f * adaptiveVolumeMultiplier);
                _spatialVoiceSource.PlayOneShot(ping);
            }

            // Estimate speech duration (~0.38s per word, minimum 1.4s)
            int wordCount = req.Text.Split(' ').Length;
            float duration = Mathf.Max(1.4f, wordCount * 0.38f);
            _speechBusyUntil = Time.time + duration;

            UnityEngine.Debug.Log($"[Audio {req.Priority}] \"{req.Text}\"");

#if WINDOWS_UWP
            SpeakWithHoloLensNativeUWP(req.Text);
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            SpeakWithWindowsHolographicRemoting(req.Text);
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            SpeakWithMacOSEnglish(req.Text);
#endif
        }

        /// <summary>
        /// Voice command "Mute" / "Resume" (KF06)
        /// </summary>
        public void SetMute(bool mute)
        {
            isMuted = mute;
            if (mute)
            {
                _queue.RemoveAll(q => q.Priority != AudioPriority.P0_DangerAlarm);
                StopCurrentSpeech();
                PlayEarcon(EarconType.ListeningEnd);
                UnityEngine.Debug.Log("[Audio] Muted. P0 Danger Alarms remain active.");
            }
            else
            {
                PlayEarcon(EarconType.Success);
                EnqueueSpeech("Audio resumed", AudioPriority.P3_ObjectIdentity);
            }
        }

        /// <summary>
        /// Voice command "Repeat" / "Details" (T3 Design Principles)
        /// </summary>
        public void RepeatLastSpeech(bool detailed = false)
        {
            string textToRepeat = detailed && !string.IsNullOrEmpty(lastDetailedText)
                ? lastDetailedText
                : lastSpokenText;

            if (string.IsNullOrEmpty(textToRepeat))
            {
                EnqueueSpeech("No previous message", AudioPriority.P3_ObjectIdentity);
                return;
            }

            EnqueueSpeech(textToRepeat, AudioPriority.P3_ObjectIdentity);
        }

        public void StopCurrentSpeech()
        {
            _speechBusyUntil = 0f;
            currentSpokenText = "";
            if (_spatialVoiceSource != null && _spatialVoiceSource.isPlaying)
            {
                _spatialVoiceSource.Stop();
            }
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            try
            {
                if (_macSayProcess != null && !_macSayProcess.HasExited)
                {
                    _macSayProcess.Kill();
                }
            }
            catch { }
#endif
        }

        /// <summary>
        /// Procedurally generates Earcons without requiring external WAV assets.
        /// </summary>
        public void PlayEarcon(EarconType type, Vector3? spatialPos = null, float pitchMultiplier = 1f)
        {
            AudioClip clip = null;
            float vol = Mathf.Clamp01(0.5f * adaptiveVolumeMultiplier);

            switch (type)
            {
                case EarconType.ListeningStart:
                    clip = GenerateToneClip(440f, 660f, 0.14f, vol);
                    break;
                case EarconType.ListeningEnd:
                    clip = GenerateToneClip(660f, 440f, 0.14f, vol);
                    break;
                case EarconType.Success:
                    clip = GenerateToneClip(523.25f, 783.99f, 0.18f, vol); // Ascending tone (C5 -> G5)
                    break;
                case EarconType.Failure:
                    clip = GenerateToneClip(400f, 250f, 0.22f, vol); // Descending tone
                    break;
                case EarconType.DangerAlarm:
                    // Danger alarm: sharp double beep (only aggressive earcon per T3)
                    clip = GenerateDoubleBeepClip(980f, 0.24f, Mathf.Clamp01(0.85f * adaptiveVolumeMultiplier));
                    break;
                case EarconType.SpatialBeacon:
                    clip = GenerateToneClip(580f * pitchMultiplier, 620f * pitchMultiplier, 0.09f, vol);
                    break;
            }

            if (clip == null) return;

            if (spatialPos.HasValue)
            {
                _spatialVoiceSource.transform.position = spatialPos.Value;
                _spatialVoiceSource.PlayOneShot(clip);
            }
            else
            {
                _earconSource.PlayOneShot(clip);
            }
        }

        private AudioClip GenerateToneClip(float startFreq, float endFreq, float duration, float volume)
        {
            int sampleRate = 44100;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(sampleRate * duration));
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                float freq = Mathf.Lerp(startFreq, endFreq, t);
                float env = Mathf.Sin(t * Mathf.PI);
                samples[i] = Mathf.Sin(2f * Mathf.PI * freq * i / sampleRate) * volume * env;
            }

            AudioClip clip = AudioClip.Create("ProceduralEarcon", sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private AudioClip GenerateDoubleBeepClip(float freq, float duration, float volume)
        {
            int sampleRate = 44100;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(sampleRate * duration));
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                bool active = (t < 0.4f) || (t > 0.55f && t < 0.95f);
                samples[i] = active ? Mathf.Sin(2f * Mathf.PI * freq * i / sampleRate) * volume : 0f;
            }

            AudioClip clip = AudioClip.Create("DangerAlarmBeep", sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

#if WINDOWS_UWP
        /// <summary>
        /// Native offline Windows Holographic OS (HoloLens 2) TTS using SpeechSynthesizer.
        /// Zero cloud dependency, runs natively on device.
        /// </summary>
        private async void SpeakWithHoloLensNativeUWP(string text)
        {
            try
            {
                if (_uwpSynthesizer == null)
                    _uwpSynthesizer = new SpeechSynthesizer();

                SpeechSynthesisStream stream = await _uwpSynthesizer.SynthesizeTextToStreamAsync(text);
                byte[] bytes = new byte[stream.Size];
                using (DataReader reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(bytes);
                }

                // Convert 16-bit PCM WAV stream from Windows SpeechSynthesizer into Unity AudioClip
                AudioClip ttsClip = ConvertWavBytesToAudioClip(bytes);
                if (ttsClip != null && _spatialVoiceSource != null)
                {
                    _spatialVoiceSource.PlayOneShot(ttsClip, adaptiveVolumeMultiplier);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("HoloLens Native UWP TTS error: " + e.Message);
            }
        }
#endif

        private AudioClip ConvertWavBytesToAudioClip(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length <= 44)
                return null;

            int channels = Mathf.Max(1, BitConverter.ToInt16(wavBytes, 22));
            int sampleRate = Mathf.Max(8000, BitConverter.ToInt32(wavBytes, 24));
            int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);

            // Locate the 'data' chunk in the WAV header
            int dataOffset = 12;
            while (dataOffset + 8 < wavBytes.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(wavBytes, dataOffset, 4);
                int chunkSize = BitConverter.ToInt32(wavBytes, dataOffset + 4);
                if (chunkId == "data")
                {
                    dataOffset += 8;
                    break;
                }
                dataOffset += 8 + chunkSize;
            }

            if (dataOffset >= wavBytes.Length)
                dataOffset = 44;

            int bytesPerSample = Mathf.Max(2, bitsPerSample / 8);
            int sampleCount = (wavBytes.Length - dataOffset) / bytesPerSample;
            if (sampleCount <= 0)
                return null;

            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(wavBytes, dataOffset + i * bytesPerSample);
                samples[i] = sample / 32768f;
            }

            AudioClip clip = AudioClip.Create("NativeTTSClip", sampleCount / channels, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        /// <summary>
        /// Synthesizes English speech on Windows using System.Speech into a WAV AudioClip
        /// and plays it directly through Unity's 3D AudioSource so Holographic Remoting streams
        /// the spatial voice directly to the HoloLens 2 speakers!
        /// </summary>
        private void SpeakWithWindowsHolographicRemoting(string text)
        {
            try
            {
                string clean = text.Replace("\"", "").Replace("'", "");
                string tempWav = Path.Combine(Application.temporaryCachePath, "hololens_tts.wav");
                if (File.Exists(tempWav))
                {
                    File.Delete(tempWav);
                }

                string psScript = $"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Rate = 1; $s.SetOutputToWaveFile('{tempWav}'); $s.Speak('{clean}'); $s.Dispose()";
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{psScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(1800);
                }

                if (File.Exists(tempWav))
                {
                    byte[] wavBytes = File.ReadAllBytes(tempWav);
                    AudioClip ttsClip = ConvertWavBytesToAudioClip(wavBytes);
                    if (ttsClip != null && _spatialVoiceSource != null)
                    {
                        _spatialVoiceSource.PlayOneShot(ttsClip, adaptiveVolumeMultiplier);
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("Windows TTS failed: " + e.Message);
            }
        }
#endif

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        private void SpeakWithMacOSEnglish(string text)
        {
            try
            {
                if (_macSayProcess != null && !_macSayProcess.HasExited)
                {
                    _macSayProcess.Kill();
                }

                string clean = text.Replace("\"", "");
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/say",
                    Arguments = $"-v Samantha -r 190 \"{clean}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _macSayProcess = Process.Start(psi);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("macOS TTS fallback failed: " + e.Message);
            }
        }
#endif

        private void OnDestroy()
        {
            StopCurrentSpeech();
#if WINDOWS_UWP
            _uwpSynthesizer?.Dispose();
#endif
        }

        /// <summary>
        /// Converts target position into body-relative direction terms (T3):
        /// "ahead", "slightly right", "right", "slightly left", "left", "behind".
        /// </summary>
        public static string GetRelativeBodyDirection(Transform userHead, Vector3 targetPos)
        {
            Vector3 toTarget = targetPos - userHead.position;
            toTarget.y = 0f;
            Vector3 forward = userHead.forward;
            forward.y = 0f;

            if (toTarget.sqrMagnitude < 0.001f)
                return "ahead";

            float signedAngle = Vector3.SignedAngle(forward.normalized, toTarget.normalized, Vector3.up);

            if (Mathf.Abs(signedAngle) <= 20f)
                return "ahead";
            if (signedAngle > 20f && signedAngle <= 60f)
                return "slightly right";
            if (signedAngle > 60f && signedAngle <= 125f)
                return "right";
            if (signedAngle < -20f && signedAngle >= -60f)
                return "slightly left";
            if (signedAngle < -60f && signedAngle >= -125f)
                return "left";

            return "behind";
        }
    }
}
