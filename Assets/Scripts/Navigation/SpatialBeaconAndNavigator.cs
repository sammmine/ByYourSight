using System;
using System.Collections.Generic;
using UnityEngine;
using ByYourSight.Audio;
using ByYourSight.Perception;

namespace ByYourSight.Navigation
{
    [Serializable]
    public class NavigationDestination
    {
        public string destinationName = "Door";
        public List<Transform> waypoints = new List<Transform>();
    }

    /// <summary>
    /// Manages:
    /// - KF03: Spatial Audio Beacon tracking that guides the user to turn their head (15-degree tolerance).
    ///         Beacon pulses faster and higher in pitch as the head aligns with the target.
    /// - KF05: Real-time walking directions (Priority P1) along a local waypoint graph.
    /// </summary>
    public class SpatialBeaconAndNavigator : MonoBehaviour
    {
        [Header("KF03: Spatial Audio Beacon (Find Object)")]
        public bool isBeaconActive = false;
        public Transform currentBeaconTarget;
        public string currentBeaconLabel = "";
        [Tooltip("Head gaze alignment tolerance angle per T3 document (15 degrees)")]
        public float alignmentToleranceDegrees = 15.0f;

        [Header("KF05: Waypoint Route Navigation (Navigate to Destination)")]
        public bool isNavigationActive = false;
        public string activeDestinationName = "";
        public int currentWaypointIndex = 0;
        public float waypointReachThreshold = 1.1f;
        public float navigationInstructionInterval = 3.5f;
        public List<NavigationDestination> destinations = new List<NavigationDestination>();

        private List<Transform> _activeRoute = new List<Transform>();
        private float _nextBeaconPulseTime = 0f;
        private float _nextNavInstructionTime = 0f;
        private bool _hasAnnouncedAlignment = false;

        private void Update()
        {
            if (isBeaconActive)
            {
                UpdateSpatialBeacon();
            }

            if (isNavigationActive)
            {
                UpdateWaypointNavigation();
            }
        }

        /// <summary>
        /// KF03: Starts guiding the user to locate an object using a Spatial Audio Beacon ("Find [object name]").
        /// </summary>
        public void StartBeaconForObject(TrackableObject targetObj)
        {
            if (targetObj == null || PriorityAudioManager.Instance == null)
                return;

            StopNavigation(silent: true);

            currentBeaconTarget = targetObj.transform;
            currentBeaconLabel = targetObj.labelName;
            isBeaconActive = true;
            _hasAnnouncedAlignment = false;

            targetObj.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 6.0f);

