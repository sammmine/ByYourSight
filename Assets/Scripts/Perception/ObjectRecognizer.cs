using System.Collections.Generic;
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

        private void Awake()
        {
            if (GetComponent<ComputerVisionDetector>() == null)
            {
                gameObject.AddComponent<ComputerVisionDetector>();
            }
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

            ComputerVisionDetector cv = ComputerVisionDetector.Instance;
            if (cv == null)
            {
                cv = GetComponent<ComputerVisionDetector>();
                if (cv == null)
                    cv = gameObject.AddComponent<ComputerVisionDetector>();
            }

            if (simulateLowLightWarning || (cv != null && cv.isLowLightDetected))
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Not sure, low light. Move closer to the object",
                    AudioPriority.P3_ObjectIdentity
                );
                return;
            }

            Transform head = Camera.main.transform;

            // 1. Check if the user is looking near an already-saved custom real-world anchor (within 24° gaze cone)
            TrackableObject savedInCone = FindSavedAnchorInGazeCone(head, 24f, maxGazeDistance);
            if (savedInCone != null)
            {
                AnnounceTrackableObject(head, savedInCone, savedInCone.transform.position, Vector3.Distance(head.position, savedInCone.transform.position));
                return;
            }

            // 2. Raycast / Multi-Ray Gaze Cone against Scene & HoloLens Spatial Mesh
            bool has3DHit = cv != null
                ? cv.TryFindBestGazeHit(head, gazeSphereRadius, maxGazeDistance, out RaycastHit hit)
                : Physics.SphereCast(new Ray(head.position, head.forward), gazeSphereRadius, out hit, maxGazeDistance);

            if (has3DHit)
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();
                if (trackable != null)
                {
                    AnnounceTrackableObject(head, trackable, hit.point, hit.distance);
                    return;
                }

                // 3. Unrecognized real-world object on Spatial Mesh -> Run Computer Vision & 3D Point-Cloud Classifier
                CVDetectionResult cvResult = cv.IdentifyUnrecognizedObjectAtGaze(head, true, hit, maxGazeDistance);
                string objName = cvResult.isDetected && !string.IsNullOrEmpty(cvResult.label)
                    ? cvResult.label
                    : "Object";

                float dist = Mathf.Max(0.5f, Mathf.Round(hit.distance * 2f) / 2f); // Round to nearest 0.5m
                string distText = FormatDistanceMeters(dist);
                string dirText = PriorityAudioManager.GetRelativeBodyDirection(head, hit.point);

                string shortMsg = $"{objName}, {distText}, {dirText}";
                string detailMsg = cvResult.isDetected && !string.IsNullOrEmpty(cvResult.detailedDescription)
                    ? $"{objName}, {distText} {dirText}. {cvResult.detailedDescription}"
                    : $"{objName} detected at {distText} {dirText}";

                Vector3 highlightScale = cvResult.isDetected ? cvResult.estimatedBounds : new Vector3(0.25f, 0.25f, 0.25f);
                SpawnTemporaryRealWorldHighlightBox(hit.point, TrackableObject.HighContrastYellow, 3.0f, highlightScale);
                CacheAutoDetectedRealWorldObject(hit.point, objName, detailMsg, cvResult.category, highlightScale);

                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hit.point);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    shortMsg,
                    AudioPriority.P3_ObjectIdentity,
                    hit.point,
                    detailMsg
                );
                return;
            }

            // 4. Fallback: No 3D Spatial Mesh collider hit (e.g., small handheld item within 0.4m-1.2m camera view)
            if (cv != null)
            {
                CVDetectionResult camOnly = cv.IdentifyUnrecognizedObjectAtGaze(head, false, default, maxGazeDistance);
                if (camOnly.isDetected && !string.IsNullOrEmpty(camOnly.label))
                {
                    float rawDist = Vector3.Distance(head.position, camOnly.worldPosition);
                    float dist = Mathf.Max(0.5f, Mathf.Round(rawDist * 2f) / 2f);
                    string distText = FormatDistanceMeters(dist);
                    string dirText = PriorityAudioManager.GetRelativeBodyDirection(head, camOnly.worldPosition);

                    string shortMsg = $"{camOnly.label}, {distText}, {dirText}";
                    string detailMsg = $"{camOnly.label}, {distText} {dirText}. {camOnly.detailedDescription}";

                    SpawnTemporaryRealWorldHighlightBox(camOnly.worldPosition, TrackableObject.HighContrastYellow, 3.0f, camOnly.estimatedBounds);
                    CacheAutoDetectedRealWorldObject(camOnly.worldPosition, camOnly.label, detailMsg, camOnly.category, camOnly.estimatedBounds);

                    PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, camOnly.worldPosition);
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        shortMsg,
                        AudioPriority.P3_ObjectIdentity,
                        camOnly.worldPosition,
                        detailMsg
                    );
                    return;
                }
            }

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
            PriorityAudioManager.Instance.EnqueueSpeech(
                "No object detected ahead",
                AudioPriority.P3_ObjectIdentity
            );
        }

        private void AnnounceTrackableObject(Transform head, TrackableObject trackable, Vector3 hitPoint, float rawDistance)
        {
            string objName = trackable.labelName;
            float dist = Mathf.Max(0.5f, Mathf.Round(rawDistance * 2f) / 2f);
            string distText = FormatDistanceMeters(dist);
            string dirText = PriorityAudioManager.GetRelativeBodyDirection(head, hitPoint);

            string shortMsg = $"{objName}, {distText}, {dirText}";
            string detailMsg = !string.IsNullOrEmpty(trackable.detailedDescription)
                ? $"{objName}, {distText} {dirText}. {trackable.detailedDescription}"
                : $"{objName} detected at {distText} {dirText}";

            trackable.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 3.0f);

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hitPoint);
            PriorityAudioManager.Instance.EnqueueSpeech(
                shortMsg,
                AudioPriority.P3_ObjectIdentity,
                hitPoint,
                detailMsg
            );
        }

        private static string FormatDistanceMeters(float dist)
        {
            return Mathf.Approximately(dist, 1f)
                ? "1 meter"
                : (dist % 1f == 0f ? $"{(int)dist} meters" : $"{dist:0.0} meters");
        }

        private TrackableObject FindSavedAnchorInGazeCone(Transform head, float maxAngleDeg, float maxDist)
        {
            TrackableObject best = null;
            float bestAngle = maxAngleDeg;

            foreach (TrackableObject t in FindObjectsOfType<TrackableObject>())
            {
                if (!IsRealWorldAnchor(t))
                    continue;

                Vector3 toObj = t.transform.position - head.position;
                float d = toObj.magnitude;
                if (d > maxDist || d < 0.1f)
                    continue;

                float angle = Vector3.Angle(head.forward, toObj);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    best = t;
                }
            }

            return best;
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
            ComputerVisionDetector cv = ComputerVisionDetector.Instance;
            bool hasHit = cv != null
                ? cv.TryFindBestGazeHit(head, gazeSphereRadius, maxGazeDistance, out RaycastHit hit)
                : Physics.SphereCast(new Ray(head.position, head.forward), gazeSphereRadius, out hit, maxGazeDistance);

            string finalLabel = string.IsNullOrWhiteSpace(customLabel) ? "Saved Item" : customLabel.Trim();

            // If user said generic "Save object", infer the label via Computer Vision when possible
            if (string.Equals(finalLabel, "Saved Item", System.StringComparison.OrdinalIgnoreCase) && cv != null)
            {
                CVDetectionResult cvGuess = cv.IdentifyUnrecognizedObjectAtGaze(head, hasHit, hit, maxGazeDistance);
                if (cvGuess.isDetected && !string.IsNullOrEmpty(cvGuess.label))
                {
                    finalLabel = cvGuess.label;
                }
            }

            if (hasHit)
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();
                // Relabel simulated objects, but never overwrite another real-room tag the user is looking at
                if (trackable != null && !IsRealWorldAnchor(trackable))
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
        private const string AutoDetectedAnchorPrefix = "RealWorldAutoCV_";

        private static bool IsRealWorldAnchor(TrackableObject t)
        {
            return t != null && t.gameObject.name.StartsWith(RealWorldAnchorPrefix);
        }

        /// <summary>
        /// Caches an object automatically recognized by Computer Vision so the user can immediately
        /// say "Find [object]" (KF03) or "Navigate to [object]" (KF05) without having to manually save it first.
        /// </summary>
        private void CacheAutoDetectedRealWorldObject(
            Vector3 worldPos,
            string label,
            string detail,
            SemanticObjectCategory category,
            Vector3 bounds)
        {
            if (string.IsNullOrWhiteSpace(label) || string.Equals(label, "Wall", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "Floor", System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Do not overwrite user-saved custom anchors with the same name
            foreach (TrackableObject existing in FindObjectsOfType<TrackableObject>())
            {
                if (IsRealWorldAnchor(existing) &&
                    string.Equals(existing.labelName, label, System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (existing.gameObject.name.StartsWith(AutoDetectedAnchorPrefix) &&
                    string.Equals(existing.labelName, label, System.StringComparison.OrdinalIgnoreCase))
                {
                    existing.transform.position = worldPos;
                    existing.detailedDescription = detail;
                    return;
                }
            }

            GameObject autoObj = new GameObject(AutoDetectedAnchorPrefix + label);
            autoObj.transform.position = worldPos;
            autoObj.transform.localScale = bounds;

            TrackableObject t = autoObj.AddComponent<TrackableObject>();
            t.labelName = label;
            t.detailedDescription = detail;
            t.category = category;
            t.isCustomUserSaved = false;
        }

        /// <summary>
        /// Labels of every real-room tag the user has saved this session ("List saved").
        /// </summary>
        public List<string> GetSavedLabels()
        {
            List<string> labels = new List<string>();
            foreach (TrackableObject t in FindObjectsOfType<TrackableObject>())
            {
                if ((IsRealWorldAnchor(t) || t.gameObject.name.StartsWith(AutoDetectedAnchorPrefix)) &&
                    !labels.Contains(t.labelName))
                {
                    labels.Add(t.labelName);
                }
            }
            return labels;
        }

        private TrackableObject CreateRealWorldSpatialAnchor(Vector3 worldPos, string label)
        {
            // Saving the same label again moves the tag instead of creating a duplicate
            foreach (TrackableObject existing in FindObjectsOfType<TrackableObject>())
            {
                if ((IsRealWorldAnchor(existing) || existing.gameObject.name.StartsWith(AutoDetectedAnchorPrefix)) &&
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
            return trackable;
        }

        public static void SpawnTemporaryRealWorldHighlightBox(Vector3 worldPos, Color color, float duration)
        {
            SpawnTemporaryRealWorldHighlightBox(worldPos, color, duration, new Vector3(0.25f, 0.25f, 0.25f));
        }

        public static void SpawnTemporaryRealWorldHighlightBox(Vector3 worldPos, Color color, float duration, Vector3 scale)
        {
            GameObject boxObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            boxObj.name = "RealWorldHighlightMarker";
            Destroy(boxObj.GetComponent<Collider>());
            boxObj.transform.position = worldPos;
            boxObj.transform.localScale = scale;

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

                float currentDist = Vector3.Distance(head.position, obj.transform.position);
                if (_previousPersonDistance.TryGetValue(obj, out float prevDist))
                {
                    bool isGettingCloser = (prevDist - currentDist) > 0.015f;
                    if (currentDist >= minPersonDetectionDistance && currentDist <= personDetectionRadius && isGettingCloser)
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
