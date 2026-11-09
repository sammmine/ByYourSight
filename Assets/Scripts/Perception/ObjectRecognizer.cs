using System.Collections.Generic;
using System.IO;
using UnityEngine;
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
            LoadSavedAnchorsFromDisk();
        }

        private void Update()
        {
            MonitorApproachingPeople();
        }

        /// <summary>
        /// KF01: Triggered when the user says "What is this?"
        /// T3 output pattern: [What] + [Distance] + [Direction], e.g., "Water bottle, 1.5 meters, ahead"
        /// </summary>
        public void IdentifyObjectInGaze()
        {
            if (Camera.main == null || PriorityAudioManager.Instance == null)
                return;

            if (simulateLowLightWarning)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Not sure, low light. Move closer to the object",
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            Transform head = Camera.main.transform;
            Ray gazeRay = new Ray(head.position, head.forward);

            if (TryFindBestTargetInGaze(gazeRay, head, out RaycastHit hit, out TrackableObject trackable, out Vector3 targetPoint, out float rawDistance))
            {
                string objName = trackable != null ? trackable.labelName : SurfaceClassifier.Describe(hit, head);
                float dist = Mathf.Max(0.5f, Mathf.Round(rawDistance * 2f) / 2f); // Round to nearest 0.5m
                string distText = Mathf.Approximately(dist, 1f)
                    ? "1 meter"
                    : (dist % 1f == 0f ? $"{(int)dist} meters" : $"{dist:0.0} meters");
                string dirText = PriorityAudioManager.GetRelativeBodyDirection(head, targetPoint);

                string shortMsg = $"{objName}, {distText}, {dirText}";
                string detailMsg = trackable != null && !string.IsNullOrEmpty(trackable.detailedDescription)
                    ? $"{objName}, {distText} {dirText}. {trackable.detailedDescription}"
                    : $"{objName} detected at {distText} {dirText}";

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
            else
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "No object detected ahead",
                    AudioPriority.P3_ObjectIdentity
                );
            }
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

            // Fallback: check if any non-Person TrackableObject is within a 20-degree cone of head gaze
            // (handles slight PV camera pitch offset on HoloLens 2 when holding small objects in front of the visor)
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
                if (angle <= 20f)
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

        /// <summary>
        /// KF02: Captures/saves the real-world object in front of the user with a custom voice label
        /// and anchors a 3D wireframe bounding box at its real-world 3D position.
        /// Triggered when the user says "Save object [label]".
        /// </summary>
        public void SaveCustomObjectInGaze(string customLabel)
        {
            if (Camera.main == null || PriorityAudioManager.Instance == null)
                return;

            Transform head = Camera.main.transform;
            Ray gazeRay = new Ray(head.position, head.forward);
            string finalLabel = string.IsNullOrWhiteSpace(customLabel) ? "Saved Item" : customLabel.Trim();

            if (Physics.SphereCast(gazeRay, gazeSphereRadius, out RaycastHit hit, maxGazeDistance))
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();
                // Relabel simulated objects, but never overwrite another real-room tag or a camera-detected object
                if (trackable != null && !IsRealWorldAnchor(trackable) && !RealWorldObjectDetector.IsDetectionMarker(trackable))
                {
                    trackable.labelName = finalLabel;
                    trackable.detailedDescription = $"Personal item saved by user with label {finalLabel}";
                    trackable.isCustomUserSaved = true;
                    trackable.TriggerVisualHighlight(TrackableObject.HighContrastGreen, 4.0f);

                    PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hit.point);
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        $"Saved, {trackable.labelName}",
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
                    $"Saved, {anchored.labelName}",
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
                    $"Saved, {anchored.labelName}, 1 meter ahead",
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
            // Saving the same label again moves the tag instead of creating a duplicate
            foreach (TrackableObject existing in FindObjectsOfType<TrackableObject>())
            {
                if (IsRealWorldAnchor(existing) &&
                    string.Equals(existing.labelName, label, System.StringComparison.OrdinalIgnoreCase))
                {
                    Destroy(existing.gameObject);
                }
            }

            GameObject anchorObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            anchorObj.name = RealWorldAnchorPrefix + label;
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
            trackable.labelName = label;
            trackable.detailedDescription = $"Real-world anchored object ({label})";
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
            t.TriggerVisualHighlight(color, duration);
            Destroy(boxObj, duration + 0.1f);
        }

        /// <summary>
        /// Finds an object by name (to pass to KF03 Spatial Beacon).
        /// </summary>
        public TrackableObject FindObjectByName(string queryName)
        {
            if (string.IsNullOrWhiteSpace(queryName))
                return null;

            string q = queryName.Trim().ToLowerInvariant();
            TrackableObject[] allObjects = FindObjectsOfType<TrackableObject>();

            TrackableObject bestMatch = null;
            float closestDist = float.MaxValue;
            Vector3 userPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;

            foreach (var obj in allObjects)
            {
                if (obj.labelName.ToLowerInvariant().Contains(q))
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
                                $"Person approaching, {dir}",
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
