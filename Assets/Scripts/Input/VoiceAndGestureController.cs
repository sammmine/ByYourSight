using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
#if UNITY_STANDALONE_WIN || UNITY_WSA || UNITY_EDITOR_WIN
using UnityEngine.Windows.Speech;
#endif
using ByYourSight.Audio;
using ByYourSight.Perception;
using ByYourSight.Navigation;
using ByYourSight.Safety;

namespace ByYourSight.InputSystem
{
    /// <summary>
    /// Manages:
    /// - KF04: Native HoloLens 2 Offline English Voice Command Vocabulary via Windows KeywordRecognizer
    ///         ("What is this?", "Save object", "Find [item]", "Navigate to [destination]",
    ///          "Mute"/"Resume", "Repeat", "Details", "Help", "Cancel").
    /// - KF06: Adaptive volume based on ambient noise + Quick one-handed Mute gesture.
    /// - Editor Simulation (WASD + Mouse Look + Voice Command HUD) for testing on macOS/PC.
    /// </summary>
    public class VoiceAndGestureController : MonoBehaviour
    {
        [Header("By Your Sight Module References")]
        public ObjectRecognizer objectRecognizer;
        public SpatialBeaconAndNavigator navigator;
        public ObstacleDetector obstacleDetector;

        [Header("KF06: Ambient Noise Monitoring (RMS)")]
        public bool enableMicrophoneAmbientMonitor = false;
        [Range(0f, 1f)]
        public float simulatedAmbientNoiseLevel = 0.2f;

        [Header("HoloLens 2 Real-World Mode vs Editor Simulation")]
        [Tooltip("If true, hides the dummy floor & simulated obstacles so HoloLens scans the real physical room via Spatial Mesh.")]
        public bool isRealHoloLensRoomMode = false;
        public bool enableEditorWalkSimulation = true;
        public float walkSpeed = 2.2f;
        public float mouseLookSensitivity = 2.2f;

        private float _yaw = 0f;
        private float _pitch = 0f;
        private string _customCommandInput = "Cari Botol minum";
        private string _customSaveLabel = "Kunci";
        private RealWorldObjectDetector _objectDetector;

        // Maps phonetic/variant spoken phrases -> canonical Indonesian command
        private readonly Dictionary<string, string> _phoneticCommandAliases = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // KF01: "Ini apa?" (including real English dictionary words that match /ɪni ɑpɑ/ on Windows en-US recognizer)
            { "ini apa", "ini apa" },
            { "inny apa", "ini apa" },
            { "innie apa", "ini apa" },
            { "any apa", "ini apa" },
            { "in knee apa", "ini apa" },
            { "in knee upper", "ini apa" },
            { "in knee opera", "ini apa" },
            { "in knee papa", "ini apa" },
            { "in knee up", "ini apa" },
            { "in knee up a", "ini apa" },
            { "any upper", "ini apa" },
            { "any opera", "ini apa" },
            { "any papa", "ini apa" },
            { "any up", "ini apa" },
            { "any up a", "ini apa" },
            { "mini upper", "ini apa" },
            { "mini opera", "ini apa" },
            { "mini papa", "ini apa" },
            { "teeny upper", "ini apa" },
            { "penny upper", "ini apa" },
            { "knee upper", "ini apa" },
            { "knee up", "ini apa" },
            { "in the upper", "ini apa" },
            { "in upper", "ini apa" },
            { "ee knee ah pah", "ini apa" },
            { "ini a pa", "ini apa" },
            { "ini up a", "ini apa" },
            { "apa ini", "ini apa" },
            { "upper any", "ini apa" },
            { "upper in knee", "ini apa" },
            { "upper mini", "ini apa" },
            { "opera any", "ini apa" },
            { "papa any", "ini apa" },
            { "up a any", "ini apa" },
            { "up in knee", "ini apa" },
            { "ah pah ee knee", "ini apa" },
            { "ah pah ini", "ini apa" },
            { "up a ini", "ini apa" },
            { "ada apa", "ini apa" },
            { "benda apa", "ini apa" },
            { "objek apa", "ini apa" },
            { "lihat ini", "ini apa" },
            { "lihat depan", "ini apa" },
            { "what is this", "ini apa" },
            { "identify", "ini apa" },

