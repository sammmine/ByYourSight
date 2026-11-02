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

            TrackableObject detectedTrackable = null;
            Vector3 hitPoint = Vector3.zero;
            float hitDistance = 0f;
            bool hasHit = false;

            // 1. Primary SphereCast along head gaze
            if (Physics.SphereCast(gazeRay, gazeSphereRadius, out RaycastHit hit, maxGazeDistance))
            {
                detectedTrackable = hit.collider.GetComponentInParent<TrackableObject>();
                hitPoint = hit.point;
                hitDistance = hit.distance;
                hasHit = true;
            }

            // 2. Fallback Wide Gaze Cone (28 degrees) so HoloLens users don't miss objects due to slight head pitch differences
            if (detectedTrackable == null)
            {
                TrackableObject coneMatch = FindBestObjectInGazeCone(head, maxGazeDistance, 28f);
                if (coneMatch != null)
                {
                    detectedTrackable = coneMatch;
                    hitPoint = coneMatch.transform.position;
                    hitDistance = Vector3.Distance(head.position, hitPoint);
                    hasHit = true;
                }
            }

            if (hasHit)
            {
                string objName = detectedTrackable != null ? detectedTrackable.labelName : "Object surface";
                float dist = Mathf.Max(0.5f, Mathf.Round(hitDistance * 2f) / 2f); // Round to nearest 0.5m
                string distText = Mathf.Approximately(dist, 1f)
                    ? "1 meter"
                    : (dist % 1f == 0f ? $"{(int)dist} meters" : $"{dist:0.0} meters");
                string dirText = PriorityAudioManager.GetRelativeBodyDirection(head, hitPoint);

                string shortMsg = $"{objName}, {distText}, {dirText}";
                string detailMsg = detectedTrackable != null && !string.IsNullOrEmpty(detectedTrackable.detailedDescription)
                    ? $"{objName}, {distText} {dirText}. {detectedTrackable.detailedDescription}"
                    : $"{objName} detected at {distText} {dirText}";

                if (detectedTrackable != null)
                {
                    detectedTrackable.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 3.5f);
                }
                else
                {
                    SpawnTemporaryRealWorldHighlightBox(hitPoint, TrackableObject.HighContrastYellow, 3.5f);
                }

                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hitPoint);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    shortMsg,
                    AudioPriority.P3_ObjectIdentity,
                    hitPoint,
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

        private TrackableObject FindBestObjectInGazeCone(Transform head, float maxDist, float maxAngleDegrees)
        {
            TrackableObject[] allObjects = FindObjectsOfType<TrackableObject>();
            TrackableObject best = null;
            float bestAngle = maxAngleDegrees;

            foreach (var obj in allObjects)
            {
                if (obj == null || !obj.gameObject.activeInHierarchy)
                    continue;

                Vector3 toObj = obj.transform.position - head.position;
                float dist = toObj.magnitude;
                if (dist > maxDist || dist < 0.2f)
                    continue;

                float angle = Vector3.Angle(head.forward, toObj.normalized);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    best = obj;
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
            Ray gazeRay = new Ray(head.position, head.forward);
            string finalLabel = string.IsNullOrWhiteSpace(customLabel) ? "Saved Item" : customLabel;

            if (Physics.SphereCast(gazeRay, gazeSphereRadius, out RaycastHit hit, maxGazeDistance))
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();
                if (trackable != null)
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

        private TrackableObject CreateRealWorldSpatialAnchor(Vector3 worldPos, string label)
        {
            GameObject anchorObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            anchorObj.name = $"RealWorldAnchor_{label}";
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

                float currentDist = Vector3.Distance(head.position, obj.transform.position);
                if (_previousPersonDistance.TryGetValue(obj, out float prevDist))
                {
                    bool isGettingCloser = (prevDist - currentDist) > 0.015f;
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
