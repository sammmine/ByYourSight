using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using ByYourSight.Audio;

namespace ByYourSight.Perception
{
    /// <summary>
    /// Manages KF01 (Identify object in head gaze direction),
    /// KF02 (Save custom object & voice label locally), and
    /// KF08 (Detect approaching person -> Audio Priority P2).
    /// </summary>
    public class ObjectRecognizer : MonoBehaviour
    {
        [Header("Head Gaze Detection Settings (KF01)")]
        public float maxGazeDistance = 5.0f;
        public float gazeSphereRadius = 0.28f;

        [Tooltip("Simulates low-light condition per T3 Technical Constraints")]
        public bool simulateLowLightWarning = false;

        [Header("Approaching Person Detection (KF08 - P2)")]
        [Tooltip("Person detection range: 1.0m - 3.0m (100 cm - 300 cm per M2/T3 Acceptance Criteria)")]
        public float minPersonDetectionDistance = 1.0f;
        public float personDetectionRadius = 3.0f;
        public float personAlertCooldown = 8.0f;

        [Header("Nearby Person Notice (KF08 - P2)")]
        [Tooltip("A person this close and in front of the user is announced once, even when standing still")]
        public float personNearbyDistance = 2.0f;
        [Tooltip("Half-angle around the head direction that counts as in front of the user")]
        public float personNearbyHalfAngle = 30f;
        [Tooltip("The person must stay in front this long before being announced")]
        public float personNearbyConfirmSeconds = 0.5f;
        [Tooltip("The notice can repeat after nobody has been nearby for this long")]
        public float personNearbyResetSeconds = 5f;

        /// <summary>Last sentence spoken by "Ada apa saja?", so "Batal" can stop it.</summary>
        public string LastViewSummary { get; private set; } = "";

        private readonly Dictionary<TrackableObject, float> _lastPersonAlertTime = new Dictionary<TrackableObject, float>();
        private readonly Dictionary<TrackableObject, float> _previousPersonDistance = new Dictionary<TrackableObject, float>();
        private readonly Dictionary<TrackableObject, float> _firstPersonSeenTime = new Dictionary<TrackableObject, float>();
        private readonly Dictionary<TrackableObject, float> _lastPersonSampleTime = new Dictionary<TrackableObject, float>();
        private readonly Dictionary<TrackableObject, int> _personApproachStreak = new Dictionary<TrackableObject, int>();
        private float _lastPersonSpeechTime = -999f;
        private float _nextNearbyPersonCheckTime = 0f;
        private float _nearbyPersonSince = -1f;
        private float _lastNearbyPersonTime = -999f;
        private bool _nearbyPersonAnnounced = false;

        [System.Serializable]
        private class SavedSpatialAnchorData
        {
            public string label;
            public float x;
            public float y;
            public float z;
        }

        [System.Serializable]
        private class SavedSpatialAnchorCollection
        {
            public List<SavedSpatialAnchorData> anchors = new List<SavedSpatialAnchorData>();
        }

        private static string SavedAnchorsFilePath =>
            Path.Combine(Application.persistentDataPath, "saved_spatial_anchors.json");

        private void Start()
        {
            // Reset/hapus data objek tersimpan lama dari testing sebelumnya saat aplikasi dijalankan
            ClearSavedAnchors();
            LoadSavedAnchorsFromDisk();
        }

        private void Update()
        {
            MonitorApproachingPeople();
        }