            // KF02: "Simpan objek" (uses HUD label if no object specified)
            { "simpan objek", "simpan objek" },
            { "simpan benda", "simpan objek" },
            { "save object", "simpan objek" },
            { "save item", "simpan objek" },

            // Anchor management: "List saved" & "Clear saved"
            { "list saved", "daftar simpanan" },
            { "clear saved", "hapus simpanan" },
            { "delete saved", "hapus simpanan" },

            // Audio control: "Ulangi" & "Detail"
            { "ulangi", "ulangi" },
            { "ulang", "ulangi" },
            { "repeat", "ulangi" },
            { "detail", "detail" },
            { "dee tail", "detail" },
            { "details", "detail" },

            // KF06: "Diam" & "Lanjut"
            { "diam", "diam" },
            { "dee um", "diam" },
            { "dee am", "diam" },
            { "hening", "diam" },
            { "bisukan", "diam" },
            { "mute", "diam" },
            { "quiet", "diam" },
            { "lanjut", "lanjut" },
            { "lun jut", "lanjut" },
            { "lan joot", "lanjut" },
            { "suara", "lanjut" },
            { "resume", "lanjut" },
            { "unmute", "lanjut" },

            // Navigation control: "Batal" / "Berhenti" & "Bantuan"
            { "batal", "batal" },
            { "bah tal", "batal" },
            { "berhenti", "batal" },
            { "stop", "batal" },
            { "cancel", "batal" },
            { "bantuan", "bantuan" },
            { "bun too un", "bantuan" },
            { "ban too an", "bantuan" },
            { "tolong", "bantuan" },
            { "help", "bantuan" }
        };

#if UNITY_STANDALONE_WIN || UNITY_WSA || UNITY_EDITOR_WIN
        private KeywordRecognizer _keywordRecognizer;
#endif

        [Header("Real-Room Tagging (Simpan -> Cari / Antar ke)")]
        [Tooltip("Labels the user can tag in the real room (supports both Indonesian and English).")]
        public List<string> realWorldTagLabels = new List<string>
        {
            "pintu", "rak", "tempat sampah", "meja", "kursi", "tas", "handphone", "botol", "cangkir", "kunci", "buku", "laptop",
            "door", "shelf", "trash bin", "desk", "chair", "bag", "phone", "water bottle", "coffee mug"
        };

        // Always use the focused core list when building the KeywordRecognizer grammar so serialized scene lists
        // of 40+ items don't flood the recognizer with 300+ "cari ..." rules that hijack "ini apa".
        private static readonly string[] CoreSpeechObjectTerms = new string[]
        {
            "pintu", "rak", "tempat sampah", "meja", "kursi", "tas", "handphone", "botol", "botol minum", "cangkir", "kunci", "buku", "laptop",
            "door", "shelf", "trash bin", "desk", "chair", "bag", "phone", "water bottle", "coffee mug"
        };

        private string[] BuildVoiceVocabulary()
        {
            HashSet<string> phrases = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string key in _phoneticCommandAliases.Keys)
            {
                phrases.Add(key);
            }

            foreach (string item in CoreSpeechObjectTerms)
            {
                // Indonesian prefixes
                phrases.Add($"simpan {item}");
                phrases.Add($"cari {item}");
                phrases.Add($"antar ke {item}");

                // English fallback prefixes
                phrases.Add($"save {item}");
                phrases.Add($"find {item}");
                phrases.Add($"navigate to {item}");
            }

