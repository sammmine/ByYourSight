using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ByYourSight.Perception
{
    /// <summary>
    /// Opsi 2 — Real-world object detection for KF01 / KF03 / KF05 / KF07 / KF08.
    ///
    /// Polls the laptop detection server (Tools/ObjectDetectionServer/detection_server.py), which runs YOLO on the
    /// HoloLens 2 PV camera stream from Windows Device Portal. Every 2D box is projected from the head pose at the
    /// moment the frame was captured, raycast against the real Spatial Mesh to get a 3D position, and kept as an
    /// invisible TrackableObject marker ("Detected_chair"). The existing features then work on real objects unchanged:
    /// "What is this?" names it, "Find chair" / "Navigate to chair" guide to it, ObstacleDetector names it in hazard
    /// messages, and people trigger KF08 "Person approaching".
    /// </summary>
    public class RealWorldObjectDetector : MonoBehaviour
    {
        [Header("Detection Server (Tools/ObjectDetectionServer)")]
        public bool enableDetection = true;
        public string serverUrl = "http://127.0.0.1:8765/detections";
        public float pollIntervalSeconds = 0.25f;
        [Range(0f, 1f)] public float minConfidence = 0.25f;

        [Header("HoloLens 2 PV Camera Projection")]
        [Tooltip("Horizontal field of view of the HoloLens 2 photo/video camera in the live stream")]
        public float cameraHorizontalFov = 64.7f;
        [Tooltip("Tilt between the PV camera and the head gaze. Positive = camera looks lower than the gaze.")]
        public float cameraPitchOffsetDegrees = 0f;
        [Tooltip("Delay of the Device Portal live stream. If markers land on the side you just turned away from, increase it.")]
        public float streamLatencySeconds = 1.0f;
        public float maxDetectionDistance = 6f;

        [Header("Tracking")]
        public float mergeRadius = 0.7f;
        public float personMergeRadius = 1.2f;
        [Tooltip("People move, so their markers disappear quickly when no longer seen")]
        public float personForgetSeconds = 2.5f;
        [Tooltip("Objects stay remembered this long after they were last seen, even when out of view")]
        public float objectForgetSeconds = 300f;
        [Tooltip("An object that should be in view but isn't detected this many times in a row is removed")]
        public int missesBeforeRemoval = 8;

        [Header("People (KF08) & \"Ada apa saja?\" Summary")]
        [Tooltip("People below this confidence are not tracked or announced as nearby. \"Ada apa saja?\" uses minConfidence so it matches the camera preview.")]
        [Range(0f, 1f)] public float announceMinConfidence = 0.35f;
        [Tooltip("Used when the top or bottom of a person is outside the frame")]
        public float personShoulderWidthMeters = 0.5f;
        [Tooltip("A person box cut off at the bottom whose top edge is below this frame height (0 = top, 1 = bottom) is treated as the user's own hand or arm")]
        [Range(0f, 1f)] public float ownHandMinTopEdge = 0.65f;
        [Tooltip("\"Ada apa saja?\" only uses a camera frame newer than this")]
        public float maxSummaryFrameAgeSeconds = 3f;
        [Tooltip("YOLO flickers between frames, so \"Ada apa saja?\" uses the fullest frame received within this time")]
        public float summaryLookbackSeconds = 2f;
        [Tooltip("Items this close to the frame center count as \"di depan\"")]
        [Range(0.05f, 0.5f)] public float summaryCenterHalfWidth = 0.17f;
        [Tooltip("Maximum label-and-direction groups spoken at once. \"Detail\" reads the full list.")]
        public int summaryMaxGroups = 5;

        [Header("Status (Read-Only)")]
        public bool isServerConnected = false;
        public int trackedObjectCount = 0;
        public string lastDetectedLabels = "";

        public const string MarkerPrefix = "Detected_";

        public static bool IsDetectionMarker(TrackableObject t)
        {
            return t != null && t.gameObject.name.StartsWith(MarkerPrefix);
        }

        [Serializable]
        private class DetectionDto
        {
            public string label;
            public float conf;
            public float cx;
            public float cy;
            public float w;
            public float h;
        }

        [Serializable]
        private class DetectionResponse
        {
            public float age;
            public int width;
            public int height;
            public DetectionDto[] detections;
        }

        private class TrackedDetection
        {
            public TrackableObject marker;
            public string label;
            public bool isPerson;
            public float lastSeen;
            public float lastCenterOffset;
            public int misses;
        }

        private struct HeadPose
        {
            public float time;
            public Vector3 position;
            public Quaternion rotation;
        }

        // YOLO (COCO) & bilingual voice terms -> spoken Bahasa Indonesia label
        private static readonly Dictionary<string, string> LabelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "person", "orang" },
            { "orang", "orang" },
            { "cup", "cangkir" },
            { "coffee mug", "cangkir" },
            { "mug", "cangkir" },
            { "gelas", "cangkir" },
            { "cangkir", "cangkir" },
            { "chung keer", "cangkir" },
            { "guh las", "cangkir" },
            { "bottle", "botol minum" },
            { "water bottle", "botol minum" },
            { "botol", "botol minum" },
            { "botol air", "botol minum" },
            { "botol minum", "botol minum" },
            { "bo tol", "botol minum" },
            { "cell phone", "handphone" },
            { "phone", "handphone" },
            { "handphone", "handphone" },
            { "hp", "handphone" },
            { "hape", "handphone" },
            { "hah peh", "handphone" },
            { "ponsel", "handphone" },
            { "dining table", "meja" },
            { "table", "meja" },
            { "desk", "meja" },
            { "work desk", "meja kerja" },
            { "meja", "meja" },
            { "meja kerja", "meja kerja" },
            { "may jah", "meja" },
            { "meh ja", "meja" },
            { "chair", "kursi" },
            { "kursi", "kursi" },
            { "koor see", "kursi" },
            { "couch", "sofa" },
            { "sofa", "sofa" },
            { "bed", "tempat tidur" },
            { "kasur", "tempat tidur" },
            { "tempat tidur", "tempat tidur" },
            { "kah soor", "tempat tidur" },
            { "backpack", "tas" },
            { "handbag", "tas" },
            { "suitcase", "tas" },
            { "bag", "tas" },
            { "tas", "tas" },
            { "tuss", "tas" },
            { "tv", "televisi" },
            { "television", "televisi" },
            { "televisi", "televisi" },
            { "potted plant", "tanaman" },
            { "plant", "tanaman" },
            { "tanaman", "tanaman" },
            { "keys", "kunci" },
            { "house keys", "kunci" },
            { "kunci", "kunci" },
            { "koon chee", "kunci" },
            { "box", "kotak" },
            { "small box", "kotak" },
            { "kotak", "kotak" },
            { "ko tak", "kotak" },
            { "trash can", "tempat sampah" },
            { "trash bin", "tempat sampah" },
            { "sampah", "tempat sampah" },
            { "tempat sampah", "tempat sampah" },
            { "sum pah", "tempat sampah" },
            { "refrigerator", "rak" },
            { "fridge", "rak" },
            { "shelf", "rak" },
            { "rack", "rak" },
            { "rak", "rak" },
            { "rak buku", "rak" },
            { "ruck", "rak" },
            { "cabinet", "lemari" },
            { "lemari", "lemari" },
            { "luh mah ree", "lemari" },
            { "door", "pintu" },
            { "exit", "pintu" },
            { "exit door", "pintu" },
            { "pintu", "pintu" },
            { "pintu keluar", "pintu" },
            { "peen too", "pintu" },
            { "pin too", "pintu" },
            { "window", "jendela" },
            { "jendela", "jendela" },
            { "stairs", "tangga" },
            { "tangga", "tangga" },
            { "tung gah", "tangga" },
            { "pillar", "tiang" },
            { "tiang", "tiang" },
            { "book", "buku" },
            { "buku", "buku" },
            { "boo koo", "buku" },
            { "laptop", "laptop" },
            { "toilet", "toilet" },
            { "sink", "wastafel" },
            { "wastafel", "wastafel" },
            { "fire hydrant", "hidran" },
            { "hidran", "hidran" },
            { "hee drun", "hidran" },
            { "bench", "bangku" },
            { "keyboard", "keyboard" },
            { "mouse", "mouse" },
            { "remote", "remot" },
            { "scissors", "gunting" },
            { "clock", "jam dinding" },
            { "vase", "vas" },
            { "umbrella", "payung" },
            { "bowl", "mangkok" },
            { "spoon", "sendok" },
            { "fork", "garpu" },
            { "knife", "pisau" }
        };

        // Objects that are dangerous to walk into (KF07). Everything else is an everyday object (KF01).
        private static readonly HashSet<string> HazardLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chair", "table", "desk", "sofa", "bed", "bench", "toilet", "plant", "bicycle", "motorcycle",
            "refrigerator", "shelf", "rack", "cabinet", "stairs", "door", "pillar", "trash bin", "bag", "fire hydrant", "stop sign",
            "car", "parking meter",
            "kursi", "meja", "meja kerja", "tempat tidur", "bangku", "tanaman", "rak", "lemari",
            "tangga", "pintu", "tiang", "tempat sampah", "tas", "hidran"
        };

        private readonly List<HeadPose> _poseHistory = new List<HeadPose>();
        private readonly List<TrackedDetection> _tracked = new List<TrackedDetection>();
        private readonly List<KeyValuePair<float, DetectionResponse>> _recentResponses = new List<KeyValuePair<float, DetectionResponse>>();
        private DetectionResponse _latestResponse;
        private float _latestResponseWallTime = -100f;
        private bool _warnedOffline = false;
        private float _lastFrameStamp = -100f;
        private Material _markerMaterial;

        private Transform GetCameraTransform()
        {
            if (Camera.main != null)
                return Camera.main.transform;
            Camera anyCam = FindObjectOfType<Camera>();
            return anyCam != null ? anyCam.transform : transform;
        }

        private void OnEnable()
        {
            // Ensure Unity accepts all detections shown on the http://127.0.0.1:8765 preview (server default conf = 0.25)
            if (minConfidence > 0.25f)
                minConfidence = 0.25f;
            StartCoroutine(PollLoop());
        }

        private void Update()
        {
            Transform head = GetCameraTransform();
            if (head != null)
            {
                _poseHistory.Add(new HeadPose { time = Time.realtimeSinceStartup, position = head.position, rotation = head.rotation });
                while (_poseHistory.Count > 0 && Time.realtimeSinceStartup - _poseHistory[0].time > 5f)
                    _poseHistory.RemoveAt(0);
            }

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedDetection t = _tracked[i];
                float forget = t.isPerson ? personForgetSeconds : objectForgetSeconds;
                if (t.marker == null || Time.time - t.lastSeen > forget)
                    RemoveAt(i);
            }
            trackedObjectCount = _tracked.Count;
        }

        public bool IngestRawDetectionJson(string json, float requestTime)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;
            try
            {
                DetectionResponse resp = JsonUtility.FromJson<DetectionResponse>(json);
                if (resp != null)
                {
                    isServerConnected = true;
                    _warnedOffline = false;
                    ProcessDetections(resp, requestTime, forceProcess: true);
                    return resp.detections != null && resp.detections.Length > 0;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Detection] Could not parse live server response: " + e.Message);
            }
            return false;
        }

        private IEnumerator PollLoop()
        {
            List<string> candidateUrls = BuildCandidateServerUrls();

            while (true)
            {
                if (!enableDetection)
                {
                    yield return new WaitForSeconds(0.5f);
                    continue;
                }

                float requestTime = Time.realtimeSinceStartup; // wall clock: editor hitches must not shift pose matching
                using (UnityWebRequest req = UnityWebRequest.Get(serverUrl))
                {
                    req.timeout = 2;
                    yield return req.SendWebRequest();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        if (!isServerConnected)
                            Debug.Log($"[Detection] Connected to detection server at {serverUrl}");
                        isServerConnected = true;
                        _warnedOffline = false;

                        DetectionResponse resp = null;
                        try
                        {
                            resp = JsonUtility.FromJson<DetectionResponse>(req.downloadHandler.text);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[Detection] Could not read server response: " + e.Message);
                        }

                        if (resp != null)
                            ProcessDetections(resp, requestTime);
                    }
                    else
                    {
                        isServerConnected = false;

                        // If 127.0.0.1 failed (e.g. running as a standalone UWP build on HoloLens 2 while
                        // detection_server.py is running on the Windows laptop), probe embedded laptop IPs!
                        bool foundAlternative = false;
                        foreach (string altUrl in candidateUrls)
                        {
                            if (altUrl == serverUrl)
                                continue;

                            float probeTime = Time.realtimeSinceStartup;
                            using (UnityWebRequest probe = UnityWebRequest.Get(altUrl))
                            {
                                probe.timeout = 1;
                                yield return probe.SendWebRequest();
                                if (probe.result == UnityWebRequest.Result.Success)
                                {
                                    serverUrl = altUrl;
                                    isServerConnected = true;
                                    _warnedOffline = false;
                                    foundAlternative = true;
                                    Debug.Log($"[Detection] Auto-discovered laptop detection server at {serverUrl}");

                                    try
                                    {
                                        DetectionResponse resp = JsonUtility.FromJson<DetectionResponse>(probe.downloadHandler.text);
                                        if (resp != null)
                                            ProcessDetections(resp, probeTime);
                                    }
                                    catch { }
                                    break;
                                }
                            }
                        }

                        if (!foundAlternative)
                        {
                            // Also scan the local /24 Wi-Fi subnet in parallel batches in case the laptop IP changed
                            yield return StartCoroutine(ScanLocalSubnetForServer());
                            foundAlternative = isServerConnected;
                        }

                        if (!foundAlternative)
                        {
                            if (!_warnedOffline)
                            {
                                Debug.LogWarning($"[Detection] Detection server not reachable at {serverUrl} ({req.error}). " +
                                                 "Start Tools/ObjectDetectionServer/detection_server.py on this laptop. Retrying every 3 s.");
                                _warnedOffline = true;
                            }
                            yield return new WaitForSeconds(3f);
                        }
                    }
                }

                yield return new WaitForSeconds(pollIntervalSeconds);
            }
        }

        private List<string> BuildCandidateServerUrls()
        {
            List<string> urls = new List<string>();
            if (!string.IsNullOrWhiteSpace(serverUrl))
                urls.Add(serverUrl.Trim());

            string localhostUrl = "http://127.0.0.1:8765/detections";
            if (!urls.Contains(localhostUrl))
                urls.Add(localhostUrl);

            TextAsset hostsAsset = Resources.Load<TextAsset>("DetectionServerHosts");
            if (hostsAsset != null && !string.IsNullOrWhiteSpace(hostsAsset.text))
            {
                string[] lines = hostsAsset.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string ip = line.Trim();
                    if (!string.IsNullOrEmpty(ip))
                    {
                        string candidate = $"http://{ip}:8765/detections";
                        if (!urls.Contains(candidate))
                            urls.Add(candidate);
                    }
                }
            }

            return urls;
        }

        private IEnumerator ScanLocalSubnetForServer()
        {
            List<string> prefixes = new List<string>();
            try
            {
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                    {
                        string[] parts = ip.ToString().Split('.');
                        if (parts.Length == 4)
                        {
                            string prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";
                            if (!prefixes.Contains(prefix))
                                prefixes.Add(prefix);
                        }
                    }
                }
            }
            catch { }

            const int batchSize = 32;
            foreach (string prefix in prefixes)
            {
                for (int start = 1; start <= 254 && !isServerConnected; start += batchSize)
                {
                    int end = Mathf.Min(254, start + batchSize - 1);
                    List<UnityWebRequest> batch = new List<UnityWebRequest>(end - start + 1);
                    for (int host = start; host <= end; host++)
                    {
                        string url = $"http://{prefix}{host}:8765/detections";
                        UnityWebRequest r = UnityWebRequest.Get(url);
                        r.timeout = 1;
                        r.SendWebRequest();
                        batch.Add(r);
                    }

                    float waitUntil = Time.realtimeSinceStartup + 0.65f;
                    while (Time.realtimeSinceStartup < waitUntil)
                    {
                        bool anyPending = false;
                        foreach (UnityWebRequest r in batch)
                        {
                            if (r.isDone && r.result == UnityWebRequest.Result.Success && !isServerConnected)
                            {
                                serverUrl = r.url;
                                isServerConnected = true;
                                _warnedOffline = false;
                                Debug.Log($"[Detection] Subnet scan discovered laptop detection server at {serverUrl}");
                                break;
                            }
                            if (!r.isDone)
                                anyPending = true;
                        }
                        if (isServerConnected || !anyPending)
                            break;
                        yield return null;
                    }

                    foreach (UnityWebRequest r in batch)
                    {
                        r.Dispose();
                    }
                }
            }
        }

        private void ProcessDetections(DetectionResponse resp, float requestTime, bool forceProcess = false)
        {
            if (resp == null || resp.detections == null)
                return;

            // Allow up to 15s frame age so CPU-bound YOLO inference + Holographic Remoting never drops valid frames
            if (resp.age > 15f)
            {
                lastDetectedLabels = "";
                return;
            }

            if (!ReferenceEquals(resp, _latestResponse))
            {
                _recentResponses.Add(new KeyValuePair<float, DetectionResponse>(requestTime, resp));
                _recentResponses.RemoveAll(r => requestTime - r.Key > summaryLookbackSeconds);
            }

            _latestResponse = resp;
            _latestResponseWallTime = requestTime;

            if (_poseHistory.Count == 0)
            {
                Transform cam = GetCameraTransform();
                _poseHistory.Add(new HeadPose
                {
                    time = Time.realtimeSinceStartup,
                    position = cam != null ? cam.position : Vector3.zero,
                    rotation = cam != null ? cam.rotation : Quaternion.identity
                });
            }

            // Same frame as last poll (YOLO slower than polling): refresh lastSeen on active markers so they never expire
            float frameStamp = requestTime - Mathf.Max(0f, resp.age);
            if (!forceProcess && Mathf.Abs(frameStamp - _lastFrameStamp) < 0.03f)
            {
                foreach (DetectionDto d in resp.detections)
                {
                    if (d == null || string.IsNullOrEmpty(d.label) || d.conf < minConfidence)
                        continue;
                    string mapped = MapLabel(d.label);
                    foreach (TrackedDetection existing in _tracked)
                    {
                        // People move: only a newly processed frame may keep a person marker alive
                        if (existing != null && !existing.isPerson && existing.label == mapped)
                            existing.lastSeen = Time.time;
                    }
                }
                return;
            }
            _lastFrameStamp = frameStamp;

            // Head pose at the moment the camera frame was captured
            float captureTime = frameStamp - streamLatencySeconds;
            HeadPose pose = GetPoseAt(captureTime);

            float aspect = (resp.width > 0 && resp.height > 0) ? (float)resp.height / resp.width : 9f / 16f;
            float tanH = Mathf.Tan(cameraHorizontalFov * 0.5f * Mathf.Deg2Rad);
            float tanV = tanH * aspect;
            Quaternion camRot = pose.rotation * Quaternion.Euler(cameraPitchOffsetDegrees, 0f, 0f);

            HashSet<TrackedDetection> matched = new HashSet<TrackedDetection>();
            List<string> labels = new List<string>();

            foreach (DetectionDto d in resp.detections)
            {
                if (d == null || string.IsNullOrEmpty(d.label) || d.conf < minConfidence)
                    continue;

                string label = MapLabel(d.label);
                bool isPerson = label == "orang" || label == "person";

                // A forced re-read may reuse an old frame, which must not bring back a person who has left
                if (isPerson && (forceProcess || d.conf < announceMinConfidence || IsLikelyOwnHand(d)))
                    continue;

                // Pixel -> ray: aim at the lower-middle of the box for floor-standing things, the center otherwise
                float aimY = isPerson ? d.cy : d.cy + d.h * 0.15f;
                Vector3 localDir = new Vector3((d.cx - 0.5f) * 2f * tanH, -(aimY - 0.5f) * 2f * tanV, 1f);
                Vector3 worldDir = (camRot * localDir).normalized;

                bool isHazard = HazardLabels.Contains(label);
                bool hasSurface = RaycastRealSurface(pose.position, worldDir, maxDetectionDistance, out RaycastHit hit);
                float distance;
                if (isPerson)
                {
                    // People are rarely in the Spatial Mesh (they move), so the ray would hit the wall behind them.
                    // Estimate distance from box height, assuming a ~1.7 m tall person.
                    float estimate = 1.7f / Mathf.Max(0.05f, d.h * 2f * tanV);
                    bool cutVertically = d.cy + d.h * 0.5f > 0.97f || d.cy - d.h * 0.5f < 0.03f;
                    if (cutVertically)
                    {
                        // With a level gaze the feet leave the frame within about 4.5 m, so box height says little.
                        // Use the shoulder width instead, unless the sides are cut off too (then the person is very close).
                        bool cutSideways = d.cx - d.w * 0.5f < 0.03f || d.cx + d.w * 0.5f > 0.97f;
                        estimate = cutSideways
                            ? Mathf.Min(estimate, 1.2f)
                            : personShoulderWidthMeters / Mathf.Max(0.05f, d.w * 2f * tanH);
                    }
                    distance = Mathf.Clamp(estimate, 0.5f, maxDetectionDistance);
                    if (hasSurface && hit.distance < distance) distance = hit.distance;
                }
                else if (!isHazard)
                {
                    // Small everyday objects (phone, water bottle, mug, etc.) may be held in hand in mid-air
                    // where the Spatial Mesh only sees the distant wall behind them (or no surface at all).
                    float handheldEstimate = 0.24f / Mathf.Max(0.04f, d.h * 2f * tanV);
                    if (!hasSurface)
                    {
                        distance = Mathf.Clamp(handheldEstimate, 0.5f, 2.5f);
                    }
                    else if (d.h >= 0.16f && hit.distance > handheldEstimate + 0.5f)
                    {
                        distance = Mathf.Clamp(handheldEstimate, 0.5f, hit.distance);
                    }
                    else
                    {
                        distance = hit.distance;
                    }
                }
                else
                {
                    // Furniture / hazard object (chair, desk, shelf, door, trash bin, bag, etc.):
                    // Use Spatial Mesh hit distance when available; otherwise estimate from bounding box height
                    // so detections visible on http://127.0.0.1:8765 are never discarded when mesh is unscanned.
                    if (hasSurface)
                    {
                        distance = hit.distance;
                    }
                    else
                    {
                        float furnitureEstimate = 0.85f / Mathf.Max(0.05f, d.h * 2f * tanV);
                        distance = Mathf.Clamp(furnitureEstimate, 0.7f, maxDetectionDistance);
                    }
                }
                Vector3 point = pose.position + worldDir * distance;

                float width = Mathf.Clamp(d.w * 2f * tanH * distance, 0.1f, 3f);
                float height = Mathf.Clamp(d.h * 2f * tanV * distance, 0.1f, 2.2f);
                float depth = Mathf.Clamp(width, 0.15f, 0.6f);
                Vector3 size = new Vector3(width, height, depth);

                TrackedDetection t = FindMatch(label, point, isPerson);
                if (t == null)
                {
                    t = CreateMarker(label, isPerson, point, size, pose.position);
                    _tracked.Add(t);
                }
                else
                {
                    Transform m = t.marker.transform;
                    float follow = isPerson ? 0.8f : 0.35f;
                    m.position = Vector3.Lerp(m.position, point, follow);
                    m.localScale = Vector3.Lerp(m.localScale, size, 0.3f);
                    FaceUser(m, pose.position);
                }

                t.lastSeen = Time.time;
                t.lastCenterOffset = Vector2.Distance(new Vector2(d.cx, d.cy), new Vector2(0.5f, 0.5f));
                t.misses = 0;
                t.marker.detailedDescription = $"{Capitalize(label)} terdeteksi kamera, keyakinan {Mathf.RoundToInt(d.conf * 100f)} persen";
                matched.Add(t);
                if (!labels.Contains(label)) labels.Add(label);
            }

            lastDetectedLabels = string.Join(", ", labels);

            // Negative evidence: an object that should be visible in this frame but wasn't detected is fading away
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedDetection t = _tracked[i];
                if (t.isPerson || matched.Contains(t) || t.marker == null)
                    continue;

                if (IsVisibleInFrame(t.marker.transform.position, pose.position, camRot, tanH, tanV))
                {
                    t.misses++;
                    if (t.misses >= missesBeforeRemoval)
                        RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// A hand or arm enters the frame from the bottom and stays low. A standing person reaches the upper part of the frame.
        /// </summary>
        private bool IsLikelyOwnHand(DetectionDto d)
        {
            return d.cy + d.h * 0.5f > 0.97f && d.cy - d.h * 0.5f > ownHandMinTopEdge;
        }

        public enum ViewSummaryStatus
        {
            Ok,
            CameraNotReady,
            NothingDetected
        }

        /// <summary>
        /// "Ada apa saja?": summarizes what is in the newest camera frame only, so remembered objects are never listed.
        /// </summary>
        public ViewSummaryStatus DescribeCurrentView(out string shortSummary, out string fullSummary)
        {
            shortSummary = "";
            fullSummary = "";

            if (!enableDetection || !isServerConnected)
                return ViewSummaryStatus.CameraNotReady;

            float now = Time.realtimeSinceStartup;
            List<ViewItem> items = null;
            int rawCount = 0;

            // Oldest to newest, so the newest frame wins a tie
            foreach (KeyValuePair<float, DetectionResponse> entry in _recentResponses)
            {
                DetectionResponse resp = entry.Value;
                float sinceReceived = Mathf.Max(0f, now - entry.Key);
                if (resp == null || resp.detections == null || sinceReceived > summaryLookbackSeconds ||
                    sinceReceived + Mathf.Max(0f, resp.age) > maxSummaryFrameAgeSeconds)
                    continue;

                List<ViewItem> candidate = CollectSummaryItems(resp.detections);
                if (items == null || candidate.Count >= items.Count)
                {
                    items = candidate;
                    rawCount = resp.detections.Length;
                }
            }

            if (items == null)
                return ViewSummaryStatus.CameraNotReady;

            Debug.Log($"[Detection] Ada apa saja: {rawCount} deteksi dari server, {items.Count} dipakai (minConfidence={minConfidence:0.00})");

            return ViewSummaryBuilder.TryBuild(items, summaryCenterHalfWidth, summaryMaxGroups, out shortSummary, out fullSummary)
                ? ViewSummaryStatus.Ok
                : ViewSummaryStatus.NothingDetected;
        }

        private List<ViewItem> CollectSummaryItems(DetectionDto[] detections)
        {
            List<ViewItem> items = new List<ViewItem>();
            foreach (DetectionDto d in detections)
            {
                if (d == null || string.IsNullOrEmpty(d.label) || d.conf < minConfidence)
                    continue;

                string label = MapLabel(d.label);
                bool isPerson = label == "orang" || label == "person";
                if (isPerson && IsLikelyOwnHand(d))
                    continue;

                items.Add(new ViewItem { Label = label, IsPerson = isPerson, CenterX = d.cx, Confidence = d.conf });
            }
            return items;
        }

        /// <summary>
        /// Returns the most central non-person object actively detected on the camera stream (http://127.0.0.1:8765)
        /// within the last maxAgeSeconds. Prioritizes everyday handheld objects (phone, bottle, cup, book, laptop, etc.)
        /// closest to the center of the camera frame (cx=0.5, cy=0.5).
        /// </summary>
        public TrackableObject GetMostCentralActiveDetection(float maxAgeSeconds = 10.0f)
        {
            if (_latestResponse != null && (Time.realtimeSinceStartup - _latestResponseWallTime) <= maxAgeSeconds)
            {
                ProcessDetections(_latestResponse, _latestResponseWallTime, forceProcess: true);
            }

            TrackableObject best = null;
            float bestScore = float.MaxValue;
            float now = Time.time;

            foreach (TrackedDetection t in _tracked)
            {
                if (t == null || t.isPerson || t.marker == null)
                    continue;
                if (now - t.lastSeen > maxAgeSeconds)
                    continue;

                // Prioritize EverydayObject (0) over background furniture/HazardObstacle (1), then camera center closeness
                int catPriority = t.marker.category == SemanticObjectCategory.EverydayObject ? 0 : 1;
                float score = catPriority * 10f + t.lastCenterOffset;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = t.marker;
                }
            }

            return best;
        }

        /// <summary>
        /// Directly resolves the best live camera detection for KF01 ("Ini apa?").
        /// If two everyday objects (e.g. "Laptop" and "Cangkir") are simultaneously visible in the camera frame,
        /// combines their names ("Laptop dan Cangkir") so neither is missed.
        /// </summary>
        public bool TryGetBestLiveDetection(
            Transform head,
            out TrackableObject bestMarker,
            out string indonesianLabel,
            out Vector3 targetPoint,
            out float distanceMeters,
            out string detailedDescription,
            float maxAgeSeconds = 10.0f)
        {
            bestMarker = null;
            indonesianLabel = "";
            targetPoint = Vector3.zero;
            distanceMeters = 0f;
            detailedDescription = "";

            if (_latestResponse != null && (Time.realtimeSinceStartup - _latestResponseWallTime) <= maxAgeSeconds)
            {
                ProcessDetections(_latestResponse, _latestResponseWallTime, forceProcess: true);
            }

            float now = Time.time;
            List<TrackedDetection> candidates = new List<TrackedDetection>();
            foreach (TrackedDetection t in _tracked)
            {
                if (t == null || t.isPerson || t.marker == null)
                    continue;
                if (now - t.lastSeen > maxAgeSeconds)
                    continue;
                candidates.Add(t);
            }

            if (candidates.Count == 0)
                return false;

            candidates.Sort((a, b) =>
            {
                int prioA = a.marker.category == SemanticObjectCategory.EverydayObject ? 0 : 1;
                int prioB = b.marker.category == SemanticObjectCategory.EverydayObject ? 0 : 1;
                if (prioA != prioB)
                    return prioA.CompareTo(prioB);
                return a.lastCenterOffset.CompareTo(b.lastCenterOffset);
            });

            TrackedDetection best = candidates[0];
            bestMarker = best.marker;
            targetPoint = best.marker.transform.position;
            Vector3 headPos = head != null ? head.position : GetCameraTransform().position;
            distanceMeters = Mathf.Clamp(Vector3.Distance(headPos, targetPoint), 0.4f, maxDetectionDistance);
            detailedDescription = best.marker.detailedDescription;

            string primaryLabel = ToIndonesianLabel(best.label);
            string secondaryLabel = null;

            if (best.marker.category == SemanticObjectCategory.EverydayObject)
            {
                for (int i = 1; i < candidates.Count; i++)
                {
                    TrackedDetection other = candidates[i];
                    if (other.marker.category != SemanticObjectCategory.EverydayObject)
                        continue;
                    if (now - other.lastSeen > 4.0f)
                        continue;

                    string candidateName = ToIndonesianLabel(other.label);
                    if (!string.Equals(candidateName, primaryLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        secondaryLabel = candidateName;
                        break;
                    }
                }
            }

            indonesianLabel = string.IsNullOrEmpty(secondaryLabel)
                ? primaryLabel
                : $"{primaryLabel} dan {secondaryLabel}";

            return true;
        }

        private TrackedDetection FindMatch(string label, Vector3 point, bool isPerson)
        {
            float radius = isPerson ? personMergeRadius : mergeRadius;
            TrackedDetection best = null;
            float bestDist = radius;
            foreach (TrackedDetection t in _tracked)
            {
                if (t.label != label || t.marker == null) continue;
                float dist = Vector3.Distance(t.marker.transform.position, point);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = t;
                }
            }
            return best;
        }

        private TrackedDetection CreateMarker(string label, bool isPerson, Vector3 point, Vector3 size, Vector3 userPos)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = MarkerPrefix + label;
            go.transform.position = point;
            go.transform.localScale = size;
            FaceUser(go.transform, userPos);

            // Black = fully transparent on the HoloLens additive display; it only lights up when highlighted
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
            {
                if (_markerMaterial == null)
                {
                    Shader shader = Shader.Find("Sprites/Default");
                    if (shader != null)
                        _markerMaterial = new Material(shader) { color = new Color(0f, 0f, 0f, 0f) };
                }
                if (_markerMaterial != null)
                    r.sharedMaterial = _markerMaterial;
            }

            TrackableObject trackable = go.AddComponent<TrackableObject>();
            trackable.labelName = Capitalize(label);
            trackable.category = isPerson
                ? SemanticObjectCategory.Person
                : (HazardLabels.Contains(label) ? SemanticObjectCategory.HazardObstacle : SemanticObjectCategory.EverydayObject);
            trackable.detailedDescription = $"{Capitalize(label)} terdeteksi kamera";

            return new TrackedDetection { marker = trackable, label = label, isPerson = isPerson, lastSeen = Time.time };
        }

        private void RemoveAt(int index)
        {
            TrackedDetection t = _tracked[index];
            if (t.marker != null)
                Destroy(t.marker.gameObject);
            _tracked.RemoveAt(index);
        }

        /// <summary>
        /// Nearest hit on the real Spatial Mesh (or simulated room geometry), ignoring markers and user tags.
        /// </summary>
        private static bool RaycastRealSurface(Vector3 origin, Vector3 dir, float maxDistance, out RaycastHit best)
        {
            best = default;
            float bestDist = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(origin, dir, maxDistance))
            {
                if (h.distance >= bestDist) continue;
                if (h.collider.GetComponentInParent<TrackableObject>() != null) continue;
                best = h;
                bestDist = h.distance;
            }
            return bestDist < float.MaxValue;
        }

        private static bool IsVisibleInFrame(Vector3 target, Vector3 camPos, Quaternion camRot, float tanH, float tanV)
        {
            Vector3 local = Quaternion.Inverse(camRot) * (target - camPos);
            if (local.z < 0.5f || local.z > 4f) return false;
            // Keep a margin from the frame edges, where detection is unreliable
            if (Mathf.Abs(local.x / local.z) > tanH * 0.8f) return false;
            if (Mathf.Abs(local.y / local.z) > tanV * 0.8f) return false;

            // Hidden behind a wall or another real surface: not a miss
            Vector3 dir = target - camPos;
            if (RaycastRealSurface(camPos, dir.normalized, dir.magnitude, out RaycastHit hit) && hit.distance < dir.magnitude - 0.4f)
                return false;
            return true;
        }

        private HeadPose GetPoseAt(float time)
        {
            HeadPose best = _poseHistory[_poseHistory.Count - 1];
            float bestDelta = float.MaxValue;
            foreach (HeadPose p in _poseHistory)
            {
                float delta = Mathf.Abs(p.time - time);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = p;
                }
            }
            return best;
        }

        private static void FaceUser(Transform t, Vector3 userPos)
        {
            Vector3 toUser = userPos - t.position;
            toUser.y = 0f;
            if (toUser.sqrMagnitude > 0.001f)
                t.rotation = Quaternion.LookRotation(-toUser.normalized, Vector3.up);
        }

        /// <summary>
        /// Normalizes any English, phonetic, or Indonesian object name into its canonical Bahasa Indonesia spoken label.
        /// </summary>
        public static string ToIndonesianLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;
            return Capitalize(MapLabel(raw));
        }

        private static string MapLabel(string raw)
        {
            string key = raw.Trim().ToLowerInvariant();
            return LabelMap.TryGetValue(key, out string mapped) ? mapped : key;
        }

        private static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
