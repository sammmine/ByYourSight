using UnityEngine;

namespace ByYourSight.Perception
{
    public enum SemanticObjectCategory
    {
        EverydayObject, // KF01 & KF02: Everyday objects (Water bottle, Chair, Coffee mug, Keys, etc.)
        HazardObstacle, // KF07: Hazard obstacles (Pillar, Stairs, Low table, Wall)
        Person          // KF08: People around the user
    }

    /// <summary>
    /// Scene object marker (or 3D bounding box result from local YOLOv8 ONNX inference)
    /// storing English metadata and supporting high-contrast visual highlights (T3 Design System).
    /// </summary>
    public class TrackableObject : MonoBehaviour
    {
        [Header("Identitas Objek (Bahasa Indonesia)")]
        public string labelName = "Botol minum";
        public string detailedDescription = "Botol air minum biru di atas meja";
        public SemanticObjectCategory category = SemanticObjectCategory.EverydayObject;

        [Header("Movement Simulation (For KF08 Approaching Person)")]
        public bool simulateApproachingUser = false;
        public float approachSpeed = 0.8f;

        private Renderer _renderer;
        private Color _originalColor;
        private float _highlightUntil = 0f;
        private Color _activeHighlightColor;
        private LineRenderer _outlineBoxRenderer;

        // Semantic colors per T3 Design System (HoloLens Additive Display):
        // Yellow = info/target, Red/Orange = hazard, Green = success
        public static readonly Color HighContrastYellow = new Color(1f, 0.92f, 0.1f, 1f);
        public static readonly Color HighContrastRedOrange = new Color(1f, 0.25f, 0.05f, 1f);
        public static readonly Color HighContrastGreen = new Color(0.15f, 1f, 0.35f, 1f);

        private void Awake()
        {
            _renderer = GetComponentInChildren<Renderer>();
            if (_renderer != null && _renderer.material != null)
            {
                _originalColor = _renderer.material.HasProperty("_BaseColor")
                    ? _renderer.material.GetColor("_BaseColor")
                    : _renderer.material.color;
            }
            SetupThickBoundingBoxOutline();
        }

        private void SetupThickBoundingBoxOutline()
        {
            GameObject outlineObj = new GameObject("T3_BoundingBoxOutline");
            outlineObj.transform.SetParent(transform, false);
            _outlineBoxRenderer = outlineObj.AddComponent<LineRenderer>();
            _outlineBoxRenderer.useWorldSpace = true;
            _outlineBoxRenderer.loop = false;
            _outlineBoxRenderer.widthMultiplier = 0.025f; // Thick high-contrast outline per T3 Visual Layer
            Shader lineShader = Shader.Find("Sprites/Default");
            if (lineShader != null)
            {
                _outlineBoxRenderer.material = new Material(lineShader);
            }
            _outlineBoxRenderer.enabled = false;
        }

        private void Update()
        {
            if (simulateApproachingUser && Camera.main != null)
            {
                Vector3 userPos = Camera.main.transform.position;
                Vector3 target = new Vector3(userPos.x, transform.position.y, userPos.z);
                float dist = Vector3.Distance(transform.position, target);
                if (dist > 1.3f)
                {
                    transform.position = Vector3.MoveTowards(transform.position, target, approachSpeed * Time.deltaTime);
                }
            }

            if (_outlineBoxRenderer != null && _outlineBoxRenderer.enabled && _renderer != null)
            {
                UpdateBoundingBoxPositions(_renderer.bounds);
            }

            if (_renderer != null && Time.time > _highlightUntil && _highlightUntil > 0f)
            {
                _highlightUntil = 0f;
                ApplyColor(_originalColor);
                if (_outlineBoxRenderer != null)
                {
                    _outlineBoxRenderer.enabled = false;
                }
            }
        }

        public void TriggerVisualHighlight(Color semanticColor, float duration = 2.0f)
        {
            if (_renderer == null) return;
            _activeHighlightColor = semanticColor;
            ApplyColor(semanticColor);
            if (_outlineBoxRenderer != null)
            {
                _outlineBoxRenderer.startColor = semanticColor;
                _outlineBoxRenderer.endColor = semanticColor;
                UpdateBoundingBoxPositions(_renderer.bounds);
                _outlineBoxRenderer.enabled = true;
            }
            _highlightUntil = Time.time + duration;
        }

        private void UpdateBoundingBoxPositions(Bounds b)
        {
            Vector3 min = b.min - Vector3.one * 0.03f;
            Vector3 max = b.max + Vector3.one * 0.03f;

            Vector3[] pts = new Vector3[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(min.x, max.y, min.z)
            };

            _outlineBoxRenderer.positionCount = pts.Length;
            _outlineBoxRenderer.SetPositions(pts);
        }

        private void ApplyColor(Color c)
        {
            if (_renderer == null || _renderer.material == null) return;
            _renderer.material.color = c;
            if (_renderer.material.HasProperty("_BaseColor"))
            {
                _renderer.material.SetColor("_BaseColor", c);
            }
        }
    }
}
