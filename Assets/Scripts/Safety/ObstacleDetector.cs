using UnityEngine;
using ByYourSight.Audio;
using ByYourSight.Perception;

namespace ByYourSight.Safety
{
    /// <summary>
    /// KF07 (Critical Priority): Detects obstacles ahead of the user using Raycast/SphereCast
    /// against Spatial Mesh / hazard colliders, triggering a P0 Danger Alarm that interrupts all other audio.
    /// Example T3 output: "Warning! Pillar, 1 meter, ahead"
    /// </summary>
    public class ObstacleDetector : MonoBehaviour
    {
        [Header("Hazard Detection Settings (KF07)")]
        [Tooltip("Hazard warning distance threshold (meters)")]
        public float hazardWarningDistance = 1.6f;

        [Tooltip("SphereCast sweep radius ahead of the user")]
        public float detectionRadius = 0.25f;

        [Tooltip("Check interval (T3 latency target <= 300ms / 0.15s)")]
        public float checkIntervalSeconds = 0.15f;

        [Header("Current Obstacle Status (Read-Only)")]
        public bool isHazardDetected = false;
        public string currentHazardName = "";
        public float currentHazardDistance = 0f;

        private float _nextCheckTime = 0f;
        private Vector3 _lastUserPosition;

        private void Start()
        {
            if (Camera.main != null)
            {
                _lastUserPosition = Camera.main.transform.position;
            }
        }

        private void Update()
        {
            if (Time.time < _nextCheckTime)
                return;

            _nextCheckTime = Time.time + checkIntervalSeconds;
            CheckObstaclesAhead();
        }

        private void CheckObstaclesAhead()
        {
            if (Camera.main == null || PriorityAudioManager.Instance == null)
                return;

            Transform head = Camera.main.transform;
            Vector3 moveDelta = head.position - _lastUserPosition;
            _lastUserPosition = head.position;

            // Detection direction follows horizontal forward gaze/step direction
            Vector3 forwardHorizontal = new Vector3(head.forward.x, 0f, head.forward.z).normalized;
            if (forwardHorizontal.sqrMagnitude < 0.01f)
                forwardHorizontal = head.forward;

            Ray ray = new Ray(head.position, forwardHorizontal);
            if (Physics.SphereCast(ray, detectionRadius, out RaycastHit hit, hazardWarningDistance))
            {
                TrackableObject trackable = hit.collider.GetComponentInParent<TrackableObject>();

                // Only treat as hazard if marked HazardObstacle or an untagged structural collider (wall/pillar)
                bool isHazard = trackable == null || trackable.category == SemanticObjectCategory.HazardObstacle;
                if (!isHazard)
                {
                    isHazardDetected = false;
                    return;
                }

                isHazardDetected = true;
                currentHazardName = trackable != null ? trackable.labelName : hit.collider.gameObject.name;
                currentHazardDistance = hit.distance;

                if (trackable != null)
                {
                    trackable.TriggerVisualHighlight(TrackableObject.HighContrastRedOrange, 1.0f);
                }

                // Trigger P0 alarm if the user is moving toward the hazard or very close
                bool isMoving = moveDelta.magnitude > 0.002f;
                if (isMoving || hit.distance < 1.15f)
                {
                    int roundedMeters = Mathf.Max(1, Mathf.RoundToInt(hit.distance));
                    string meterUnit = roundedMeters == 1 ? "meter" : "meters";
                    string dir = PriorityAudioManager.GetRelativeBodyDirection(head, hit.point);
                    string alarmMessage = $"Warning! {currentHazardName}, {roundedMeters} {meterUnit}, {dir}";

                    PriorityAudioManager.Instance.EnqueueSpeech(
                        alarmMessage,
                        AudioPriority.P0_DangerAlarm,
                        hit.point
                    );
                }
            }
            else
            {
                isHazardDetected = false;
                currentHazardName = "";
                currentHazardDistance = 0f;
            }
        }
    }
}