        /// <summary>
        /// KF01: Triggered when the user says "Ini apa?" / "What is this?"
        /// T3 output pattern: [Apa] + [Jarak] + [Arah], e.g., "Botol minum, 1 setengah meter, di depan"
        /// Prioritizes live AI camera detections from detection_server.py (http://127.0.0.1:8765/detections)
        /// so objects visible on the camera preview (laptop, cup, bottle, phone, etc.) are always announced first.
        /// </summary>
        public void IdentifyObjectInGaze()
        {
            if (PriorityAudioManager.Instance == null)
                return;

            Camera activeCam = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
            if (activeCam == null)
                return;

            if (simulateLowLightWarning)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Kurang jelas, cahaya redup. Dekatkan ke objek",
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            Transform head = activeCam.transform;
            Ray gazeRay = new Ray(head.position, head.forward);

            RealWorldObjectDetector camDetector = FindObjectOfType<RealWorldObjectDetector>();
            if (camDetector == null)
                camDetector = gameObject.AddComponent<RealWorldObjectDetector>();

            // 1. Highest Priority: Live AI Camera Detection from http://127.0.0.1:8765/detections
            if (camDetector != null && camDetector.TryGetBestLiveDetection(
                    head,
                    out TrackableObject camTrackable,
                    out string camObjName,
                    out Vector3 camPoint,
                    out float camDist,
                    out string camDetail))
            {
                AnnounceIdentifiedObject(camObjName, camDist, camPoint, head, camTrackable, camDetail);
                return;
            }

            // 2. If no live detection is cached yet, do an immediate on-demand check of the detection server
            //    before falling back to scene/mesh raycast so camera preview objects are never missed.
            StartCoroutine(IdentifyWithLiveServerCheckCoroutine(head, gazeRay, camDetector));
        }

        private IEnumerator IdentifyWithLiveServerCheckCoroutine(Transform head, Ray gazeRay, RealWorldObjectDetector camDetector)
        {
            if (camDetector != null && camDetector.enableDetection)
            {
                string primaryUrl = string.IsNullOrWhiteSpace(camDetector.serverUrl)
                    ? "http://127.0.0.1:8765/detections"
                    : camDetector.serverUrl.Trim();

                float reqTime = Time.realtimeSinceStartup;
                using (UnityWebRequest req = UnityWebRequest.Get(primaryUrl))
                {
                    req.timeout = 3;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        camDetector.IngestRawDetectionJson(req.downloadHandler.text, reqTime);
                    }
                }

                if (head != null && camDetector.TryGetBestLiveDetection(
                        head,
                        out TrackableObject camTrackable,
                        out string camObjName,
                        out Vector3 camPoint,
                        out float camDist,
                        out string camDetail))
                {
                    AnnounceIdentifiedObject(camObjName, camDist, camPoint, head, camTrackable, camDetail);
                    yield break;
                }
            }

            if (head != null)
            {
                ExecuteGazeRaycastIdentification(gazeRay, head);
            }
        }