            return new List<string>(phrases).ToArray();
        }

        private void Start()
        {
            if (objectRecognizer == null) objectRecognizer = FindObjectOfType<ObjectRecognizer>();
            if (navigator == null) navigator = FindObjectOfType<SpatialBeaconAndNavigator>();
            if (obstacleDetector == null) obstacleDetector = FindObjectOfType<ObstacleDetector>();
            if (FindObjectOfType<RealWorldObjectDetector>() == null) gameObject.AddComponent<RealWorldObjectDetector>();

            if (Camera.main != null)
            {
                Camera.main.backgroundColor = new Color(0f, 0f, 0f, 0f);
                Vector3 euler = Camera.main.transform.eulerAngles;
                _yaw = euler.y;
                _pitch = euler.x;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || WINDOWS_UWP
            isRealHoloLensRoomMode = true;
#endif
            GameObject simEnv = GameObject.Find("ByYourSight_PrototypeEnvironment");
            if (simEnv != null && isRealHoloLensRoomMode)
            {
                simEnv.SetActive(false);
            }

            InitializeNativeHoloLensSpeech();

            // Startup audio greeting in Bahasa Indonesia
            if (PriorityAudioManager.Instance != null)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "By Your Sight siap. Ucapkan Bantuan untuk daftar perintah",
                    AudioPriority.P3_ObjectIdentity
                );
            }
        }

        private void InitializeNativeHoloLensSpeech()
        {
#if UNITY_STANDALONE_WIN || UNITY_WSA || UNITY_EDITOR_WIN
            try
            {
                // ConfidenceLevel.Low allows Indonesian pronunciation on the native Windows recognizer
                _keywordRecognizer = new KeywordRecognizer(BuildVoiceVocabulary(), ConfidenceLevel.Low);
                _keywordRecognizer.OnPhraseRecognized += args =>
                {
                    ProcessVoiceCommand(args.text, fromMicrophone: true);
                };
                _keywordRecognizer.Start();
                Debug.Log("[Voice] Native HoloLens KeywordRecognizer started (Bilingual Indonesian Phonetic + English).");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Voice] Native KeywordRecognizer unavailable on this device: " + e.Message);
            }
