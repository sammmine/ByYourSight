using UnityEngine;
using ByYourSight.Audio;
using ByYourSight.Perception;

namespace ByYourSight.Safety
{
    /// <summary>
    /// KF07 (Critical Priority): Detects obstacles in the walking path using SphereCasts at head level and
    /// knee level against the HoloLens 2 Spatial Mesh and camera-detected objects.
    ///
    /// To avoid overstimulation, speech follows two tiers and is not repeated for the same obstacle:
    ///   - Near (≤ 1.5 m) and walking toward it: calm notice, no beep   -> "Chair, 1 meter, ahead"
    ///   - Critical (≤ 0.7 m):                    alarm with double-beep -> "Warning! Chair, ahead"
    /// Obstacle names differ by type: detected object label, "Wall", "Low obstacle", "Overhead obstacle".
    /// </summary>
    public class ObstacleDetector : MonoBehaviour
    {
        [Header("Hazard Detection Settings (KF07)")]
        [Tooltip("Hazard warning distance threshold: 1.1m (reduced from 1.5m to avoid premature alerts)")]
        public float hazardWarningDistance = 1.1f;

        [Tooltip("Below this distance the P0 alarm with double-beep always plays (reduced from 0.7m to 0.45m)")]
        public float criticalDistance = 0.45f;

        [Tooltip("SphereCast sweep radius ahead of the user")]
        public float detectionRadius = 0.18f;

        [Tooltip("Check interval: 0.15s / 150ms (T3 latency target < 300ms)")]
        public float checkIntervalSeconds = 0.15f;

        [Header("Anti-Overstimulation")]
        [Tooltip("Walking speed toward the obstacle (m/s) needed before a non-critical notice is spoken")]
        public float minApproachSpeed = 0.25f;
        [Tooltip("Walking speed toward the obstacle (m/s) needed before a critical alarm is triggered")]
        public float minCriticalApproachSpeed = 0.15f;
        [Tooltip("Minimum quiet cooldown between any hazard announcements")]
        public float minAnyWarningCooldown = 4.0f;
        [Tooltip("Same obstacle type is not announced again within this time unless it becomes critical")]
        public float sameTypeRepeatSeconds = 12f;
        [Tooltip("The exact same obstacle location is not announced again within this time unless it becomes critical")]
        public float sameObstacleRepeatSeconds = 20f;

        [Header("Current Obstacle Status (Read-Only)")]
        public bool isHazardDetected = false;
        public string currentHazardName = "";
        public float currentHazardDistance = 0f;

        private float _nextCheckTime = 0f;
        private Vector3 _lastUserPosition;
        private float _lastSampleTime;
        private Vector3 _smoothedVelocity;

        private string _lastAnnouncedLabel = "";
        private Vector3Int _lastAnnouncedCell;
        private int _lastAnnouncedTier = 0;
        private float _lastAnnounceTime = -100f;

