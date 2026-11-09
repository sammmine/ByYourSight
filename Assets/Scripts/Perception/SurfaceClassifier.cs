using UnityEngine;

namespace ByYourSight.Perception
{
    /// <summary>
    /// Gives an unlabeled HoloLens 2 Spatial Mesh hit a short, distinguishable name
    /// ("Wall", "Low obstacle", "Overhead obstacle", "Floor") from its surface normal and height above the floor,
    /// so hazard messages differ by obstacle type instead of a generic "Obstacle".
    /// </summary>
    public static class SurfaceClassifier
    {
        public const string Floor = "Lantai";
        public const string Wall = "Dinding";
        public const string LowObstacle = "Rintangan rendah";
        public const string OverheadObstacle = "Rintangan atas";
        public const string Obstacle = "Rintangan";

        private static float _cachedFloorY = float.NaN;
        private static float _floorSampleTime = -10f;

        /// <summary>
        /// Estimated floor height under the user: a downward raycast against real surfaces, refreshed every 0.5 s.
        /// Falls back to 1.6 m below the head when the floor hasn't been scanned yet.
        /// </summary>
        public static float EstimateFloorY(Transform head)
        {
            if (Time.time - _floorSampleTime < 0.5f && !float.IsNaN(_cachedFloorY))
                return _cachedFloorY;

            _floorSampleTime = Time.time;
            float fallback = head.position.y - 1.6f;
            RaycastHit[] hits = Physics.RaycastAll(head.position, Vector3.down, 2.5f);
            float best = float.NaN;
            float bestDist = float.MaxValue;
            foreach (RaycastHit h in hits)
            {
                if (h.collider.GetComponentInParent<TrackableObject>() != null) continue;
                if (h.distance < 0.9f) continue; // ignore desks/tables directly below the head
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h.point.y;
                }
            }

            _cachedFloorY = float.IsNaN(best) ? fallback : best;
            return _cachedFloorY;
        }

        public static string Describe(RaycastHit hit, Transform head)
        {
            float floorY = EstimateFloorY(head);
            float heightAboveFloor = hit.point.y - floorY;
            float up = Vector3.Dot(hit.normal.normalized, Vector3.up);

            if (up > 0.7f)
                return heightAboveFloor < 0.15f ? Floor : LowObstacle;
            if (up < -0.7f)
                return OverheadObstacle;
            if (Mathf.Abs(up) < 0.45f)
                return heightAboveFloor < 0.9f ? LowObstacle : Wall;
            return Obstacle;
        }
    }
}