        private void ExecuteGazeRaycastIdentification(Ray gazeRay, Transform head)
        {
            if (TryFindBestTargetInGaze(gazeRay, head, out RaycastHit hit, out TrackableObject trackable, out Vector3 targetPoint, out float rawDistance))
            {
                string objName;
                if (trackable != null)
                {
                    objName = RealWorldObjectDetector.ToIndonesianLabel(trackable.labelName);
                }
                else
                {
                    // No camera detection: say so honestly instead of guessing "Meja"/"Dinding" from the Spatial Mesh,
                    // so the tester (and developer) can tell whether the camera AI is connected and seeing anything.
                    RealWorldObjectDetector det = FindObjectOfType<RealWorldObjectDetector>();
                    if (det == null || !det.isServerConnected)
                        objName = "Kamera AI tidak terhubung";
                    else
                        objName = "Kamera belum mengenali benda";
                }

                string detail = trackable != null ? trackable.detailedDescription : "";
                AnnounceIdentifiedObject(objName, rawDistance, targetPoint, head, trackable, detail);
            }
            else
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Tidak ada objek di depan",
                    AudioPriority.P3_ObjectIdentity
                );
            }
        }

        private void AnnounceIdentifiedObject(
            string objName,
            float rawDistance,
            Vector3 targetPoint,
            Transform head,
            TrackableObject trackable,
            string customDetail)
        {
            string distText = PriorityAudioManager.FormatDistance(rawDistance);
            string dirText = PriorityAudioManager.GetRelativeBodyDirection(head, targetPoint);

            string shortMsg = $"{objName}, {distText}, {dirText}";
            string detailMsg = !string.IsNullOrEmpty(customDetail)
                ? $"{objName}, {distText} {dirText}. {customDetail}"
                : $"{objName}, {distText}, {dirText}";

            if (trackable != null)
            {
                trackable.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 3.0f);
            }
            else
            {
                SpawnTemporaryRealWorldHighlightBox(targetPoint, TrackableObject.HighContrastYellow, 3.0f);
            }

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, targetPoint);
            PriorityAudioManager.Instance.EnqueueSpeech(
                shortMsg,
                AudioPriority.P3_ObjectIdentity,
                targetPoint,
                detailMsg
            );
        }

        /// <summary>
        /// "Ada apa saja?" / "What is there?"
        /// Announces up to maxCount (default 3) objects visible to the user.
        /// Prioritizes live AI camera detections (http://127.0.0.1:8765/detections),
        /// with graceful fallback to scene objects if testing in simulation mode.
        /// </summary>
        public void IdentifyAllVisibleObjects(int maxCount = 3)
        {
            if (PriorityAudioManager.Instance == null)
                return;

            Camera activeCam = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
            if (activeCam == null)
                return;

            if (simulateLowLightWarning)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Kurang jelas, cahaya redup. Dekatkan ke objek",
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            Transform head = activeCam.transform;

            RealWorldObjectDetector camDetector = FindObjectOfType<RealWorldObjectDetector>();
            if (camDetector == null)
                camDetector = gameObject.AddComponent<RealWorldObjectDetector>();

            // 1. Highest Priority: Live AI Camera Detection from http://127.0.0.1:8765/detections
            if (camDetector != null && camDetector.TryGetVisibleObjects(head, maxCount, out List<RealWorldObjectDetector.VisibleObjectItem> items))
            {
                AnnounceVisibleObjects(items, head);
                return;
            }

            // 2. If no objects cached yet, query the detection server on-demand before fallback
            StartCoroutine(IdentifyAllVisibleObjectsCoroutine(head, maxCount, camDetector));
        }

        private IEnumerator IdentifyAllVisibleObjectsCoroutine(Transform head, int maxCount, RealWorldObjectDetector camDetector)
        {
            if (camDetector != null && camDetector.enableDetection)
            {
                string primaryUrl = string.IsNullOrWhiteSpace(camDetector.serverUrl)
                    ? "http://127.0.0.1:8765/detections"
                    : camDetector.serverUrl.Trim();

                float reqTime = Time.realtimeSinceStartup;
                using (UnityWebRequest req = UnityWebRequest.Get(primaryUrl))
                {
                    req.timeout = 3;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        camDetector.IngestRawDetectionJson(req.downloadHandler.text, reqTime);
                    }
                }

                if (head != null && camDetector.TryGetVisibleObjects(head, maxCount, out List<RealWorldObjectDetector.VisibleObjectItem> items))
                {
                    AnnounceVisibleObjects(items, head);
                    yield break;
                }
            }

            if (head != null)
            {
                FallbackSceneVisibleObjectsIdentification(head, maxCount);
            }
        }

        private void FallbackSceneVisibleObjectsIdentification(Transform head, int maxCount)
        {
            TrackableObject[] all = FindObjectsOfType<TrackableObject>();
            List<RealWorldObjectDetector.VisibleObjectItem> sceneItems = new List<RealWorldObjectDetector.VisibleObjectItem>();
            HashSet<string> seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            List<KeyValuePair<TrackableObject, float>> candidates = new List<KeyValuePair<TrackableObject, float>>();
            foreach (var t in all)
            {
                if (t == null || !t.gameObject.activeInHierarchy)
                    continue;
                Vector3 toObj = t.transform.position - head.position;
                float dist = toObj.magnitude;
                if (dist < 0.2f || dist > maxGazeDistance)
                    continue;

                float angle = Vector3.Angle(head.forward, toObj);
                if (angle <= 48f)
                {
                    candidates.Add(new KeyValuePair<TrackableObject, float>(t, angle));
                }
            }

            candidates.Sort((a, b) => a.Value.CompareTo(b.Value));

            foreach (var pair in candidates)
            {
                if (sceneItems.Count >= maxCount)
                    break;
                TrackableObject t = pair.Key;
                string label = RealWorldObjectDetector.ToIndonesianLabel(t.labelName);
                if (seen.Contains(label))
                    continue;

                float dist = Vector3.Distance(head.position, t.transform.position);
                string dir = PriorityAudioManager.GetRelativeBodyDirection(head, t.transform.position);
                sceneItems.Add(new RealWorldObjectDetector.VisibleObjectItem
                {
                    marker = t,
                    label = label,
                    position = t.transform.position,
                    distance = dist,
                    direction = dir,
                    detailedDescription = t.detailedDescription
                });
                seen.Add(label);
            }

            if (sceneItems.Count > 0)
            {
                AnnounceVisibleObjects(sceneItems, head);
            }
            else
            {
                RealWorldObjectDetector det = FindObjectOfType<RealWorldObjectDetector>();
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                if (det == null || !det.isServerConnected)
                {
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        "Kamera AI tidak terhubung",
                        AudioPriority.P3_ObjectIdentity
                    );
                }
                else
                {
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        "Tidak ada benda terdeteksi di depan",
                        AudioPriority.P3_ObjectIdentity
                    );
                }
            }
        }

        private void AnnounceVisibleObjects(List<RealWorldObjectDetector.VisibleObjectItem> items, Transform head)
        {
            if (items == null || items.Count == 0)
                return;

            foreach (var it in items)
            {
                if (it.marker != null)
                {
                    it.marker.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 3.5f);
                }
                else
                {
                    SpawnTemporaryRealWorldHighlightBox(it.position, TrackableObject.HighContrastYellow, 3.5f);
                }
            }

            string shortMsg;
            string detailMsg;

            if (items.Count == 1)
            {
                string d = PriorityAudioManager.FormatDistance(items[0].distance);
                shortMsg = $"Terlihat 1 benda: {items[0].label}, {d}, {items[0].direction}";
                detailMsg = $"{items[0].label}, {d}, {items[0].direction}. {items[0].detailedDescription}";
            }
            else if (items.Count == 2)
            {
                shortMsg = $"Terlihat 2 benda: {items[0].label} {items[0].direction}, dan {items[1].label} {items[1].direction}";
                string d0 = PriorityAudioManager.FormatDistance(items[0].distance);
                string d1 = PriorityAudioManager.FormatDistance(items[1].distance);
                detailMsg = $"Terlihat 2 benda: {items[0].label} {d0} {items[0].direction}, dan {items[1].label} {d1} {items[1].direction}";
            }
            else
            {
                shortMsg = $"Terlihat 3 benda: {items[0].label} {items[0].direction}, {items[1].label} {items[1].direction}, dan {items[2].label} {items[2].direction}";
                string d0 = PriorityAudioManager.FormatDistance(items[0].distance);
                string d1 = PriorityAudioManager.FormatDistance(items[1].distance);
                string d2 = PriorityAudioManager.FormatDistance(items[2].distance);
                detailMsg = $"Terlihat 3 benda: {items[0].label} {d0} {items[0].direction}, {items[1].label} {d1} {items[1].direction}, dan {items[2].label} {d2} {items[2].direction}";
            }

            Vector3 primaryPos = items[0].position;
            PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, primaryPos);
            PriorityAudioManager.Instance.EnqueueSpeech(
                shortMsg,
                AudioPriority.P3_ObjectIdentity,
                primaryPos,
                detailMsg
            );
        }

        /// <summary>
        /// Selects the best target for "What is this?":
        ///   1. Never reports SemanticObjectCategory.Person (so a user's own hand/arm or nearby person never blocks objects).
        ///   2. Prioritizes EverydayObject / custom saved objects first, then HazardObstacle objects (chair, table, door),
        ///      and only falls back to untagged Spatial Mesh surfaces if no TrackableObject is in the gaze cone.
        /// </summary>
        private bool TryFindBestTargetInGaze(
            Ray gazeRay,
            Transform head,
            out RaycastHit bestHit,
            out TrackableObject bestTrackable,
            out Vector3 targetPoint,
            out float targetDistance)
        {
            bestHit = default;
            bestTrackable = null;
            targetPoint = Vector3.zero;
            targetDistance = 0f;

            RaycastHit[] hits = Physics.SphereCastAll(gazeRay, gazeSphereRadius, maxGazeDistance);

            bool foundObject = false;
            int bestObjectPriority = int.MaxValue;
            float bestObjectDist = float.MaxValue;

            bool foundSurface = false;
            RaycastHit closestSurfaceHit = default;
            float closestSurfaceDist = float.MaxValue;

            foreach (RaycastHit h in hits)
            {
                TrackableObject t = h.collider.GetComponentInParent<TrackableObject>();
                if (t != null)
                {
                    // Never identify a person on "What is this?"
                    if (t.category == SemanticObjectCategory.Person)
                        continue;

                    // Priority 0 = EverydayObject or custom saved item; Priority 1 = larger furniture/hazard obstacle
                    int priority = (t.isCustomUserSaved || t.category == SemanticObjectCategory.EverydayObject) ? 0 : 1;
                    float dist = h.distance > 0.01f ? h.distance : Vector3.Distance(head.position, t.transform.position);

                    if (priority < bestObjectPriority || (priority == bestObjectPriority && dist < bestObjectDist))
                    {
                        bestObjectPriority = priority;
                        bestObjectDist = dist;
                        bestHit = h;
                        bestTrackable = t;
                        targetPoint = h.distance > 0.01f ? h.point : t.transform.position;
                        targetDistance = dist;
                        foundObject = true;
                    }
                }
                else if (h.distance > 0.01f && h.distance < closestSurfaceDist)
                {
                    closestSurfaceDist = h.distance;
                    closestSurfaceHit = h;
                    foundSurface = true;
                }
            }

            if (foundObject)
                return true;

            // Fallback: check if any non-Person TrackableObject is within the camera field of view (42-degree cone)
            // (handles PV camera pitch offset on HoloLens 2 & webcam framing when holding objects like a phone)
            TrackableObject[] allObjects = FindObjectsOfType<TrackableObject>();
            float bestAngleScore = float.MaxValue;
            foreach (TrackableObject t in allObjects)
            {
                if (t == null || t.category == SemanticObjectCategory.Person || string.IsNullOrEmpty(t.labelName))
                    continue;

                Vector3 toObj = t.transform.position - head.position;
                float dist = toObj.magnitude;
                if (dist > maxGazeDistance || dist < 0.1f)
                    continue;

                float angle = Vector3.Angle(head.forward, toObj);
                if (angle <= 42f)
                {
                    int priority = (t.isCustomUserSaved || t.category == SemanticObjectCategory.EverydayObject) ? 0 : 1;
                    float score = priority * 100f + angle;
                    if (score < bestAngleScore)
                    {
                        bestAngleScore = score;
                        bestTrackable = t;
                        targetPoint = t.transform.position;
                        targetDistance = dist;
                        foundObject = true;
                    }
                }
            }

            if (foundObject)
                return true;

            // Direct Camera AI Fallback: if http://127.0.0.1:8765 is actively detecting an object in the camera frame,
            // return the most central detected object directly (handles webcam testing or head-pose stream lag)
            RealWorldObjectDetector camDetector = FindObjectOfType<RealWorldObjectDetector>();
            if (camDetector != null && camDetector.isServerConnected)
            {
                TrackableObject camObj = camDetector.GetMostCentralActiveDetection(10.0f);
                if (camObj != null)
                {
                    bestTrackable = camObj;
                    targetPoint = camObj.transform.position;
                    targetDistance = Mathf.Clamp(Vector3.Distance(head.position, targetPoint), 0.5f, maxGazeDistance);
                    return true;
                }
            }

            if (foundSurface)
            {
                bestHit = closestSurfaceHit;
                bestTrackable = null;
                targetPoint = closestSurfaceHit.point;
                targetDistance = closestSurfaceHit.distance;
                return true;
            }

            return false;
        }

        public bool HasActiveCameraObjectInView()
        {
            RealWorldObjectDetector camDetector = FindObjectOfType<RealWorldObjectDetector>();
            return camDetector != null && camDetector.isServerConnected && camDetector.GetMostCentralActiveDetection(10.0f) != null;
        }

        /// <summary>
        /// KF02: Captures/saves the real-world object in front of the user with a custom voice label
        /// and anchors a 3D wireframe bounding box at its real-world 3D position.
        /// Triggered when the user says "Simpan [label]" / "Save object [label]".
        /// </summary>
        public void SaveCustomObjectInGaze(string customLabel)
        {
            if (Camera.main == null || PriorityAudioManager.Instance == null)
                return;

            Transform head = Camera.main.transform;
            Ray gazeRay = new Ray(head.position, head.forward);
            string finalLabel = string.IsNullOrWhiteSpace(customLabel)
                ? "Benda tersimpan"
                : RealWorldObjectDetector.ToIndonesianLabel(customLabel.Trim());

            if (Physics.SphereCast(gazeRay, gazeSphereRadius, out RaycastHit hit, maxGazeDistance))
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();
                // Relabel simulated objects, but never overwrite another real-room tag or a camera-detected object
                if (trackable != null && !IsRealWorldAnchor(trackable) && !RealWorldObjectDetector.IsDetectionMarker(trackable))
                {
                    trackable.labelName = finalLabel;
                    trackable.detailedDescription = $"Benda pribadi disimpan dengan nama {finalLabel}";
                    trackable.isCustomUserSaved = true;
                    trackable.TriggerVisualHighlight(TrackableObject.HighContrastGreen, 4.0f);

                    PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hit.point);
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        $"Tersimpan, {trackable.labelName}",
                        AudioPriority.P3_ObjectIdentity,
                        hit.point
                    );
                    return;
                }

                // Hit real-world Spatial Mesh: spawn a real-world 3D bounding box anchor at hit.point!
                TrackableObject anchored = CreateRealWorldSpatialAnchor(hit.point, finalLabel);
                anchored.TriggerVisualHighlight(TrackableObject.HighContrastGreen, 4.0f);

                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hit.point);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"Tersimpan, {anchored.labelName}",
                    AudioPriority.P3_ObjectIdentity,
                    hit.point
                );
            }
            else
            {
                // Even if Spatial Mesh hasn't meshed a small tabletop object yet, anchor at 1.2m along real head gaze!
                Vector3 realWorldAnchorPos = head.position + head.forward * 1.2f;
                TrackableObject anchored = CreateRealWorldSpatialAnchor(realWorldAnchorPos, finalLabel);
                anchored.TriggerVisualHighlight(TrackableObject.HighContrastGreen, 4.0f);

                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, realWorldAnchorPos);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"Tersimpan, {anchored.labelName}, 1 meter di depan",
                    AudioPriority.P3_ObjectIdentity,
                    realWorldAnchorPos
                );
            }
        }

        private const string RealWorldAnchorPrefix = "RealWorldAnchor_";

        private static bool IsRealWorldAnchor(TrackableObject t)
        {
            return t != null && t.gameObject.name.StartsWith(RealWorldAnchorPrefix);
        }

        /// <summary>
        /// Labels of every real-room tag the user has saved (persisted across app restarts).
        /// </summary>
        public List<string> GetSavedLabels()
        {
            List<string> labels = new List<string>();
            foreach (TrackableObject t in FindObjectsOfType<TrackableObject>())
            {
                if (IsRealWorldAnchor(t) && !labels.Contains(t.labelName))
                    labels.Add(t.labelName);
            }
            return labels;
        }

        public void ClearSavedAnchors()
        {
            foreach (TrackableObject existing in FindObjectsOfType<TrackableObject>())
            {
                if (IsRealWorldAnchor(existing))
                {
                    Destroy(existing.gameObject);
                }
            }

            try
            {
                string path = SavedAnchorsFilePath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Debug.Log($"[ObjectRecognizer] Cleared persistent anchors file: {path}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ObjectRecognizer] Failed to delete saved anchors: {ex.Message}");
            }
        }

        private void SaveAnchorsToDisk()
        {
            try
            {
                SavedSpatialAnchorCollection collection = new SavedSpatialAnchorCollection();
                foreach (TrackableObject t in FindObjectsOfType<TrackableObject>())
                {
                    if (IsRealWorldAnchor(t))
                    {
                        Vector3 pos = t.transform.position;
                        collection.anchors.Add(new SavedSpatialAnchorData
                        {
                            label = t.labelName,
                            x = pos.x,
                            y = pos.y,
                            z = pos.z
                        });
                    }
                }

                string json = JsonUtility.ToJson(collection, true);
                File.WriteAllText(SavedAnchorsFilePath, json);
                Debug.Log($"[ObjectRecognizer] Persisted {collection.anchors.Count} anchor(s) to {SavedAnchorsFilePath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ObjectRecognizer] Failed to persist anchors: {ex.Message}");
            }
        }

        public void LoadSavedAnchorsFromDisk()
        {
            try
            {
                string path = SavedAnchorsFilePath;
                if (!File.Exists(path))
                    return;

                string json = File.ReadAllText(path);
                SavedSpatialAnchorCollection collection = JsonUtility.FromJson<SavedSpatialAnchorCollection>(json);
                if (collection == null || collection.anchors == null)
                    return;

                int loaded = 0;
                foreach (SavedSpatialAnchorData data in collection.anchors)
                {
                    if (string.IsNullOrWhiteSpace(data.label))
                        continue;

                    Vector3 pos = new Vector3(data.x, data.y, data.z);
                    CreateRealWorldSpatialAnchor(pos, data.label, persist: false);
                    loaded++;
                }

                Debug.Log($"[ObjectRecognizer] Restored {loaded} persistent spatial anchor(s) from disk");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ObjectRecognizer] Failed to load saved anchors: {ex.Message}");
            }
        }

        private TrackableObject CreateRealWorldSpatialAnchor(Vector3 worldPos, string label, bool persist = true)
        {
            string normalizedLabel = RealWorldObjectDetector.ToIndonesianLabel(label);

            // Saving the same label again moves the tag instead of creating a duplicate
            foreach (TrackableObject existing in FindObjectsOfType<TrackableObject>())
            {
                if (IsRealWorldAnchor(existing) &&
                    string.Equals(RealWorldObjectDetector.ToIndonesianLabel(existing.labelName), normalizedLabel, System.StringComparison.OrdinalIgnoreCase))
                {
                    Destroy(existing.gameObject);
                }
            }

            GameObject anchorObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            anchorObj.name = RealWorldAnchorPrefix + normalizedLabel;
            anchorObj.transform.position = worldPos;
            anchorObj.transform.localScale = new Vector3(0.22f, 0.22f, 0.22f);

            // Make the inner cube nearly transparent so only the thick 3D wireframe bounding box shows in HoloLens
            Renderer r = anchorObj.GetComponent<Renderer>();
            if (r != null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    Material m = new Material(shader);
                    m.color = new Color(0.15f, 1f, 0.35f, 0.15f);
                    r.sharedMaterial = m;
                }
            }

            TrackableObject trackable = anchorObj.AddComponent<TrackableObject>();
            trackable.labelName = normalizedLabel;
            trackable.detailedDescription = $"Objek ruangan tersimpan ({normalizedLabel})";
            trackable.category = SemanticObjectCategory.EverydayObject;
            trackable.isCustomUserSaved = true;

            if (persist)
            {
                SaveAnchorsToDisk();
            }

            return trackable;
        }

        public static void SpawnTemporaryRealWorldHighlightBox(Vector3 worldPos, Color color, float duration)
        {
            GameObject boxObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            boxObj.name = "RealWorldHighlightMarker";
            Destroy(boxObj.GetComponent<Collider>());
            boxObj.transform.position = worldPos;
            boxObj.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);

            Renderer r = boxObj.GetComponent<Renderer>();
            if (r != null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    Material m = new Material(shader);
                    m.color = new Color(color.r, color.g, color.b, 0.18f);
                    r.sharedMaterial = m;
                }
            }

            TrackableObject t = boxObj.AddComponent<TrackableObject>();
            // An empty label keeps this visual-only box out of "Ini apa" and "Cari"
            t.labelName = "";
            t.detailedDescription = "";
            t.TriggerVisualHighlight(color, duration);
            Destroy(boxObj, duration + 0.1f);
        }

        /// <summary>
        /// Finds an object by name (supports both Bahasa Indonesia and English / phonetic queries).
        /// </summary>
        public TrackableObject FindObjectByName(string queryName)
        {
            if (string.IsNullOrWhiteSpace(queryName))
                return null;

            string qRaw = queryName.Trim().ToLowerInvariant();
            string qIndo = RealWorldObjectDetector.ToIndonesianLabel(qRaw).ToLowerInvariant();
            TrackableObject[] allObjects = FindObjectsOfType<TrackableObject>();

            TrackableObject bestMatch = null;
            float closestDist = float.MaxValue;
            Vector3 userPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;

            foreach (var obj in allObjects)
            {
                if (obj == null || string.IsNullOrEmpty(obj.labelName))
                    continue;

                string objRaw = obj.labelName.ToLowerInvariant();
                string objIndo = RealWorldObjectDetector.ToIndonesianLabel(obj.labelName).ToLowerInvariant();

                bool isCupGlassSynonym =
                    ((qIndo == "gelas" || qIndo == "cangkir") && (objIndo == "gelas" || objIndo == "cangkir"));

                bool match = isCupGlassSynonym ||
                             objRaw.Contains(qRaw) || qRaw.Contains(objRaw) ||
                             objIndo.Contains(qIndo) || qIndo.Contains(objIndo);

                if (match)
                {
                    float d = Vector3.Distance(userPos, obj.transform.position);
                    if (d < closestDist)
                    {
                        closestDist = d;
                        bestMatch = obj;
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// KF08: Detects people approaching the user (Audio Priority P2 Social Info).
        /// Does not store facial identity to preserve privacy per T3 requirements.
        /// </summary>
        private void MonitorApproachingPeople()
        {
            if (PriorityAudioManager.Instance == null)
                return;

            Camera activeCam = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
            if (activeCam == null)
                return;

            Transform head = activeCam.transform;
            TrackableObject[] allObjects = FindObjectsOfType<TrackableObject>();
            float effectiveRadius = Mathf.Max(personDetectionRadius, 3.5f);

            foreach (var obj in allObjects)
            {
                if (obj == null || obj.category != SemanticObjectCategory.Person)
                    continue;

                float lastSample = _lastPersonSampleTime.TryGetValue(obj, out float ls) ? ls : -999f;
                if (Time.time - lastSample < 0.25f)
                    continue;
                _lastPersonSampleTime[obj] = Time.time;

                float currentDist = Vector3.Distance(head.position, obj.transform.position);
                if (currentDist < 0.45f)
                    continue;

                if (!_firstPersonSeenTime.TryGetValue(obj, out float firstSeen))
                {
                    firstSeen = Time.time;
                    _firstPersonSeenTime[obj] = firstSeen;
                }

                if (_previousPersonDistance.TryGetValue(obj, out float peakDist))
                {
                    bool isCameraPerson = RealWorldObjectDetector.IsDetectionMarker(obj);
                    bool approachedFromPeak = (peakDist - currentDist) >= 0.08f;
                    bool enteredCloseZone = isCameraPerson && currentDist <= 3.2f && !_lastPersonAlertTime.ContainsKey(obj);

                    if (currentDist <= effectiveRadius && (approachedFromPeak || enteredCloseZone))
                    {
                        float lastAlert = _lastPersonAlertTime.TryGetValue(obj, out float t) ? t : -999f;
                        if (Time.time - lastAlert >= personAlertCooldown && Time.time - _lastPersonSpeechTime >= personAlertCooldown)
                        {
                            _lastPersonAlertTime[obj] = Time.time;
                            _previousPersonDistance[obj] = currentDist;
                            string dir = PriorityAudioManager.GetRelativeBodyDirection(head, obj.transform.position);
                            obj.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 2.5f);

                            PriorityAudioManager.Instance.EnqueueSpeech(
                                $"Orang mendekat, {dir}",
                                AudioPriority.P2_SocialInfo,
                                obj.transform.position
                            );
                            continue;
                        }
                    }

                    // Keep the peak distance since last alert so gradual walking speeds accumulate properly
                    if (currentDist > peakDist)
                        _previousPersonDistance[obj] = currentDist;
                }
                else
                {
                    _previousPersonDistance[obj] = currentDist;
                }
            }
        }
    }
}