#endif
        }

        private void OnDestroy()
        {
#if UNITY_STANDALONE_WIN || UNITY_WSA || UNITY_EDITOR_WIN
            if (_keywordRecognizer != null)
            {
                if (_keywordRecognizer.IsRunning)
                    _keywordRecognizer.Stop();
                _keywordRecognizer.Dispose();
            }
#endif
        }

        private void Update()
        {
            UpdateAmbientNoiseAdaptation();
            HandleEditorWalkAndLook();
            HandleKeyboardShortcuts();
        }

        /// <summary>
        /// KF04: Main parser for processing Bahasa Indonesia (including phonetic spellings) & English voice commands
        /// (both from HoloLens native offline KeywordRecognizer and Editor simulation buttons).
        /// </summary>
        public void ProcessVoiceCommand(string rawCommand, bool fromMicrophone = false)
        {
            if (string.IsNullOrWhiteSpace(rawCommand) || PriorityAudioManager.Instance == null)
                return;

            string trimmed = rawCommand.Trim().TrimEnd('?', '.', '!');
            string cmd = trimmed.ToLowerInvariant();
            if (_phoneticCommandAliases.TryGetValue(cmd, out string canonical))
            {
                cmd = canonical;
            }

            Debug.Log($"[Voice] Recognized: \"{rawCommand}\" -> \"{cmd}\" (fromMic={fromMicrophone})");
            PriorityAudioManager.Instance.PlayEarcon(EarconType.ListeningStart);

            // 1. "Diam" / "Mute" (KF06)
            if (cmd == "diam" || cmd == "mute" || cmd == "quiet" || cmd == "silence")
            {
                PriorityAudioManager.Instance.SetMute(true);
                return;
            }

            // 2. "Lanjut" / "Resume" (KF06)
            if (cmd == "lanjut" || cmd == "resume" || cmd == "unmute" || cmd == "speak")
            {
                PriorityAudioManager.Instance.SetMute(false);
                return;
            }

            // If muted, automatically unmute when the user explicitly issues a new query
            if (PriorityAudioManager.Instance.isMuted)
            {
                PriorityAudioManager.Instance.isMuted = false;
            }

            // 3. "Ini apa?" / "What is this?" (KF01)
            if (cmd == "ini apa" || cmd.Contains("ini apa") || cmd.Contains("apa ini") ||
                cmd.Contains("what is this") || cmd.Contains("what's this") || cmd == "identify")
            {
                objectRecognizer?.IdentifyObjectInGaze();
                return;
            }

            // "Daftar simpanan" / "List saved"
            if (cmd == "daftar simpanan" || cmd == "list saved")
            {
                if (fromMicrophone && objectRecognizer != null && objectRecognizer.HasActiveCameraObjectInView())
                {
                    objectRecognizer.IdentifyObjectInGaze();
                    return;
                }
                List<string> saved = objectRecognizer != null ? objectRecognizer.GetSavedLabels() : new List<string>();
                PriorityAudioManager.Instance.EnqueueSpeech(
                    saved.Count == 0 ? "Belum ada objek tersimpan" : "Tersimpan: " + string.Join(", ", saved),
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            // "Hapus simpanan" / "Clear saved"
            if (cmd == "hapus simpanan" || cmd == "clear saved" || cmd == "delete saved")
            {
                objectRecognizer?.ClearSavedAnchors();
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Semua objek tersimpan dihapus",
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            // 4. "Simpan [label]" / "Save object [label]" (KF02)
            if (cmd == "simpan objek" || cmd.StartsWith("simpan") || cmd.StartsWith("seem pan") || cmd.StartsWith("save"))
            {
                string label = "";
                if (cmd != "simpan objek")
                {
                    if (cmd.StartsWith("seem pan")) label = trimmed.Substring(8).Trim();
                    else if (cmd.StartsWith("simpan")) label = trimmed.Substring(6).Trim();
                    else if (cmd.StartsWith("save")) label = trimmed.Substring(4).Trim();

                    string lowerLabel = label.ToLowerInvariant();
                    if (lowerLabel.StartsWith("objek")) label = label.Substring(5).Trim();
                    else if (lowerLabel.StartsWith("benda")) label = label.Substring(5).Trim();
                    else if (lowerLabel.StartsWith("object")) label = label.Substring(6).Trim();
                    else if (lowerLabel.StartsWith("item")) label = label.Substring(4).Trim();
                }

                if (string.IsNullOrEmpty(label))
                    label = _customSaveLabel;

                objectRecognizer?.SaveCustomObjectInGaze(label);
                return;
            }

            // 5. "Cari [nama benda]" / "Find [object name]" (KF03)
            if (cmd.StartsWith("cari ") || cmd.StartsWith("chah ree ") || cmd.StartsWith("cha ree ") || cmd.StartsWith("find "))
            {
                string targetName;
                if (cmd.StartsWith("chah ree ")) targetName = trimmed.Substring(9).Trim();
                else if (cmd.StartsWith("cha ree ")) targetName = trimmed.Substring(8).Trim();
                else targetName = trimmed.Substring(5).Trim();

                string indoTarget = RealWorldObjectDetector.ToIndonesianLabel(targetName);
                TrackableObject found = objectRecognizer != null ? objectRecognizer.FindObjectByName(targetName) : null;
                if (found != null && navigator != null)
                {
                    navigator.StartBeaconForObject(found);
                }
                else if (fromMicrophone && objectRecognizer != null && objectRecognizer.HasActiveCameraObjectInView())
                {
                    // Windows en-US KeywordRecognizer often misrecognizes spoken Indonesian "ini apa" as "cari [item]".
                    // If the target item wasn't saved and the user is pointing the camera at a live detected object,
                    // identify the object in front of them!
                    objectRecognizer.IdentifyObjectInGaze();
                }
                else
                {
                    PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        $"{indoTarget} belum disimpan. Arahkan pandangan lalu ucapkan Simpan {indoTarget}",
                        AudioPriority.P3_ObjectIdentity
                    );
                }
                return;
            }

            // 6. "Antar ke [tujuan]" / "Navigasi ke [tujuan]" / "Navigate to [destination]" (KF05)
            if (cmd.StartsWith("antar ke ") || cmd.StartsWith("un tar ke ") || cmd.StartsWith("navigasi ke ") ||
                cmd.StartsWith("navigate to ") || cmd.StartsWith("take me to "))
            {
                string dest;
                if (cmd.StartsWith("antar ke ")) dest = trimmed.Substring(9).Trim();
                else if (cmd.StartsWith("un tar ke ")) dest = trimmed.Substring(10).Trim();
                else if (cmd.StartsWith("navigasi ke ")) dest = trimmed.Substring(12).Trim();
                else if (cmd.StartsWith("navigate to ")) dest = trimmed.Substring(12).Trim();
                else dest = trimmed.Substring(11).Replace("the ", "").Trim();

                navigator?.StartNavigationTo(dest, fromMicrophone);
                return;
            }

            // 7. "Ulangi" & "Detail" (T3 Design Principles)
            if (cmd == "ulangi" || cmd == "repeat")
            {
                PriorityAudioManager.Instance.RepeatLastSpeech(detailed: false);
                return;
            }
            if (cmd == "detail" || cmd == "details")
            {
                PriorityAudioManager.Instance.RepeatLastSpeech(detailed: true);
                return;
            }

            // 8. "Batal" / "Berhenti" / "Cancel" / "Stop"
            if (cmd == "batal" || cmd == "berhenti" || cmd == "cancel" || cmd == "stop")
            {
                navigator?.StopAllGuidance(announce: true);
                return;
            }

            // 9. "Bantuan" / "Help"
            if (cmd == "bantuan" || cmd == "help")
            {
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Ucapkan: Ini apa, Simpan objek, Cari benda, Antar ke pintu, atau Diam",
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
            PriorityAudioManager.Instance.EnqueueSpeech(
                "Perintah tidak dikenali. Ucapkan Bantuan",
                AudioPriority.P3_ObjectIdentity
            );
        }

        /// <summary>
        /// KF06: Adaptively adjusts output volume multiplier based on ambient noise level.
        /// </summary>
        private void UpdateAmbientNoiseAdaptation()
        {
            if (PriorityAudioManager.Instance == null)
                return;

            // As ambient noise increases (0..1), volume multiplier scales from 0.85x to 1.45x
            PriorityAudioManager.Instance.adaptiveVolumeMultiplier = Mathf.Lerp(0.85f, 1.45f, simulatedAmbientNoiseLevel);
        }

        private void HandleEditorWalkAndLook()
        {
            if (Camera.main == null)
                return;

            Transform cam = Camera.main.transform;

            // 1. If HoloLens 2 (OpenXR / Holographic Remoting) is connected, apply real 6DoF head tracking!
            if (XRSettings.isDeviceActive)
            {
                InputDevice headDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
                if (headDevice.isValid)
                {
                    if (headDevice.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 headPos))
                    {
                        cam.localPosition = headPos;
                    }
                    if (headDevice.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion headRot))
                    {
                        cam.localRotation = headRot;
                    }
                }
            }

            if (!enableEditorWalkSimulation)
                return;

            // Hold right mouse button to rotate head gaze (only when not overridden by active XR headset)
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * mouseLookSensitivity * 2.5f;
                _pitch -= Input.GetAxis("Mouse Y") * mouseLookSensitivity * 2.5f;
                _pitch = Mathf.Clamp(_pitch, -65f, 65f);
                cam.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            // Q/E keys also rotate head horizontally without a mouse
            if (Input.GetKey(KeyCode.Q))
            {
                _yaw -= 65f * Time.deltaTime;
                cam.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }
            if (Input.GetKey(KeyCode.E))
            {
                _yaw += 65f * Time.deltaTime;
                cam.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            // WASD to walk inside the simulated room
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            Vector3 forwardFlat = new Vector3(cam.forward.x, 0f, cam.forward.z).normalized;
            Vector3 rightFlat = new Vector3(cam.right.x, 0f, cam.right.z).normalized;
            Vector3 move = (forwardFlat * v + rightFlat * h).normalized;

            if (move.sqrMagnitude > 0.01f)
            {
                cam.position += move * (walkSpeed * Time.deltaTime);
            }
        }

        public void ToggleRealHoloLensRoomMode()
        {
            isRealHoloLensRoomMode = !isRealHoloLensRoomMode;
            GameObject simEnv = GameObject.Find("ByYourSight_PrototypeEnvironment");
            if (simEnv != null)
            {
                simEnv.SetActive(!isRealHoloLensRoomMode);
            }

            if (PriorityAudioManager.Instance != null)
            {
                string modeMsg = isRealHoloLensRoomMode
                    ? "Mode ruangan nyata aktif"
                    : "Mode simulasi aktif";
                PriorityAudioManager.Instance.EnqueueSpeech(modeMsg, AudioPriority.P3_ObjectIdentity);
            }
        }

        private void HandleKeyboardShortcuts()
        {
            // Quick keyboard shortcuts for demo & usability testing (Wizard of Oz backup)
            if (Input.GetKeyDown(KeyCode.Alpha1)) ProcessVoiceCommand("Ini apa");
            if (Input.GetKeyDown(KeyCode.Alpha2)) ProcessVoiceCommand($"Simpan {_customSaveLabel}");
            if (Input.GetKeyDown(KeyCode.Alpha3)) ProcessVoiceCommand("Cari Botol minum");
            if (Input.GetKeyDown(KeyCode.Alpha4)) ProcessVoiceCommand("Cari Cangkir");
            if (Input.GetKeyDown(KeyCode.Alpha5)) ProcessVoiceCommand("Antar ke Pintu");
            if (Input.GetKeyDown(KeyCode.Alpha6)) ProcessVoiceCommand("Antar ke Meja");
            if (Input.GetKeyDown(KeyCode.Alpha7)) ProcessVoiceCommand("Ulangi");
            if (Input.GetKeyDown(KeyCode.Alpha8)) ProcessVoiceCommand("Detail");
            if (Input.GetKeyDown(KeyCode.Alpha9)) ProcessVoiceCommand("Batal");

            // One-handed gesture (KF06): 'M' key toggles Diam / Lanjut
            if (Input.GetKeyDown(KeyCode.M) && PriorityAudioManager.Instance != null)
            {
                bool nextMute = !PriorityAudioManager.Instance.isMuted;
                ProcessVoiceCommand(nextMute ? "Diam" : "Lanjut");
            }

            // 'R' key toggles between Real HoloLens Room (Spatial Mesh) vs Simulated Room
            if (Input.GetKeyDown(KeyCode.R))
            {
                ToggleRealHoloLensRoomMode();
            }
        }

        /// <summary>
        /// High-Contrast HUD (T3 Design System: Yellow/Cyan/Red on dark background)
        /// for HoloLens 2 simulation & testing all KF01-KF08 features in Unity Editor.
        /// </summary>
        private void OnGUI()
        {
            // Center Head Gaze Reticle (High-Contrast Yellow per T3)
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            GUI.color = new Color(1f, 0.92f, 0.1f, 0.95f);
            GUI.Box(new Rect(cx - 8, cy - 8, 16, 16), "+");

            // Large Low-Vision Friendly Audio Status & Subtitle Banner
            GUIStyle bannerStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            if (PriorityAudioManager.Instance != null)
            {
                string activeSpeech = !string.IsNullOrEmpty(PriorityAudioManager.Instance.currentSpokenText)
                    ? PriorityAudioManager.Instance.currentSpokenText
                    : PriorityAudioManager.Instance.lastSpokenText;

                if (obstacleDetector != null && obstacleDetector.isHazardDetected)
                {
                    GUI.color = new Color(1f, 0.22f, 0.1f, 1f); // Red/Orange Hazard (P0)
                }
                else if (PriorityAudioManager.Instance.isMuted)
                {
                    GUI.color = new Color(0.75f, 0.75f, 0.75f, 1f);
                }
                else
                {
                    GUI.color = new Color(1f, 0.92f, 0.1f, 1f); // High-Contrast Yellow Info/Target
                }

                string muteTag = PriorityAudioManager.Instance.isMuted ? " [DIAM - P0 Tetap Aktif]" : "";
                GUI.Box(
                    new Rect(Screen.width * 0.18f, 16, Screen.width * 0.64f, 54),
                    $"SUARA ({PriorityAudioManager.Instance.currentPriority}){muteTag}: \"{activeSpeech}\"",
                    bannerStyle
                );
            }

            // Voice Command Simulation Control Panel (KF01 - KF08) on Left Screen
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(16, 80, 355, 580), "By Your Sight — Bahasa Indonesia (ID/EN)", GUI.skin.window);

            string modeLabel = isRealHoloLensRoomMode
                ? "[R] Mode: RUANGAN NYATA HOLOLENS (Klik untuk Sim)"
                : "[R] Mode: RUANGAN SIMULASI (Klik untuk Nyata)";
            if (GUILayout.Button(modeLabel))
            {
                ToggleRealHoloLensRoomMode();
            }

            GUILayout.Label("Jalan: WASD | Menoleh: HoloLens atau Q/E/Klik-Kanan");

            if (_objectDetector == null) _objectDetector = FindObjectOfType<RealWorldObjectDetector>();
            if (_objectDetector != null && _objectDetector.enableDetection)
            {
                string camStatus = _objectDetector.isServerConnected
                    ? $"Kamera AI: AKTIF | {_objectDetector.trackedObjectCount} objek | lihat: {(_objectDetector.lastDetectedLabels == "" ? "-" : _objectDetector.lastDetectedLabels)}"
                    : "Kamera AI: MATI (jalankan detection_server.py)";
                GUILayout.Label(camStatus);
            }
            GUILayout.Space(6);

            if (GUILayout.Button("[1] KF01: \"Ini apa?\" (Deteksi Pandangan)"))
                ProcessVoiceCommand("Ini apa");

            GUILayout.BeginHorizontal();
            _customSaveLabel = GUILayout.TextField(_customSaveLabel, GUILayout.Width(135));
            if (GUILayout.Button("[2] KF02: \"Simpan objek\""))
                ProcessVoiceCommand($"Simpan {_customSaveLabel}");
            GUILayout.EndHorizontal();

            if (GUILayout.Button("[3] KF03: \"Cari Botol minum\" (Beacon)"))
                ProcessVoiceCommand("Cari Botol minum");

            if (GUILayout.Button("[4] KF03: \"Cari Cangkir\" (Beacon)"))
                ProcessVoiceCommand("Cari Cangkir");

            if (GUILayout.Button("[5] KF05: \"Antar ke Pintu\" (Rute P1)"))
                ProcessVoiceCommand("Antar ke Pintu");

            if (GUILayout.Button("[6] KF05: \"Antar ke Meja\" (Rute P1)"))
                ProcessVoiceCommand("Antar ke Meja");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("[7] \"Ulangi\""))
                ProcessVoiceCommand("Ulangi");
            if (GUILayout.Button("[8] \"Detail\""))
                ProcessVoiceCommand("Detail");
            if (GUILayout.Button("[9] \"Batal\""))
                ProcessVoiceCommand("Batal");
            GUILayout.EndHorizontal();

            if (GUILayout.Button("[M] KF06 Gestur/Suara: \"Diam\" / \"Lanjut\""))
            {
                bool nextMute = PriorityAudioManager.Instance != null && !PriorityAudioManager.Instance.isMuted;
                ProcessVoiceCommand(nextMute ? "Diam" : "Lanjut");
            }

            GUILayout.Space(6);
            GUILayout.Label($"KF06 Kebisingan Sekitar (Volume: {(PriorityAudioManager.Instance != null ? PriorityAudioManager.Instance.adaptiveVolumeMultiplier : 1f):0.00}x):");
            simulatedAmbientNoiseLevel = GUILayout.HorizontalSlider(simulatedAmbientNoiseLevel, 0f, 1f);

            GUILayout.Space(6);
            if (GUILayout.Button("KF08: Simulasikan Orang Mendekat (P2)"))
            {
                TriggerPersonApproachingDemo();
            }

            GUILayout.Space(6);
            GUILayout.Label("Perintah Suara Kustom (KF04):");
            GUILayout.BeginHorizontal();
            _customCommandInput = GUILayout.TextField(_customCommandInput);
            if (GUILayout.Button("Ucapkan", GUILayout.Width(75)))
            {
                ProcessVoiceCommand(_customCommandInput);
            }
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        private void TriggerPersonApproachingDemo()
        {
            TrackableObject[] all = FindObjectsOfType<TrackableObject>();
            foreach (var obj in all)
            {
                if (obj.category == SemanticObjectCategory.Person)
                {
                    if (Camera.main != null)
                    {
                        Transform cam = Camera.main.transform;
                        obj.transform.position = cam.position + cam.forward * 3.2f + cam.right * 1.4f;
                        obj.transform.position = new Vector3(obj.transform.position.x, 1.0f, obj.transform.position.z);
                    }
                    obj.simulateApproachingUser = true;
                    return;
                }
            }
        }
    }
}