        private void Start()
        {
            if (Camera.main != null)
            {
                _lastUserPosition = Camera.main.transform.position;
            }
            _lastSampleTime = Time.time;
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
            UpdateWalkingVelocity(head);

            // Path direction: where the user is walking, or where the head faces when standing still
            Vector3 flatVelocity = new Vector3(_smoothedVelocity.x, 0f, _smoothedVelocity.z);
            Vector3 pathDir = flatVelocity.magnitude > 0.3f
                ? flatVelocity.normalized
                : new Vector3(head.forward.x, 0f, head.forward.z).normalized;
            if (pathDir.sqrMagnitude < 0.01f)
                pathDir = head.forward;

            // Head-level ray (walls, pillars, people, shelves) and knee-level ray (chairs, tables, boxes)
            bool headHit = TryFindHazard(new Ray(head.position, pathDir), hazardWarningDistance, head,
                out RaycastHit headRay, out TrackableObject headObj);

            // Two downward rays: knee height at the warning distance, and shin height close by,
            // so low obstacles (boxes, chair legs) stay detected all the way to the critical tier.
            float floorY = SurfaceClassifier.EstimateFloorY(head);
            bool kneeHit = false;
            RaycastHit kneeRay = default;
            TrackableObject kneeObj = null;
            float kneeFlatBest = float.MaxValue;
            Vector2[] lowTargets =
            {
                new Vector2(hazardWarningDistance, 0.45f), // (distance ahead, height above floor)
                new Vector2(criticalDistance + 0.1f, 0.25f)
            };
            foreach (Vector2 target in lowTargets)
            {
                Vector3 targetPoint = head.position + pathDir * target.x;
                targetPoint.y = Mathf.Min(head.position.y - 0.3f, floorY + target.y);
                Vector3 toTarget = targetPoint - head.position;
                if (TryFindHazard(new Ray(head.position, toTarget.normalized), toTarget.magnitude, head,
                        out RaycastHit lowRay, out TrackableObject lowObj))
                {
                    float flat = FlatDistance(head.position, lowRay.point);
                    if (flat < kneeFlatBest)
                    {
                        kneeFlatBest = flat;
                        kneeRay = lowRay;
                        kneeObj = lowObj;
                        kneeHit = true;
                    }
                }
            }

            if (!headHit && !kneeHit)
            {
                isHazardDetected = false;
                currentHazardName = "";
                currentHazardDistance = 0f;
                return;
            }

            // Pick the obstacle that is closer horizontally
            float headFlat = headHit ? FlatDistance(head.position, headRay.point) : float.MaxValue;
            float kneeFlat = kneeHit ? FlatDistance(head.position, kneeRay.point) : float.MaxValue;
            bool useHead = headFlat <= kneeFlat;
            RaycastHit hit = useHead ? headRay : kneeRay;
            TrackableObject trackable = useHead ? headObj : kneeObj;
            float distance = useHead ? headFlat : kneeFlat;

            string label;
            if (trackable != null)
            {
                label = trackable.labelName;
            }
            else
            {
                label = SurfaceClassifier.Describe(hit, head);
                // Both rays hitting a vertical surface at a similar distance means a wall, not a low obstacle
                if (headHit && kneeHit && headObj == null && kneeObj == null && Mathf.Abs(headFlat - kneeFlat) < 0.35f)
                    label = SurfaceClassifier.Wall;
            }

            if (label == SurfaceClassifier.Floor)
            {
                isHazardDetected = false;
                return;
            }

            isHazardDetected = true;
            currentHazardName = label;
            currentHazardDistance = distance;

            Vector3 toHazard = hit.point - head.position;
            toHazard.y = 0f;
            float approachSpeed = toHazard.sqrMagnitude > 0.0001f ? Vector3.Dot(flatVelocity, toHazard.normalized) : 0f;

            bool critical = distance <= criticalDistance;
            bool walkingToward = approachSpeed >= minApproachSpeed;
            bool isPerson = trackable != null && trackable.category == SemanticObjectCategory.Person;

            // People are handled separately by KF08 (Social Info P2 "Person approaching").
            // Also ignore user's own hands/arms detected as person.
            if (isPerson)
            {
                return;
            }

            // Standing still or turning the head near a wall/desk stays silent.
            // Critical range requires a deliberate step forward (minCriticalApproachSpeed).
            if (critical)
            {
                if (approachSpeed < minCriticalApproachSpeed)
                    return;
            }
            else if (!walkingToward)
            {
                return;
            }

            int tier = critical ? 2 : 1;
            Vector3Int cell = Vector3Int.RoundToInt(hit.point / 0.75f);
            float sinceLast = Time.time - _lastAnnounceTime;
            int lastTier = sinceLast > sameObstacleRepeatSeconds ? 0 : _lastAnnouncedTier;
            bool escalates = tier > lastTier;
            bool sameType = label == _lastAnnouncedLabel;
            bool sameCell = cell == _lastAnnouncedCell;
            bool isRepeat = (sameType && sinceLast < sameTypeRepeatSeconds) || (sameCell && sinceLast < sameObstacleRepeatSeconds);

            if (!escalates)
            {
                if (isRepeat) return;
                if (sinceLast < minAnyWarningCooldown) return;
            }

            if (!Announce(label, distance, hit.point, head, trackable, critical))
                return; // dropped by the audio manager's P0 cooldown; try again on the next check

            _lastAnnouncedLabel = label;
            _lastAnnouncedCell = cell;
            _lastAnnouncedTier = tier;
            _lastAnnounceTime = Time.time;
        }

        private bool Announce(string label, float distance, Vector3 point, Transform head, TrackableObject trackable, bool critical)
        {
            string dir = PriorityAudioManager.GetRelativeBodyDirection(head, point);
            string message;
            if (critical)
            {
                message = $"Warning! {label}, {dir}";
            }
            else
            {
                int roundedMeters = Mathf.Max(1, Mathf.RoundToInt(distance));
                string meterUnit = roundedMeters == 1 ? "meter" : "meters";
                message = $"{label}, {roundedMeters} {meterUnit}, {dir}";
            }

            bool spoken = PriorityAudioManager.Instance.EnqueueSpeech(
                message,
                AudioPriority.P0_DangerAlarm,
                point,
                "",
                playAlarmEarcon: critical
            );
            if (!spoken)
                return false;

            if (trackable != null)
                trackable.TriggerVisualHighlight(TrackableObject.HighContrastRedOrange, 1.5f);
            else
                ObjectRecognizer.SpawnTemporaryRealWorldHighlightBox(point, TrackableObject.HighContrastRedOrange, 1.5f);
            return true;
        }

        /// <summary>
        /// Nearest hit that counts as a hazard: untagged real-world Spatial Mesh or a HazardObstacle (chair, desk, etc.).
        /// Everyday objects, user-saved items, and people are skipped (people are monitored via KF08).
        /// </summary>
        private bool TryFindHazard(Ray ray, float maxDistance, Transform head, out RaycastHit best, out TrackableObject bestObj)
        {
            best = default;
            bestObj = null;
            RaycastHit[] hits = Physics.SphereCastAll(ray, detectionRadius, maxDistance);
            float bestDist = float.MaxValue;

            foreach (RaycastHit h in hits)
            {
                if (h.distance <= 0f || h.distance >= bestDist) continue; // distance 0 = overlap at origin
                TrackableObject t = h.collider.GetComponentInParent<TrackableObject>();
                if (t != null && t.category == SemanticObjectCategory.EverydayObject) continue;
                if (t != null && t.category == SemanticObjectCategory.Person) continue; // Skip Person; KF08 handles social info
                if (t == null && SurfaceClassifier.Describe(h, head) == SurfaceClassifier.Floor) continue;

                best = h;
                bestObj = t;
                bestDist = h.distance;
            }
            return bestDist < float.MaxValue;
        }

        private void UpdateWalkingVelocity(Transform head)
        {
            float dt = Mathf.Max(0.0001f, Time.time - _lastSampleTime);
            Vector3 v = (head.position - _lastUserPosition) / dt;
            v.y = 0f;
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, v, 0.35f);
            _lastUserPosition = head.position;
            _lastSampleTime = Time.time;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
