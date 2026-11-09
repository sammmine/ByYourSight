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
        [Tooltip("Turn instruction trigger distance: 1.0m (100 cm before turn point per M2/T3)")]
        public float waypointReachThreshold = 1.0f;
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
        /// KF03: Starts guiding the user to locate an object using a Spatial Audio Beacon ("Cari [nama benda]").
        /// </summary>
        public void StartBeaconForObject(TrackableObject targetObj)
        {
            if (targetObj == null || PriorityAudioManager.Instance == null)
                return;

            StopNavigation(silent: true);

            currentBeaconTarget = targetObj.transform;
            currentBeaconLabel = RealWorldObjectDetector.ToIndonesianLabel(targetObj.labelName);
            isBeaconActive = true;
            _hasAnnouncedAlignment = false;

            targetObj.TriggerVisualHighlight(TrackableObject.HighContrastYellow, 6.0f);

            Transform head = Camera.main != null ? Camera.main.transform : transform;
            string distText = PriorityAudioManager.FormatDistance(Vector3.Distance(head.position, targetObj.transform.position));
            string dir = PriorityAudioManager.GetRelativeBodyDirection(head, targetObj.transform.position);

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Success, targetObj.transform.position);
            PriorityAudioManager.Instance.EnqueueSpeech(
                $"{currentBeaconLabel}, {distText}, {dir}. Putar kepala Anda",
                AudioPriority.P1_Navigation,
                targetObj.transform.position
            );
        }

        /// <summary>
        /// KF05: Starts waypoint route navigation toward a destination ("Antar ke [tujuan]" / "Navigate to [destination]").
        /// </summary>
        public void StartNavigationTo(string queryDestination)
        {
            if (PriorityAudioManager.Instance == null)
                return;

            string q = queryDestination.Trim().ToLowerInvariant();
            string qIndo = RealWorldObjectDetector.ToIndonesianLabel(q);
            List<Transform> route = null;
            string routeName = qIndo;

            // 1. Real room: a place the user tagged with "Simpan [label]" / "Save [label]" (straight-line guidance to that tag)
            ObjectRecognizer recognizer = FindObjectOfType<ObjectRecognizer>();
            TrackableObject savedTag = recognizer != null ? recognizer.FindObjectByName(q) : null;
            if (savedTag != null)
            {
                route = new List<Transform> { savedTag.transform };
                routeName = RealWorldObjectDetector.ToIndonesianLabel(savedTag.labelName);
            }
            else
            {
                // 2. Simulated room: predefined waypoint graph, only while the simulated room is visible.
                NavigationDestination found = destinations.Find(d =>
                {
                    string dRaw = d.destinationName.ToLowerInvariant();
                    string dIndo = RealWorldObjectDetector.ToIndonesianLabel(d.destinationName).ToLowerInvariant();
                    return dRaw.Contains(q) || q.Contains(dRaw) ||
                           dIndo.Contains(qIndo.ToLowerInvariant()) || qIndo.ToLowerInvariant().Contains(dIndo);
                });
                if (found != null && found.waypoints.Count > 0 &&
                    found.waypoints.TrueForAll(w => w != null && w.gameObject.activeInHierarchy))
                {
                    route = new List<Transform>(found.waypoints);
                    routeName = RealWorldObjectDetector.ToIndonesianLabel(found.destinationName);
                }
            }

            if (route == null)
            {
                PriorityAudioManager.Instance.PlayEarcon(EarconType.Failure);
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"{routeName} belum disimpan. Arahkan pandangan lalu ucapkan Simpan {routeName}",
                    AudioPriority.P1_Navigation
                );
                return;
            }

            isBeaconActive = false;
            isNavigationActive = true;
            activeDestinationName = routeName;
            _activeRoute = route;
            currentWaypointIndex = 0;
            _nextNavInstructionTime = Time.time + 0.5f;

            PriorityAudioManager.Instance.PlayEarcon(EarconType.Success);
            PriorityAudioManager.Instance.EnqueueSpeech(
                $"Mengantar ke {activeDestinationName}",
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
                PriorityAudioManager.Instance.EnqueueSpeech("Panduan dibatalkan", AudioPriority.P3_ObjectIdentity);
            }
        }

        public void StopNavigation(bool silent = false)
        {
            isNavigationActive = false;
            _activeRoute.Clear();
            if (!silent && PriorityAudioManager.Instance != null)
            {
                PriorityAudioManager.Instance.EnqueueSpeech("Navigasi dihentikan", AudioPriority.P1_Navigation);
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
                    $"{currentBeaconLabel} tepat di depan Anda",
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
                PriorityAudioManager.Instance.EnqueueSpeech(
                    $"Lurus di depan, {meters} meter",
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
                        $"Sampai di {activeDestinationName}",
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

                string turnCommand;
                if (Mathf.Abs(signedAngle) <= 22f)
                    turnCommand = $"Jalan lurus, {meters} meter";
                else if (signedAngle > 22f && signedAngle <= 65f)
                    turnCommand = $"Serong kanan, {meters} meter";
                else if (signedAngle > 65f)
                    turnCommand = $"Belok kanan, {meters} meter";
                else if (signedAngle < -22f && signedAngle >= -65f)
                    turnCommand = $"Serong kiri, {meters} meter";
                else
                    turnCommand = $"Belok kiri, {meters} meter";

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
