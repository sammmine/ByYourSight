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
        [Range(0f, 1f)] public float minConfidence = 0.15f;

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
        public float personForgetSeconds = 4.0f;
        [Tooltip("Objects stay remembered this long after they were last seen, even when out of view")]
        public float objectForgetSeconds = 300f;
        [Tooltip("An object that should be in view but isn't detected this many times in a row is removed")]
        public int missesBeforeRemoval = 80;

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
        public class VisibleObjectItem
        {
            public TrackableObject marker;
            public string label;
            public Vector3 position;
            public float distance;
            public string direction;
            public string detailedDescription;
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
            public float lastCx = 0.5f;
            public float lastCy = 0.5f;
            public float lastEstimatedDistance = 1.2f;
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
            { "cup", "gelas" },
            { "wine glass", "gelas" },
            { "glass", "gelas" },
            { "gelas", "gelas" },
            { "guh las", "gelas" },
            { "coffee mug", "cangkir" },
            { "mug", "cangkir" },
            { "cangkir", "cangkir" },
            { "chung keer", "cangkir" },
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
            { "tvmonitor", "televisi" },
            { "monitor", "televisi" },
            { "screen", "televisi" },
            { "display", "televisi" },
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
        private string _lastServerJson = "";
        private DetectionResponse _latestResponse;
        private float _latestResponseWallTime = -100f;
        private DetectionResponse _latestNonEmptyResponse;
        private float _latestNonEmptyWallTime = -100f;
        private float _lastNonEmptyBatchTime = -100f;
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
            // Ensure Unity accepts low-confidence detections (cups, TVs, laptops on live_low stream)
            if (minConfidence > 0.15f)
                minConfidence = 0.15f;
            if (missesBeforeRemoval < 60)
                missesBeforeRemoval = 60;
#if UNITY_EDITOR || UNITY_STANDALONE
            serverUrl = "http://127.0.0.1:8765/detections";
#endif
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
                    req.timeout = 4;
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
                            _lastServerJson = req.downloadHandler.text;
                            resp = JsonUtility.FromJson<DetectionResponse>(_lastServerJson);
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

#if WINDOWS_UWP && !UNITY_EDITOR
                        // Standalone UWP build on HoloLens 2: probe embedded laptop IPs and local subnet
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
                            yield return StartCoroutine(ScanLocalSubnetForServer());
                            foundAlternative = isServerConnected;
                        }

                        if (!foundAlternative)
                        {
                            if (!_warnedOffline)
                            {
                                Debug.LogWarning($"[Detection] Detection server not reachable at {serverUrl} ({req.error}). Retrying every 2 s.");
                                _warnedOffline = true;
                            }
                            yield return new WaitForSeconds(2f);
                        }
#else
                        if (!_warnedOffline)
                        {
                            Debug.LogWarning($"[Detection] Detection server not reachable at {serverUrl} ({req.error}). " +
                                             "Start Tools/ObjectDetectionServer/detection_server.py on this laptop. Retrying in 1 s.");
                            _warnedOffline = true;
                        }
                        yield return new WaitForSeconds(1.0f);