            Transform head = Camera.main != null ? Camera.main.transform : transform;
            float dist = Mathf.Max(0.5f, Mathf.Round(Vector3.Distance(head.position, targetObj.transform.position) * 2f) / 2f);
            string meterUnit = Mathf.Approximately(dist, 1f) ? "meter" : "meters";
            string dir = PriorityAudioManager.GetRelativeBodyDirection(head, targetObj.transform.position);

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, targetObj.transform.position);
            PriorityAudioManager.Instance.EnqueueSpeech(
                $"{currentBeaconLabel}, {dist:0.#} {meterUnit}, {dir}. Turn your head",
                AudioPriority.P1_Navigation,
                targetObj.transform.position
            );
        }

        /// <summary>
        /// KF05: Starts waypoint route navigation toward a destination ("Navigate to [destination]").
        /// </summary>
        public void StartNavigationTo(string queryDestination)
        {
            if (PriorityAudioManager.Instance == null)
                return;

            string q = queryDestination.Trim().ToLowerInvariant();
            NavigationDestination found = destinations.Find(d => d.destinationName.ToLowerInvariant().Contains(q));

            if (found == null || found.waypoints.Count == 0)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"Destination {queryDestination} not found",
                    AudioPriority.P1_Navigation
                );
                return;
            }

            isBeaconActive = false;
            isNavigationActive = true;
            activeDestinationName = found.destinationName;
            _activeRoute = new List<Transform>(found.waypoints);
            currentWaypointIndex = 0;
            _nextNavInstructionTime = Time.time + 0.5f;

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Success);
            PriorityAudioManager.Instance.EnqueueSpeech(
                $"Navigating to {activeDestinationName}",
                AudioPriority.P1_Navigation
            );
        }

        public void StopAllGuidance(bool announce = true)
        {
            bool wasActive = isBeaconActive || isNavigationActive;
            isBeaconActive = false;
            isNavigationActive = false;
            currentBeaconTarget = null;
            _activeRoute.Clear();

            if (announce && wasActive && PriorityAudioManager.Instance != null)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.ListeningEnd);
                PriorityAudioManager.Instance.EnqueueSpeech("Guidance cancelled", AudioPriority.P3_ObjectIdentity);
            }
        }

        public void StopNavigation(bool silent = false)
        {
            isNavigationActive = false;
            _activeRoute.Clear();
            if (!silent && PriorityAudioManager.Instance != null)
            {
                PriorityAudioManager.Instance.EnqueueSpeech("Navigation stopped", AudioPriority.P1_Navigation);
            }
        }

        private void UpdateSpatialBeacon()
        {
            if (currentBeaconTarget == null || Camera.main == null || PriorityAudioManager.Instance == null)
            {
                isBeaconActive = false;
                return;
            }

            Transform head = Camera.main.transform;
            Vector3 toTarget = currentBeaconTarget.position - head.position;
            float distance = toTarget.magnitude;

            // If user is within arm's reach (< 0.85m), search is complete
            if (distance <= 0.85f)
            {
                isBeaconActive = false;
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, currentBeaconTarget.position);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"{currentBeaconLabel} is right in front of you",
                    AudioPriority.P1_Navigation,
                    currentBeaconTarget.position
                );
                return;
            }

            // Compute horizontal angle between head gaze and target position
            Vector3 flatForward = new Vector3(head.forward.x, 0f, head.forward.z).normalized;
            Vector3 flatToTarget = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
            float angleDiff = Vector3.Angle(flatForward, flatToTarget);

            // As head aligns with target (0 deg), pulse interval shortens & pitch rises
            float normalizedAngle = Mathf.Clamp01(angleDiff / 120f);
            float pulseInterval = Mathf.Lerp(0.22f, 0.95f, normalizedAngle);
            float pitchMultiplier = Mathf.Lerp(1.45f, 0.75f, normalizedAngle);

            if (Time.time >= _nextBeaconPulseTime)
            {
                _nextBeaconPulseTime = Time.time + pulseInterval;
                PriorityAudioManager.Instance.PlayEarcon(
                    EarconType.SpatialBeacon,
                    currentBeaconTarget.position,
                    pitchMultiplier
                );
            }

            // Confirm when head enters the 15-degree tolerance per KF03 specification
            if (angleDiff <= alignmentToleranceDegrees && !_hasAnnouncedAlignment)
            {
                _hasAnnouncedAlignment = true;
                int meters = Mathf.Max(1, Mathf.RoundToInt(distance));
                string meterUnit = meters == 1 ? "meter" : "meters";
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"Straight ahead, {meters} {meterUnit}",
                    AudioPriority.P1_Navigation,
                    currentBeaconTarget.position
                );
            }
            else if (angleDiff > alignmentToleranceDegrees + 10f)
            {
                _hasAnnouncedAlignment = false;
            }
        }

        private void UpdateWaypointNavigation()
        {
            if (Camera.main == null || PriorityAudioManager.Instance == null || _activeRoute.Count == 0)
            {
                isNavigationActive = false;
                return;
            }

            Transform head = Camera.main.transform;
            Transform targetWp = _activeRoute[currentWaypointIndex];
            if (targetWp == null)
            {
                isNavigationActive = false;
                return;
            }

            Vector3 flatUser = new Vector3(head.position.x, 0f, head.position.z);
            Vector3 flatWp = new Vector3(targetWp.position.x, 0f, targetWp.position.z);
            float dist = Vector3.Distance(flatUser, flatWp);

            // Check if current waypoint is reached
            if (dist <= waypointReachThreshold)
            {
                currentWaypointIndex++;
                if (currentWaypointIndex >= _activeRoute.Count)
                {
                    isNavigationActive = false;
                    PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, targetWp.position);
                    PriorityAudioManager.Instance.EnqueueSpeech(
                        $"Arrived at {activeDestinationName}",
                        AudioPriority.P1_Navigation,
                        targetWp.position
                    );
                    return;
                }

                targetWp = _activeRoute[currentWaypointIndex];
                _nextNavInstructionTime = Time.time + 0.2f;
            }

            // Provide periodic walking direction instructions (Priority P1) + beacon ping at active waypoint
            if (Time.time >= _nextNavInstructionTime)
            {
                _nextNavInstructionTime = Time.time + navigationInstructionInterval;

                Vector3 toWp = targetWp.position - head.position;
                toWp.y = 0f;
                Vector3 forward = head.forward;
                forward.y = 0f;

                float signedAngle = Vector3.SignedAngle(forward.normalized, toWp.normalized, Vector3.up);
                int meters = Mathf.Max(1, Mathf.RoundToInt(dist));
                string meterUnit = meters == 1 ? "meter" : "meters";

                string turnCommand;
                if (Mathf.Abs(signedAngle) <= 22f)
                    turnCommand = $"Go straight, {meters} {meterUnit}";
                else if (signedAngle > 22f && signedAngle <= 65f)
                    turnCommand = $"Bear right, {meters} {meterUnit}";
                else if (signedAngle > 65f)
                    turnCommand = $"Turn right, {meters} {meterUnit}";
                else if (signedAngle < -22f && signedAngle >= -65f)
                    turnCommand = $"Bear left, {meters} {meterUnit}";
                else
                    turnCommand = $"Turn left, {meters} {meterUnit}";

                PriorityAudioManager.Instance.PlayEarcon(EarconType.SpatialBeacon, targetWp.position, 1.15f);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    turnCommand,
                    AudioPriority.P1_Navigation,
                    targetWp.position
                );
            }
        }
    }
}
