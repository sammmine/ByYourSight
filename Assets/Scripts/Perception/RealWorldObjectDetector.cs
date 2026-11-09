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
        private bool _warnedOffline = false;
        private float _lastFrameStamp = -100f;
        private Material _markerMaterial;

        private void OnEnable()
        {
            // Ensure Unity accepts all detections shown on the http://127.0.0.1:8765 preview (server default conf = 0.25)
            if (minConfidence > 0.25f)
                minConfidence = 0.25f;
            StartCoroutine(PollLoop());
        }

        private void Update()
        {
            if (Camera.main == null)
                return;

            Transform head = Camera.main.transform;
            _poseHistory.Add(new HeadPose { time = Time.realtimeSinceStartup, position = head.position, rotation = head.rotation });
            while (_poseHistory.Count > 0 && Time.realtimeSinceStartup - _poseHistory[0].time > 5f)
                _poseHistory.RemoveAt(0);

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedDetection t = _tracked[i];
                float forget = t.isPerson ? personForgetSeconds : objectForgetSeconds;
                if (t.marker == null || Time.time - t.lastSeen > forget)
                    RemoveAt(i);
            }
            trackedObjectCount = _tracked.Count;
        }

        private IEnumerator PollLoop()
        {
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
                        if (!_warnedOffline)
                        {
                            Debug.LogWarning($"[Detection] Detection server not reachable at {serverUrl} ({req.error}). " +
                                             "Start Tools/ObjectDetectionServer/detection_server.py on this laptop. Retrying every 3 s.");
                            _warnedOffline = true;
                        }
                        yield return new WaitForSeconds(3f);
                    }
                }

                yield return new WaitForSeconds(pollIntervalSeconds);
            }
        }

        private void ProcessDetections(DetectionResponse resp, float requestTime)
        {
            if (resp.detections == null || _poseHistory.Count == 0)
                return;

            // Stale result (stream dropped, server still returns the last frame): head pose can't be matched
            if (resp.age > 2f)
            {
                lastDetectedLabels = "";
                return;
            }

            // Same frame as last poll (YOLO slower than polling): already processed
            float frameStamp = requestTime - Mathf.Max(0f, resp.age);
            if (Mathf.Abs(frameStamp - _lastFrameStamp) < 0.03f)
                return;
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
                    bool cutOff = d.cy + d.h * 0.5f > 0.97f || d.cy - d.h * 0.5f < 0.03f;
                    if (cutOff) estimate = Mathf.Min(estimate, 1.2f); // body cut off by the frame = very close
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
