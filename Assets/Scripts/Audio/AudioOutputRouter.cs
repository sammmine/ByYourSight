using System;
using UnityEngine;
#if WINDOWS_UWP
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
#endif

namespace ByYourSight.Audio
{
    public enum AudioOutputProfile
    {
        /// <summary>HoloLens 2 visor speakers: max gain, partial spatialization.</summary>
        BuiltInSpeaker = 0,
        /// <summary>Wired/Bluetooth headphones or headset: moderate gain, full 3D spatial audio.</summary>
        ExternalHeadphones = 1,
        /// <summary>External (Bluetooth) speaker not worn on the head: 2D speech, spatial cues are meaningless.</summary>
        ExternalSpeaker = 2
    }

    /// <summary>
    /// Keeps Unity's audio output attached to the current default device (so a Bluetooth
    /// headset/speaker connected mid-session is picked up) and selects an output profile that
    /// PriorityAudioManager uses for gain and spatialization.
    ///
    /// - UWP (HoloLens build): the profile is auto-detected from the endpoint form factor
    ///   when the default render device changes.
    /// - Holographic Remoting / Editor: audio is streamed to the HoloLens, so Unity cannot see
    ///   which peripheral is paired on the headset. Use <see cref="forcedProfile"/>,
    ///   <see cref="SetProfile"/> or the F9 key to choose the profile manually.
    /// </summary>
    public class AudioOutputRouter : MonoBehaviour
    {
        [Serializable]
        public class ProfileSettings
        {
            [Tooltip("Software gain (dB) applied on top of the processed TTS, before the limiter.")]
            [Range(-12f, 18f)] public float voiceGainDb = 6f;
            [Range(-12f, 18f)] public float earconGainDb = 3f;
            [Tooltip("0 = 2D (always centered/loud), 1 = full 3D spatial speech.")]
            [Range(0f, 1f)] public float voiceSpatialBlend = 0.5f;
        }

        public static AudioOutputRouter Instance { get; private set; }

        public event Action<AudioOutputProfile> ProfileChanged;

        [Header("Profile")]
        [Tooltip("If enabled, auto-detection is ignored and this profile is always used.")]
        public bool useForcedProfile = false;
        public AudioOutputProfile forcedProfile = AudioOutputProfile.BuiltInSpeaker;

        [Tooltip("Keyboard shortcut (Editor/Remoting) to cycle through output profiles.")]
        public KeyCode cycleProfileKey = KeyCode.F9;

        [Header("Per-profile tuning")]
        public ProfileSettings builtInSpeaker = new ProfileSettings { voiceGainDb = 7f, earconGainDb = 4f, voiceSpatialBlend = 0.45f };
        public ProfileSettings externalHeadphones = new ProfileSettings { voiceGainDb = 2f, earconGainDb = 0f, voiceSpatialBlend = 0.85f };
        public ProfileSettings externalSpeaker = new ProfileSettings { voiceGainDb = 4f, earconGainDb = 2f, voiceSpatialBlend = 0f };

        [Header("Status (read-only)")]
        [SerializeField] private AudioOutputProfile _activeProfile = AudioOutputProfile.BuiltInSpeaker;
        [SerializeField] private string _currentDeviceName = "(default)";

        public AudioOutputProfile ActiveProfile => useForcedProfile ? forcedProfile : _activeProfile;
        public string CurrentDeviceName => _currentDeviceName;

        public ProfileSettings ActiveSettings
        {
            get
            {
                switch (ActiveProfile)
                {
                    case AudioOutputProfile.ExternalHeadphones: return externalHeadphones;
                    case AudioOutputProfile.ExternalSpeaker: return externalSpeaker;
                    default: return builtInSpeaker;
                }
            }
        }

        /// <summary>Raised after Unity re-opened the audio device (all playing sounds were stopped).</summary>
        public event Action DeviceReset;

