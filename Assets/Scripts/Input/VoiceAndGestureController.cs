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
        private string _customCommandInput = "Find Water bottle";
        private string _customSaveLabel = "House Keys";

#if UNITY_STANDALONE_WIN || UNITY_WSA || UNITY_EDITOR_WIN
        private KeywordRecognizer _keywordRecognizer;
        private readonly string[] _nativeHoloLensKeywords = new string[]
        {
            "What is this",
            "Identify",
            "Save object",
            "Save item",
            "Save water bottle",
            "Save coffee mug",
            "Save house keys",
            "Find water bottle",
            "Find coffee mug",
            "Find small box",
            "Find house keys",
            "Navigate to door",
            "Take me to the door",
            "Navigate to desk",
            "Take me to the desk",
            "Repeat",
            "Details",
            "Mute",
            "Quiet",
            "Resume",
            "Unmute",
            "Cancel",
            "Stop",
            "Help"
        };
#endif

        private void Start()
        {
            if (objectRecognizer == null) objectRecognizer = FindObjectOfType<ObjectRecognizer>();
            if (navigator == null) navigator = FindObjectOfType<SpatialBeaconAndNavigator>();
            if (obstacleDetector == null) obstacleDetector = FindObjectOfType<ObstacleDetector>();

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

            // Startup audio greeting
            if (PriorityAudioManager.Instance != null)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "By Your Sight ready. Say Help for commands",
                    AudioPriority.P3_ObjectIdentity
                );
            }
        }

        private void InitializeNativeHoloLensSpeech()
        {
#if UNITY_STANDALONE_WIN || UNITY_WSA || UNITY_EDITOR_WIN
            try
            {
                _keywordRecognizer = new KeywordRecognizer(_nativeHoloLensKeywords, ConfidenceLevel.Medium);
                _keywordRecognizer.OnPhraseRecognized += args =>
                {
                    ProcessVoiceCommand(args.text);
                };
                _keywordRecognizer.Start();
                Debug.Log("[Voice] Native HoloLens KeywordRecognizer started (Offline English).");
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
        /// KF04: Main parser for processing English voice commands
        /// (both from HoloLens native offline KeywordRecognizer and Editor simulation buttons).
        /// </summary>
        public void ProcessVoiceCommand(string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand) || PriorityAudioManager.Instance == null)
                return;

            string cmd = rawCommand.Trim().ToLowerInvariant();
            PriorityAudioManager.Instance.PlayEarcon(EarconType.ListeningStart);

            // 1. "Mute" / "Quiet" (KF06)
            if (cmd == "mute" || cmd == "quiet" || cmd == "silence")
            {
                PriorityAudioManager.Instance.SetMute(true);
                return;
            }

            // 2. "Resume" / "Unmute" (KF06)
            if (cmd == "resume" || cmd == "unmute" || cmd == "speak")
            {
                PriorityAudioManager.Instance.SetMute(false);
                return;
            }

            // If muted, automatically unmute when the user explicitly issues a new query
            if (PriorityAudioManager.Instance.isMuted)
            {
                PriorityAudioManager.Instance.isMuted = false;
            }

            // 3. "What is this?" / "Identify" (KF01)
            if (cmd.Contains("what is this") || cmd.Contains("what's this") || cmd == "identify")
            {
                objectRecognizer?.IdentifyObjectInGaze();
                return;
            }

            // 4. "Save object [label]" (KF02)
            if (cmd.StartsWith("save object") || cmd.StartsWith("save item") || cmd.StartsWith("save"))
            {
                string label = rawCommand.Length > 11
                    ? rawCommand.Substring(rawCommand.ToLowerInvariant().IndexOf("save") + 4)
                        .Replace("object", "")
                        .Replace("item", "")
                        .Trim()
                    : _customSaveLabel;
                if (string.IsNullOrEmpty(label)) label = _customSaveLabel;
                objectRecognizer?.SaveCustomObjectInGaze(label);
                return;
            }

            // 5. "Find [object name]" (KF03)
            if (cmd.StartsWith("find "))
            {
                string targetName = rawCommand.Substring(5).Trim();
                TrackableObject found = objectRecognizer != null ? objectRecognizer.FindObjectByName(targetName) : null;
                if (found != null && navigator != null)
                {
                    navigator.StartBeaconForObject(found);
                }
                else
                {
                    PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        $"{targetName} not found nearby",
                        AudioPriority.P3_ObjectIdentity
                    );
                }
                return;
            }

            // 6. "Navigate to [destination]" / "Take me to [destination]" (KF05)
            if (cmd.StartsWith("navigate to ") || cmd.StartsWith("take me to "))
            {
                string dest = cmd.StartsWith("navigate to ")
                    ? rawCommand.Substring(12).Trim()
                    : rawCommand.Substring(11).Replace("the ", "").Trim();
                navigator?.StartNavigationTo(dest);
                return;
            }

            // 7. "Repeat" & "Details" (T3 Design Principles)
            if (cmd == "repeat")
            {
                PriorityAudioManager.Instance.RepeatLastSpeech(detailed: false);
                return;
            }
            if (cmd == "details" || cmd == "detail")
            {
                PriorityAudioManager.Instance.RepeatLastSpeech(detailed: true);
                return;
            }

            // 8. "Cancel" / "Stop"
            if (cmd == "cancel" || cmd == "stop")
            {
                navigator?.StopAllGuidance(announce: true);
                return;
            }

            // 9. "Help"
            if (cmd == "help")
            {
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Say: What is this, Find item, Navigate to door, or Mute",
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
            PriorityAudioManager.Instance.EnqueueSpeech(
                "Command not recognized. Say Help",
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
                    ? "Real room spatial scan active"
                    : "Simulation room active";
                PriorityAudioManager.Instance.EnqueueSpeech(modeMsg, AudioPriority.P3_ObjectIdentity);
            }
        }

        private void HandleKeyboardShortcuts()
        {
            // Quick keyboard shortcuts for demo & usability testing
            if (Input.GetKeyDown(KeyCode.Alpha1)) ProcessVoiceCommand("What is this?");
            if (Input.GetKeyDown(KeyCode.Alpha2)) ProcessVoiceCommand($"Save object {_customSaveLabel}");
            if (Input.GetKeyDown(KeyCode.Alpha3)) ProcessVoiceCommand("Find Water bottle");
            if (Input.GetKeyDown(KeyCode.Alpha4)) ProcessVoiceCommand("Find Coffee mug");
            if (Input.GetKeyDown(KeyCode.Alpha5)) ProcessVoiceCommand("Navigate to Door");
            if (Input.GetKeyDown(KeyCode.Alpha6)) ProcessVoiceCommand("Navigate to Desk");
            if (Input.GetKeyDown(KeyCode.Alpha7)) ProcessVoiceCommand("Repeat");
            if (Input.GetKeyDown(KeyCode.Alpha8)) ProcessVoiceCommand("Details");
            if (Input.GetKeyDown(KeyCode.Alpha9)) ProcessVoiceCommand("Cancel");

            // One-handed gesture (KF06): 'M' key toggles Mute / Resume
            if (Input.GetKeyDown(KeyCode.M) && PriorityAudioManager.Instance != null)
            {
                bool nextMute = !PriorityAudioManager.Instance.isMuted;
                ProcessVoiceCommand(nextMute ? "Mute" : "Resume");
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

                string muteTag = PriorityAudioManager.Instance.isMuted ? " [MUTED - P0 Active]" : "";
                GUI.Box(
                    new Rect(Screen.width * 0.18f, 16, Screen.width * 0.64f, 54),
                    $"SPEECH ({PriorityAudioManager.Instance.currentPriority}){muteTag}: \"{activeSpeech}\"",
                    bannerStyle
                );
            }

            // Voice Command Simulation Control Panel (KF01 - KF08) on Left Screen
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(16, 80, 340, 580), "By Your Sight — HoloLens Native (EN)", GUI.skin.window);

            string modeLabel = isRealHoloLensRoomMode
                ? "[R] Mode: REAL HOLOLENS ROOM (Click for Sim)"
                : "[R] Mode: SIMULATED ROOM (Click for Real Room)";
            if (GUILayout.Button(modeLabel))
            {
                ToggleRealHoloLensRoomMode();
            }

            GUILayout.Label("Walk: WASD | Turn Head: HoloLens or Q/E/Right-Click");
            GUILayout.Space(6);

            if (GUILayout.Button("[1] KF01: \"What is this?\" (Gaze Detect)"))
                ProcessVoiceCommand("What is this?");

            GUILayout.BeginHorizontal();
            _customSaveLabel = GUILayout.TextField(_customSaveLabel, GUILayout.Width(135));
            if (GUILayout.Button("[2] KF02: \"Save object\""))
                ProcessVoiceCommand($"Save object {_customSaveLabel}");
            GUILayout.EndHorizontal();

            if (GUILayout.Button("[3] KF03: \"Find Water bottle\" (Beacon)"))
                ProcessVoiceCommand("Find Water bottle");

            if (GUILayout.Button("[4] KF03: \"Find Coffee mug\" (Beacon)"))
                ProcessVoiceCommand("Find Coffee mug");

            if (GUILayout.Button("[5] KF05: \"Navigate to Door\" (Route P1)"))
                ProcessVoiceCommand("Navigate to Door");

            if (GUILayout.Button("[6] KF05: \"Navigate to Desk\" (Route P1)"))
                ProcessVoiceCommand("Navigate to Desk");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("[7] \"Repeat\""))
                ProcessVoiceCommand("Repeat");
            if (GUILayout.Button("[8] \"Details\""))
                ProcessVoiceCommand("Details");
            if (GUILayout.Button("[9] \"Cancel\""))
                ProcessVoiceCommand("Cancel");
            GUILayout.EndHorizontal();

            if (GUILayout.Button("[M] KF06 Gesture/Voice: \"Mute\" / \"Resume\""))
            {
                bool nextMute = PriorityAudioManager.Instance != null && !PriorityAudioManager.Instance.isMuted;
                ProcessVoiceCommand(nextMute ? "Mute" : "Resume");
            }

            GUILayout.Space(6);
            GUILayout.Label($"KF06 Ambient Noise (Volume: {(PriorityAudioManager.Instance != null ? PriorityAudioManager.Instance.adaptiveVolumeMultiplier : 1f):0.00}x):");
            simulatedAmbientNoiseLevel = GUILayout.HorizontalSlider(simulatedAmbientNoiseLevel, 0f, 1f);

            GUILayout.Space(6);
            if (GUILayout.Button("KF08: Simulate Approaching Person (P2)"))
            {
                TriggerPersonApproachingDemo();
            }

            GUILayout.Space(6);
            GUILayout.Label("Custom Voice Command (KF04):");
            GUILayout.BeginHorizontal();
            _customCommandInput = GUILayout.TextField(_customCommandInput);
            if (GUILayout.Button("Speak", GUILayout.Width(75)))
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
