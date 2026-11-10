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

        private readonly Dictionary<TrackableObject, float> _lastPersonAlertTime = new Dictionary<TrackableObject, float>();
        private readonly Dictionary<TrackableObject, float> _previousPersonDistance = new Dictionary<TrackableObject, float>();
        private readonly Dictionary<TrackableObject, float> _lastPersonSampleTime = new Dictionary<TrackableObject, float>();

        private void Start()
        {
            // Clean up any legacy saved anchors file from disk on HoloLens 2 / PC storage
            try
            {
                string legacyPath = Path.Combine(Application.persistentDataPath, "saved_spatial_anchors.json");
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                    Debug.Log($"[ObjectRecognizer] Purged legacy saved spatial anchors file: {legacyPath}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ObjectRecognizer] Could not delete legacy anchor file: {ex.Message}");
            }
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
                    req.timeout = 1;
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
                    string surfaceDesc = SurfaceClassifier.Describe(hit, head);
                    // On "Ini apa?", a horizontal surface at waist/desk height is a table/desk ("Meja"), not "Rintangan rendah"
                    objName = surfaceDesc == SurfaceClassifier.LowObstacle ? "Meja" : surfaceDesc;
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

                    // Priority 0 = EverydayObject; Priority 1 = larger furniture/hazard obstacle
                    int priority = (t.category == SemanticObjectCategory.EverydayObject) ? 0 : 1;
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
                if (t == null || t.category == SemanticObjectCategory.Person)
                    continue;

                Vector3 toObj = t.transform.position - head.position;
                float dist = toObj.magnitude;
                if (dist > maxGazeDistance || dist < 0.1f)
                    continue;

                float angle = Vector3.Angle(head.forward, toObj);
                if (angle <= 42f)
                {
                    int priority = (t.category == SemanticObjectCategory.EverydayObject) ? 0 : 1;
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

                bool match = objRaw.Contains(qRaw) || qRaw.Contains(objRaw) ||
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
            if (Camera.main == null || PriorityAudioManager.Instance == null)
                return;

            Transform head = Camera.main.transform;
            TrackableObject[] allObjects = FindObjectsOfType<TrackableObject>();

            foreach (var obj in allObjects)
            {
                if (obj.category != SemanticObjectCategory.Person)
                    continue;

                // Sample every 0.5 s: camera-estimated distances jitter, so require a real approach (~0.4 m/s)
                float lastSample = _lastPersonSampleTime.TryGetValue(obj, out float ls) ? ls : -999f;
                if (Time.time - lastSample < 0.5f)
                    continue;
                _lastPersonSampleTime[obj] = Time.time;

                float currentDist = Vector3.Distance(head.position, obj.transform.position);
                if (_previousPersonDistance.TryGetValue(obj, out float prevDist))
                {
                    bool isGettingCloser = (prevDist - currentDist) > 0.2f;
                    if (currentDist <= personDetectionRadius && isGettingCloser)
                    {
                        float lastAlert = _lastPersonAlertTime.TryGetValue(obj, out float t) ? t : -999f;
                        if (Time.time - lastAlert >= personAlertCooldown)
                        {
                            _lastPersonAlertTime[obj] = Time.time;
                            string dir = PriorityAudioManager.GetRelativeBodyDirection(head, obj.transform.position);
                            obj.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 2.5f);

                            PriorityAudioManager.Instance.EnqueueSpeech(
                                $"Orang mendekat, {dir}",
                                AudioPriority.P2_SocialInfo,
                                obj.transform.position
                            );
                        }
                    }
                }

                _previousPersonDistance[obj] = currentDist;
            }
        }
    }
}