#endif
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

            _latestResponse = resp;
            _latestResponseWallTime = requestTime;

            if (resp.detections.Length == 0)
            {
                if (Time.realtimeSinceStartup - _latestNonEmptyWallTime > 12f)
                    lastDetectedLabels = "";
                return;
            }

            _latestNonEmptyResponse = resp;
            _latestNonEmptyWallTime = requestTime;

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

            float effectiveAge = Mathf.Clamp(resp.age, 0f, 2.0f);
            float frameStamp = requestTime - effectiveAge;
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
            float confThreshold = Mathf.Min(minConfidence, 0.15f);
            float batchTime = Time.time;
            _lastNonEmptyBatchTime = batchTime;

            foreach (DetectionDto d in resp.detections)
            {
                if (d == null || string.IsNullOrEmpty(d.label) || d.conf < confThreshold)
                    continue;

                string label = MapLabel(d.label);
                bool isPerson = label == "orang" || label == "person";

                // Ignore user's own hand/arm near the bottom edge of the camera frame
                if (isPerson && d.cy > 0.76f && d.h < 0.42f)
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
                    // Combine height (~1.65 m) and shoulder/torso width (~0.52 m) so distance decreases smoothly
                    // even when the person is close enough that their head/feet are clipped by the 16:9 frame.
                    float distFromHeight = 1.65f / Mathf.Max(0.06f, d.h * 2f * tanV);
                    float distFromWidth = 0.52f / Mathf.Max(0.05f, d.w * 2f * tanH);
                    bool cutOff = (d.cy + d.h * 0.5f > 0.94f) || (d.cy - d.h * 0.5f < 0.06f) || (d.h > 0.74f);
                    float estimate = cutOff
                        ? Mathf.Min(distFromHeight, distFromWidth)
                        : (distFromHeight * 0.6f + distFromWidth * 0.4f);
                    distance = Mathf.Clamp(estimate, 0.5f, maxDetectionDistance);
                }
                else if (label == "televisi")
                {
                    // Wall-mounted or stand TV (~0.65 m tall): use wall mesh distance when scanned, otherwise box height
                    if (hasSurface)
                    {
                        distance = hit.distance;
                    }
                    else
                    {
                        float tvEstimate = 0.65f / Mathf.Max(0.05f, d.h * 2f * tanV);
                        distance = Mathf.Clamp(tvEstimate, 0.8f, maxDetectionDistance);
                    }
                }
                else if (!isHazard)
                {
                    // Everyday objects (laptop, mug/cup, water bottle, phone, book, etc.)
                    float refHeight = label == "laptop" ? 0.30f : 0.24f;
                    float handheldEstimate = refHeight / Mathf.Max(0.04f, d.h * 2f * tanV);
                    if (!hasSurface)
                    {
                        distance = Mathf.Clamp(handheldEstimate, 0.5f, 3.0f);
                    }
                    else if (d.h >= 0.14f && hit.distance > handheldEstimate + 0.5f)
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
                    // Furniture / hazard object (chair, desk, shelf, door, trash bin, bag, etc.)
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

                float minSize = label == "televisi" ? 0.75f : (!isHazard && !isPerson ? 0.36f : 0.15f);
                float width = Mathf.Clamp(d.w * 2f * tanH * distance, minSize, 3f);
                float height = Mathf.Clamp(d.h * 2f * tanV * distance, minSize, 2.2f);
                float depth = Mathf.Clamp(width, 0.25f, 0.6f);
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
                    float follow = isPerson ? 0.85f : 0.35f;
                    m.position = Vector3.Lerp(m.position, point, follow);
                    m.localScale = Vector3.Lerp(m.localScale, size, 0.3f);
                    FaceUser(m, pose.position);
                }

                t.lastSeen = batchTime;
                t.lastCx = d.cx;
                t.lastCy = d.cy;
                t.lastEstimatedDistance = distance;
                t.lastCenterOffset = Vector2.Distance(new Vector2(d.cx, d.cy), new Vector2(0.5f, 0.5f));
                t.misses = 0;
                t.marker.detailedDescription = $"{Capitalize(label)} terdeteksi kamera, keyakinan {Mathf.RoundToInt(d.conf * 100f)} persen";
                matched.Add(t);
                if (!labels.Contains(label)) labels.Add(label);
            }

            if (labels.Count > 0)
                lastDetectedLabels = string.Join(", ", labels);

            if (!forceProcess)
            {
                // Keep EverydayObject markers (cup, laptop, TV, bottle, phone) anchored even if YOLO flickers for a few frames
                for (int i = _tracked.Count - 1; i >= 0; i--)
                {
                    TrackedDetection t = _tracked[i];
                    if (t.isPerson || matched.Contains(t) || t.marker == null)
                        continue;
                    if (t.marker.category == SemanticObjectCategory.EverydayObject)
                        continue;

                    if (IsVisibleInFrame(t.marker.transform.position, pose.position, camRot, tanH, tanV))
                    {
                        t.misses++;
                        if (t.misses >= missesBeforeRemoval)
                            RemoveAt(i);
                    }
                }
            }
        }

        private static bool IsGenericTableLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
                return false;
            return string.Equals(label, "meja", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(label, "meja kerja", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(label, "table", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(label, "dining table", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(label, "desk", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the most central non-person object actively detected on the camera stream (http://127.0.0.1:8765)
        /// within the last maxAgeSeconds. Prioritizes specific objects (kursi, buku, gelas, cangkir, botol, laptop, TV)
        /// over generic tables (meja), and picks the object closest to the center of the camera frame (cx=0.5, cy=0.5).
        /// </summary>
        public TrackableObject GetMostCentralActiveDetection(float maxAgeSeconds = 20.0f)
        {
            DetectionResponse activeResp = (_latestResponse != null && _latestResponse.detections != null && _latestResponse.detections.Length > 0)
                ? _latestResponse
                : _latestNonEmptyResponse;
            float activeWallTime = (activeResp == _latestResponse) ? _latestResponseWallTime : _latestNonEmptyWallTime;

            if (activeResp != null && (Time.realtimeSinceStartup - activeWallTime) <= maxAgeSeconds)
            {
                ProcessDetections(activeResp, activeWallTime, forceProcess: true);
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

                bool inLatestBatch = Mathf.Abs(t.lastSeen - _lastNonEmptyBatchTime) <= 0.75f;
                int batchPrio = inLatestBatch ? 0 : 1;
                int tablePrio = IsGenericTableLabel(t.label) ? 1 : 0;
                float score = batchPrio * 100f + tablePrio * 10f + t.lastCenterOffset;
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
        /// Prioritizes objects in the current camera frame, ranks specific objects (kursi, buku, gelas, cangkir,
        /// botol minum, laptop, televisi, handphone) above generic tables (meja), and picks the object closest
        /// to the center of the camera frame.
        /// </summary>
        public bool TryGetBestLiveDetection(
            Transform head,
            out TrackableObject bestMarker,
            out string indonesianLabel,
            out Vector3 targetPoint,
            out float distanceMeters,
            out string detailedDescription,
            float maxAgeSeconds = 25.0f)
        {
            bestMarker = null;
            indonesianLabel = "";
            targetPoint = Vector3.zero;
            distanceMeters = 0f;
            detailedDescription = "";

            DetectionResponse activeResp = (_latestResponse != null && _latestResponse.detections != null && _latestResponse.detections.Length > 0)
                ? _latestResponse
                : _latestNonEmptyResponse;
            float activeWallTime = (activeResp == _latestResponse) ? _latestResponseWallTime : _latestNonEmptyWallTime;

            if (activeResp != null && (Time.realtimeSinceStartup - activeWallTime) <= maxAgeSeconds)
            {
                ProcessDetections(activeResp, activeWallTime, forceProcess: true);
            }

            float now = Time.time;
            Transform cam = head != null ? head : GetCameraTransform();
            Vector3 headPos = cam != null ? cam.position : Vector3.zero;
            Quaternion headRot = cam != null ? cam.rotation : Quaternion.identity;

            List<TrackedDetection> latestBatchCandidates = new List<TrackedDetection>();
            List<TrackedDetection> allCandidates = new List<TrackedDetection>();

            foreach (TrackedDetection t in _tracked)
            {
                if (t == null || t.isPerson || t.marker == null)
                    continue;
                float age = now - t.lastSeen;
                if (age > maxAgeSeconds)
                    continue;

                allCandidates.Add(t);
                if (Mathf.Abs(t.lastSeen - _lastNonEmptyBatchTime) <= 0.75f && age <= 15.0f)
                {
                    latestBatchCandidates.Add(t);
                }
            }

            List<TrackedDetection> candidates = latestBatchCandidates.Count > 0 ? latestBatchCandidates : allCandidates;
            if (candidates.Count == 0)
                return false;

            candidates.Sort((a, b) =>
            {
                // Specific objects (kursi, buku, gelas, cangkir, botol minum, laptop, televisi, etc.) always beat generic table ("meja")
                int tableA = IsGenericTableLabel(a.label) ? 1 : 0;
                int tableB = IsGenericTableLabel(b.label) ? 1 : 0;
                if (tableA != tableB)
                    return tableA.CompareTo(tableB);

                // Next, prefer the object closest to the center of the camera frame (cx=0.5, cy=0.5)
                return a.lastCenterOffset.CompareTo(b.lastCenterOffset);
            });

            TrackedDetection best = candidates[0];
            bestMarker = best.marker;

            float tanH = Mathf.Tan(cameraHorizontalFov * 0.5f * Mathf.Deg2Rad);
            float tanV = tanH * (9f / 16f);
            Vector3 localDir = new Vector3((best.lastCx - 0.5f) * 2f * tanH, -(best.lastCy - 0.5f) * 2f * tanV, 1f).normalized;
            Vector3 worldDir = headRot * localDir;

            distanceMeters = Mathf.Clamp(
                best.lastEstimatedDistance > 0.1f ? best.lastEstimatedDistance : Vector3.Distance(headPos, best.marker.transform.position),
                0.5f,
                maxDetectionDistance
            );
            targetPoint = headPos + worldDir * distanceMeters;
            best.marker.transform.position = targetPoint;
            detailedDescription = best.marker.detailedDescription;
            indonesianLabel = ToIndonesianLabel(best.label);

            return true;
        }

        /// <summary>
        /// Resolves up to maxCount visible objects for "Ada apa saja?" ("What is around?").
        /// Prioritizes current camera frame objects, distinct object types over duplicate labels,
        /// specific objects (EverydayObjects / Hazards / People) over generic tables,
        /// and orders them by visual importance (center offset).
        /// </summary>
        public bool TryGetVisibleObjects(
            Transform head,
            int maxCount,
            out List<VisibleObjectItem> results,
            float maxAgeSeconds = 25.0f)
        {
            results = new List<VisibleObjectItem>();

            DetectionResponse activeResp = (_latestResponse != null && _latestResponse.detections != null && _latestResponse.detections.Length > 0)
                ? _latestResponse
                : _latestNonEmptyResponse;
            float activeWallTime = (activeResp == _latestResponse) ? _latestResponseWallTime : _latestNonEmptyWallTime;

            if (activeResp != null && (Time.realtimeSinceStartup - activeWallTime) <= maxAgeSeconds)
            {
                ProcessDetections(activeResp, activeWallTime, forceProcess: true);
            }

            float now = Time.time;
            Transform cam = head != null ? head : GetCameraTransform();
            Vector3 headPos = cam != null ? cam.position : Vector3.zero;
            Quaternion headRot = cam != null ? cam.rotation : Quaternion.identity;

            List<TrackedDetection> latestBatchCandidates = new List<TrackedDetection>();
            List<TrackedDetection> allCandidates = new List<TrackedDetection>();

            foreach (TrackedDetection t in _tracked)
            {
                if (t == null || t.marker == null)
                    continue;
                float age = now - t.lastSeen;
                if (age > maxAgeSeconds)
                    continue;

                allCandidates.Add(t);
                if (Mathf.Abs(t.lastSeen - _lastNonEmptyBatchTime) <= 0.75f && age <= 15.0f)
                {
                    latestBatchCandidates.Add(t);
                }
            }

            List<TrackedDetection> candidates = latestBatchCandidates.Count > 0 ? latestBatchCandidates : allCandidates;
            if (candidates.Count == 0)
                return false;

            // Sort candidates by priority:
            // 1. Specific objects (buku, gelas, botol, laptop, kursi, orang, TV, etc.) beat generic table ("meja")
            // 2. Objects closer to the camera center (cx=0.5, cy=0.5)
            candidates.Sort((a, b) =>
            {
                int tableA = IsGenericTableLabel(a.label) ? 1 : 0;
                int tableB = IsGenericTableLabel(b.label) ? 1 : 0;
                if (tableA != tableB)
                    return tableA.CompareTo(tableB);

                return a.lastCenterOffset.CompareTo(b.lastCenterOffset);
            });

            float tanH = Mathf.Tan(cameraHorizontalFov * 0.5f * Mathf.Deg2Rad);
            float tanV = tanH * (9f / 16f);

            HashSet<string> seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Pass 1: pick distinct object labels so we get a diverse set of up to maxCount items
            foreach (TrackedDetection t in candidates)
            {
                if (results.Count >= maxCount)
                    break;

                string indo = ToIndonesianLabel(t.label);
                if (seenLabels.Contains(indo))
                    continue;

                Vector3 localDir = new Vector3((t.lastCx - 0.5f) * 2f * tanH, -(t.lastCy - 0.5f) * 2f * tanV, 1f).normalized;
                Vector3 worldDir = headRot * localDir;

                float dist = Mathf.Clamp(
                    t.lastEstimatedDistance > 0.1f ? t.lastEstimatedDistance : Vector3.Distance(headPos, t.marker.transform.position),
                    0.5f,
                    maxDetectionDistance
                );
                Vector3 targetPoint = headPos + worldDir * dist;
                t.marker.transform.position = targetPoint;
                string dir = PriorityAudioManager.GetRelativeBodyDirection(cam, targetPoint);

                results.Add(new VisibleObjectItem
                {
                    marker = t.marker,
                    label = indo,
                    position = targetPoint,
                    distance = dist,
                    direction = dir,
                    detailedDescription = t.marker.detailedDescription
                });
                seenLabels.Add(indo);
            }

            // Pass 2: if still under maxCount (e.g. multiple items of same label), include remaining candidates
            if (results.Count < maxCount)
            {
                foreach (TrackedDetection t in candidates)
                {
                    if (results.Count >= maxCount)
                        break;

                    bool alreadyAdded = false;
                    foreach (var res in results)
                    {
                        if (res.marker == t.marker)
                        {
                            alreadyAdded = true;
                            break;
                        }
                    }
                    if (alreadyAdded)
                        continue;

                    string indo = ToIndonesianLabel(t.label);
                    Vector3 localDir = new Vector3((t.lastCx - 0.5f) * 2f * tanH, -(t.lastCy - 0.5f) * 2f * tanV, 1f).normalized;
                    Vector3 worldDir = headRot * localDir;

                    float dist = Mathf.Clamp(
                        t.lastEstimatedDistance > 0.1f ? t.lastEstimatedDistance : Vector3.Distance(headPos, t.marker.transform.position),
                        0.5f,
                        maxDetectionDistance
                    );
                    Vector3 targetPoint = headPos + worldDir * dist;
                    t.marker.transform.position = targetPoint;
                    string dir = PriorityAudioManager.GetRelativeBodyDirection(cam, targetPoint);

                    results.Add(new VisibleObjectItem
                    {
                        marker = t.marker,
                        label = indo,
                        position = targetPoint,
                        distance = dist,
                        direction = dir,
                        detailedDescription = t.marker.detailedDescription
                    });
                }
            }

            return results.Count > 0;
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