        private AudioOutputProfile _lastAnnouncedProfile;
        private volatile bool _pendingReset;
#if WINDOWS_UWP
        private string _startupDeviceId;
        private volatile bool _pendingUwpRedetect;
        private const string FormFactorKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e} 0";
#endif

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            _lastAnnouncedProfile = ActiveProfile;
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;

#if WINDOWS_UWP
            try
            {
                _startupDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                MediaDevice.DefaultAudioRenderDeviceChanged += OnUwpDefaultRenderDeviceChanged;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AudioOutputRouter] UWP device watch unavailable: " + e.Message);
            }
            DetectUwpProfile(_startupDeviceId);
#endif
        }

        private void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
#if WINDOWS_UWP
            try { MediaDevice.DefaultAudioRenderDeviceChanged -= OnUwpDefaultRenderDeviceChanged; } catch { }
#endif
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_pendingReset)
            {
                _pendingReset = false;
                ResetAudioDevice();
            }

#if WINDOWS_UWP
            if (_pendingUwpRedetect)
            {
                _pendingUwpRedetect = false;
                string id = null;
                try { id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default); } catch { }
                DetectUwpProfile(id);
                _pendingReset = true;
            }
#endif

            if (cycleProfileKey != KeyCode.None && Input.GetKeyDown(cycleProfileKey))
            {
                AudioOutputProfile next = (AudioOutputProfile)(((int)ActiveProfile + 1) % 3);
                SetProfile(next);
            }

            if (ActiveProfile != _lastAnnouncedProfile)
            {
                _lastAnnouncedProfile = ActiveProfile;
                Debug.Log($"[AudioOutputRouter] Output profile -> {ActiveProfile} (device: {_currentDeviceName})");
                ProfileChanged?.Invoke(ActiveProfile);
            }
        }

        /// <summary>Manually select the output profile (e.g. from a voice command or menu).</summary>
        public void SetProfile(AudioOutputProfile profile)
        {
            useForcedProfile = true;
            forcedProfile = profile;
        }

        /// <summary>Return to automatic detection (UWP only; elsewhere falls back to BuiltInSpeaker).</summary>
        public void UseAutoProfile()
        {
            useForcedProfile = false;
        }

        /// <summary>Spoken Indonesian label for the given profile.</summary>
        public static string GetSpokenLabel(AudioOutputProfile profile)
        {
            switch (profile)
            {
                case AudioOutputProfile.ExternalHeadphones: return "Mode headset";
                case AudioOutputProfile.ExternalSpeaker: return "Mode speaker eksternal";
                default: return "Mode speaker HoloLens";
            }
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            if (!deviceWasChanged) return;
            // Re-open the output on the *new* default device; defer to Update to avoid re-entrancy.
            _pendingReset = true;
        }

        private void ResetAudioDevice()
        {
            try
            {
                AudioConfiguration config = AudioSettings.GetConfiguration();
                AudioSettings.Reset(config);
                Debug.Log("[AudioOutputRouter] Audio output re-attached to the current default device.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AudioOutputRouter] Audio reset failed: " + e.Message);
            }
            DeviceReset?.Invoke();
        }

#if WINDOWS_UWP
        private void OnUwpDefaultRenderDeviceChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
        {
            // Raised on a background thread; handle on the main thread.
            if (args.Role == AudioDeviceRole.Default)
                _pendingUwpRedetect = true;
        }

        private async void DetectUwpProfile(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return;
            try
            {
                DeviceInformation info = await DeviceInformation.CreateFromIdAsync(deviceId, new[] { FormFactorKey });
                _currentDeviceName = info != null ? info.Name : deviceId;

                int formFactor = -1;
                if (info != null && info.Properties.TryGetValue(FormFactorKey, out object ff) && ff != null)
                    formFactor = Convert.ToInt32(ff);

                // EndpointFormFactor: 3 = Headphones, 5 = Headset, 6 = Handset
                if (formFactor == 3 || formFactor == 5 || formFactor == 6)
                    _activeProfile = AudioOutputProfile.ExternalHeadphones;
                else if (!string.Equals(deviceId, _startupDeviceId, StringComparison.OrdinalIgnoreCase))
                    _activeProfile = AudioOutputProfile.ExternalSpeaker;
                else
                    _activeProfile = AudioOutputProfile.BuiltInSpeaker;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AudioOutputRouter] Device detection failed: " + e.Message);
            }
        }
#endif
    }
}
