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
        public float personDetectionRadius = 4.0f;
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

            if (Physics.SphereCast(gazeRay, gazeSphereRadius, out RaycastHit hit, maxGazeDistance))
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();
                string objName = trackable != null ? trackable.labelName : hit.collider.gameObject.name;
                float dist = Mathf.Max(0.5f, Mathf.Round(hit.distance * 2f) / 2f); // Round to nearest 0.5m
                string distText = Mathf.Approximately(dist, 1f)
                    ? "1 meter"
                    : (dist % 1f == 0f ? $"{(int)dist} meters" : $"{dist:0.0} meters");
                string dirText = PriorityAudioManager.GetRelativeBodyDirection(head, hit.point);

                string shortMsg = $"{objName}, {distText}, {dirText}";
                string detailMsg = trackable != null && !string.IsNullOrEmpty(trackable.detailedDescription)
                    ? $"{objName}, {distText} {dirText}. {trackable.detailedDescription}"
                    : shortMsg;

                if (trackable != null)
                {
                    trackable.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 3.0f);
                }

                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hit.point);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    shortMsg,
                    AudioPriority.P3_ObjectIdentity,
                    hit.point,
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
        /// KF02: Captures/saves the object in front of the user with a custom voice label.
        /// Triggered when the user says "Save object [label]".
        /// </summary>
        public void SaveCustomObjectInGaze(string customLabel)
        {
            if (Camera.main == null || PriorityAudioManager.Instance == null)
                return;

            Transform head = Camera.main.transform;
            Ray gazeRay = new Ray(head.position, head.forward);

            if (Physics.SphereCast(gazeRay, gazeSphereRadius, out RaycastHit hit, maxGazeDistance))
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();
                if (trackable == null)
                {
                    trackable = hit.collider.gameObject.AddComponent<TrackableObject>();
                }

                trackable.labelName = string.IsNullOrWhiteSpace(customLabel) ? "Saved Item" : customLabel;
                trackable.detailedDescription = $"Personal item saved by user with label {trackable.labelName}";
                trackable.isCustomUserSaved = true;
                trackable.TriggerVisualHighlight(TrackableObject.HighContrastGreen, 3.0f);

                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, hit.point);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"Saved, {trackable.labelName}",
                    AudioPriority.P3_ObjectIdentity,
                    hit.point
                );
            }
            else
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    "Point your head at an object first",
                    AudioPriority.P3_ObjectIdentity
                );
            }
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
