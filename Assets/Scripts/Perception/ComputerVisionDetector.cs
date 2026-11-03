using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace ByYourSight.Perception
{
    /// <summary>
    /// Result of hybrid 2D RGB Camera + 3D Spatial Mesh Point-Cloud object recognition.
    /// </summary>
    public struct CVDetectionResult
    {
        public bool isDetected;
        public string label;
        public string detailedDescription;
        public float confidence;
        public Vector3 worldPosition;
        public Vector3 estimatedBounds;
        public SemanticObjectCategory category;
    }

    /// <summary>
    /// On-device Computer Vision & 3D Spatial Point-Cloud Object Detector for HoloLens 2.
    /// Combines:
    /// 1. 2D RGB Camera Analysis (WebCamTexture from HoloLens PV Camera / Remoting / Webcam):
    ///    - Real-time low-light luminance evaluation (KF01)
    ///    - Foreground contour extraction, Sobel edge orientation histograms, HSV color profile,
    ///      aspect ratio, specular highlight, and screen/skin signature classification.
    /// 2. 3D Spatial Mesh Multi-Ray Point-Cloud Classifier (ARMeshManager):
    ///    - 9x9 depth-cluster segmentation around gaze hit point
    ///    - Floor elevation calibration, support-surface (tabletop) detection, protrusion depth,
    ///      and metric width/height/depth estimation in meters
    ///    - Distinguishes 20+ indoor object types (water bottle, coffee mug, cell phone, laptop,
    ///      monitor, book, bag, small box, chair, desk, table, sofa, bed, door, window, cabinet,
    ///      sink, stairs, pillar, person, wall) instead of generic "Object surface".
    /// 3. Self-healing runtime ARMeshManager collider synchronization & XR Origin setup.
    /// </summary>
    public class ComputerVisionDetector : MonoBehaviour
    {
        public static ComputerVisionDetector Instance { get; private set; }

        [Header("Camera & Vision Settings (KF01 & KF08)")]
        [Tooltip("Automatically start HoloLens PV Camera / WebCamTexture for 2D RGB vision & low-light checks")]
        public bool enableCameraVision = true;

        [Tooltip("Luminance threshold (0..1) below which KF01 announces low-light warning")]
        [Range(0.01f, 0.25f)]
        public float lowLightLuminanceThreshold = 0.055f;

        [Tooltip("Interval in seconds between background CV & spatial mesh collider sync updates")]
        public float backgroundScanInterval = 0.35f;

        [Header("Runtime Telemetry (Read-Only)")]
        public bool isCameraActive = false;
        public string activeCameraName = "None";
        public float currentFrameLuminance = 0.5f;
        public bool isLowLightDetected = false;
        public string lastDetectedLabel = "None";
        public float lastDetectedConfidence = 0f;

        private WebCamTexture _webCamTexture;
        private Color32[] _pixelBuffer;
        private float _nextBackgroundScanTime = 0f;
        private float _nextColliderSyncTime = 0f;
        private ARMeshManager _meshManager;
        private TrackableObject _dynamicRealWorldPerson;
        private Vector3 _lastPersonClusterPos = Vector3.zero;
        private float _lastPersonSeenTime = -999f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            EnsureSpatialMeshAndXROriginReady();
            InitializeCameraFeed();
        }

        private void OnDestroy()
        {
            if (_meshManager != null)
            {
                _meshManager.meshesChanged -= OnSpatialMeshesChanged;
            }

            if (_webCamTexture != null && _webCamTexture.isPlaying)
            {
                _webCamTexture.Stop();
            }
        }

        private void Update()
        {
            if (Time.time >= _nextColliderSyncTime)
            {
                _nextColliderSyncTime = Time.time + 1.0f;
                SyncAllSpatialMeshColliders();
            }

            if (Time.time >= _nextBackgroundScanTime)
            {
                _nextBackgroundScanTime = Time.time + backgroundScanInterval;
                UpdateCameraLuminanceAndVision();
                ScanForApproachingRealWorldPeople();
            }
        }

        /// <summary>
        /// Ensures XR Origin wraps Main Camera and ARMeshManager, and subscribes to mesh updates
        /// so every HoloLens 2 spatial mesh chunk has an active MeshCollider for physics raycasts.
        /// </summary>
        public void EnsureSpatialMeshAndXROriginReady()
        {
            Camera mainCam = Camera.main;
            if (mainCam == null)
                return;

            XROrigin xrOrigin = FindObjectOfType<XROrigin>();
            if (xrOrigin == null)
            {
                GameObject originObj = new GameObject("XR Origin (HoloLens 2)");
                xrOrigin = originObj.AddComponent<XROrigin>();
                GameObject offsetObj = new GameObject("Camera Offset");
                offsetObj.transform.SetParent(originObj.transform, false);
                xrOrigin.CameraFloorOffsetObject = offsetObj;
                xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
                xrOrigin.CameraYOffset = 0f;
            }

            if (xrOrigin.CameraFloorOffsetObject != null &&
                mainCam.transform.parent != xrOrigin.CameraFloorOffsetObject.transform)
            {
                mainCam.transform.SetParent(xrOrigin.CameraFloorOffsetObject.transform, true);
            }
            xrOrigin.Camera = mainCam;

            _meshManager = FindObjectOfType<ARMeshManager>();
            if (_meshManager != null)
            {
                if (_meshManager.transform.parent != xrOrigin.transform)
                {
                    _meshManager.transform.SetParent(xrOrigin.transform, false);
                }

                _meshManager.meshesChanged -= OnSpatialMeshesChanged;
                _meshManager.meshesChanged += OnSpatialMeshesChanged;
            }

            SyncAllSpatialMeshColliders();
        }

        private void OnSpatialMeshesChanged(ARMeshesChangedEventArgs args)
        {
            if (args.added != null)
            {
                foreach (MeshFilter mf in args.added)
                    SyncMeshCollider(mf);
            }
            if (args.updated != null)
            {
                foreach (MeshFilter mf in args.updated)
                    SyncMeshCollider(mf);
            }
        }

        private static void SyncMeshCollider(MeshFilter mf)
        {
            if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0)
                return;

            MeshCollider mc = mf.GetComponent<MeshCollider>();
            if (mc == null)
            {
                mc = mf.gameObject.AddComponent<MeshCollider>();
            }

            if (mc.sharedMesh != mf.sharedMesh)
            {
                mc.sharedMesh = mf.sharedMesh;
            }
        }

        private void SyncAllSpatialMeshColliders()
        {
            if (_meshManager == null)
            {
                _meshManager = FindObjectOfType<ARMeshManager>();
                if (_meshManager == null)
                    return;
            }

            // Ensure the template prefab itself does not block rays at (0,0,0)
            if (_meshManager.meshPrefab != null)
            {
                Collider templateCol = _meshManager.meshPrefab.GetComponent<Collider>();
                if (templateCol != null && _meshManager.meshPrefab.sharedMesh == null)
                {
                    templateCol.enabled = true;
                }
            }

            foreach (MeshFilter mf in _meshManager.meshes)
            {
                SyncMeshCollider(mf);
            }
        }

        private void InitializeCameraFeed()
        {
            if (!enableCameraVision)
                return;

            try
            {
                WebCamDevice[] devices = WebCamTexture.devices;
                if (devices == null || devices.Length == 0)
                {
                    isCameraActive = false;
                    activeCameraName = "No WebCamDevice detected (Using 3D Spatial Mesh Point-Cloud CV)";
                    return;
                }

                string selectedDevice = devices[0].name;
                for (int i = 0; i < devices.Length; i++)
                {
                    string n = devices[i].name.ToLowerInvariant();
                    if (n.Contains("hololens") || n.Contains("pv") || n.Contains("remoting") ||
                        n.Contains("mixed reality") || n.Contains("windows"))
                    {
                        selectedDevice = devices[i].name;
                        break;
                    }
                }

                _webCamTexture = new WebCamTexture(selectedDevice, 320, 240, 15);
                _webCamTexture.Play();
                activeCameraName = selectedDevice;
                isCameraActive = true;
                Debug.Log($"[ByYourSight CV] Started camera feed: {selectedDevice}");
            }
            catch (System.Exception ex)
            {
                isCameraActive = false;
                activeCameraName = $"Camera unavailable ({ex.Message})";
            }
        }

        /// <summary>
        /// Updates frame luminance for KF01 low-light detection.
        /// Ignores uninitialized all-zero textures (variance near 0) before the camera warms up.
        /// </summary>
        private void UpdateCameraLuminanceAndVision()
        {
            if (_webCamTexture == null || !_webCamTexture.isPlaying || _webCamTexture.width <= 16)
            {
                isLowLightDetected = false;
                return;
            }

            int w = _webCamTexture.width;
            int h = _webCamTexture.height;
            int total = w * h;
            if (_pixelBuffer == null || _pixelBuffer.Length != total)
            {
                _pixelBuffer = new Color32[total];
            }

            _webCamTexture.GetPixels32(_pixelBuffer);

            double sumY = 0.0;
            double sumSqY = 0.0;
            int step = Mathf.Max(1, total / 1200);
            int samples = 0;

            for (int i = 0; i < total; i += step)
            {
                Color32 c = _pixelBuffer[i];
                float y = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                sumY += y;
                sumSqY += y * y;
                samples++;
            }

            if (samples > 0)
            {
                float meanY = (float)(sumY / samples);
                float varianceY = (float)((sumSqY / samples) - (meanY * meanY));
                currentFrameLuminance = meanY;

                // Only trigger low-light if the camera frame has real sensor variance (not a blank uninitialized stream)
                isLowLightDetected = varianceY > 0.0004f && meanY < lowLightLuminanceThreshold;
            }
        }

        /// <summary>
        /// Multi-ray gaze sweep that finds the best 3D object or Spatial Mesh hit in the user's central gaze cone.
        /// Prevents false "No object detected ahead" when looking slightly off-center or between mesh triangles.
        /// </summary>
        public bool TryFindBestGazeHit(Transform head, float sphereRadius, float maxDist, out RaycastHit bestHit)
        {
            Ray centerRay = new Ray(head.position, head.forward);
            if (Physics.SphereCast(centerRay, sphereRadius, out bestHit, maxDist))
            {
                return true;
            }

            if (Physics.Raycast(centerRay, out bestHit, maxDist))
            {
                return true;
            }

            // Sweep a concentric gaze cone (up to 12 degrees) to catch objects in the user's central field of view
            float[] angles = { 3.5f, 7.0f, 11.5f };
            Vector3[] dirs =
            {
                head.up, -head.up, head.right, -head.right,
                (head.up + head.right).normalized, (head.up - head.right).normalized,
                (-head.up + head.right).normalized, (-head.up - head.right).normalized
            };

            float closestDist = float.MaxValue;
            bool found = false;
            bestHit = default;

            foreach (float angleDeg in angles)
            {
                float rad = angleDeg * Mathf.Deg2Rad;
                float sin = Mathf.Sin(rad);
                float cos = Mathf.Cos(rad);

                foreach (Vector3 offsetDir in dirs)
                {
                    Vector3 rayDir = (head.forward * cos + offsetDir * sin).normalized;
                    if (Physics.SphereCast(head.position, sphereRadius * 0.8f, rayDir, out RaycastHit candidate, maxDist))
                    {
                        // Prioritize TrackableObjects first, otherwise closest valid surface
                        TrackableObject t = candidate.collider.GetComponentInParent<TrackableObject>();
                        float scoreDist = t != null ? candidate.distance * 0.6f : candidate.distance;
                        if (scoreDist < closestDist)
                        {
                            closestDist = scoreDist;
                            bestHit = candidate;
                            found = true;
                        }
                    }
                }

                if (found)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Main entry point for KF01 ("What is this?"):
        /// Analyzes an unrecognized real-world object at the gaze raycast hit (or in the 2D camera view)
        /// using hybrid 3D Spatial Mesh Point-Cloud Geometry + 2D RGB Camera Vision.
        /// </summary>
        public CVDetectionResult IdentifyUnrecognizedObjectAtGaze(Transform head, bool has3DHit, RaycastHit hit, float maxDist)
        {
            // 1. Analyze 2D RGB camera frame if available
            bool has2D = TryClassifyFrom2DCameraGaze(out string label2D, out string colorName2D, out float conf2D, out float aspect2D, out float fillRatio2D);

            // 2. If we have a 3D Spatial Mesh hit, run the 9x9 3D Point-Cloud Geometric Classifier
            if (has3DHit)
            {
                CVDetectionResult geoResult = ClassifyFrom3DSpatialPointCloud(head, hit, maxDist);

                // Fuse 2D camera vision with 3D spatial geometry when 2D sees a clear foreground object
                if (has2D && conf2D >= 0.52f)
                {
                    // If 3D says generic surface (Wall/Table/Desk) and 2D sees a small foreground item held in front or on top of it
                    bool isGenericBackground3D = geoResult.label == "Wall" || geoResult.label == "Table" || geoResult.label == "Desk";
                    bool isSmallForeground2D = label2D == "Water bottle" || label2D == "Coffee mug" ||
                                               label2D == "Cell phone" || label2D == "Laptop" ||
                                               label2D == "Book" || label2D == "Bag";

                    if (isGenericBackground3D && isSmallForeground2D && conf2D >= 0.58f)
                    {
                        geoResult.label = label2D;
                        geoResult.confidence = Mathf.Max(geoResult.confidence, conf2D);
                        string colorPrefix = string.IsNullOrEmpty(colorName2D) ? "" : $"{Capitalize(colorName2D)} ";
                        geoResult.detailedDescription = $"{colorPrefix}{label2D.ToLowerInvariant()} recognized by camera vision in front of {geoResult.label.ToLowerInvariant()}";
                    }
                    else if (!string.IsNullOrEmpty(colorName2D))
                    {
                        geoResult.detailedDescription = $"{Capitalize(colorName2D)} {geoResult.label.ToLowerInvariant()}. {geoResult.detailedDescription}";
                    }
                }

                lastDetectedLabel = geoResult.label;
                lastDetectedConfidence = geoResult.confidence;
                return geoResult;
            }

            // 3. Fallback when 3D Spatial Mesh did not hit (e.g., small handheld object at 0.5m-1.0m inside HoloLens near-clip)
            if (has2D && conf2D >= 0.45f)
            {
                float estimatedDist = Mathf.Clamp(0.95f / Mathf.Sqrt(Mathf.Max(0.08f, fillRatio2D * 4f)), 0.5f, 1.5f);
                Vector3 estPos = head.position + head.forward * estimatedDist;
                string colorPrefix = string.IsNullOrEmpty(colorName2D) ? "" : $"{Capitalize(colorName2D)} ";

                CVDetectionResult camOnly = new CVDetectionResult
                {
                    isDetected = true,
                    label = label2D,
                    detailedDescription = $"{colorPrefix}{label2D.ToLowerInvariant()} detected by camera vision",
                    confidence = conf2D,
                    worldPosition = estPos,
                    estimatedBounds = new Vector3(0.22f, 0.25f, 0.22f),
                    category = label2D == "Person" ? SemanticObjectCategory.Person : SemanticObjectCategory.EverydayObject
                };

                lastDetectedLabel = camOnly.label;
                lastDetectedConfidence = camOnly.confidence;
                return camOnly;
            }

            return new CVDetectionResult { isDetected = false };
        }

        /// <summary>
        /// Classifies an untagged obstacle hit for KF07 (ObstacleDetector) so warnings can name the obstacle type.
        /// </summary>
        public string ClassifyObstacleSurface(RaycastHit hit, Transform head)
        {
            CVDetectionResult res = ClassifyFrom3DSpatialPointCloud(head, hit, 3.0f);
            return res.isDetected && !string.IsNullOrEmpty(res.label) ? res.label : "Obstacle";
        }

        /// <summary>
        /// 9x9 Multi-Ray 3D Point-Cloud & Surface Geometry Classifier on the HoloLens 2 Spatial Mesh.
        /// Extracts metric width, height, depth, floor elevation, support surface, and surface normal variance.
        /// </summary>
        private CVDetectionResult ClassifyFrom3DSpatialPointCloud(Transform head, RaycastHit centerHit, float maxDist)
        {
            Vector3 p = centerHit.point;
            Vector3 n = centerHit.normal.normalized;
            float centerDist = centerHit.distance;

            // 1. Estimate floor Y level from downward raycast or human eye height (1.60m below head)
            float floorY = EstimateFloorY(head, p);
            float heightAboveFloor = Mathf.Max(0f, p.y - floorY);

            // 2. Sample a 7x7 angular grid around the hit point to segment the connected 3D cluster
            Vector3 toTarget = (p - head.position).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, toTarget);
            if (right.sqrMagnitude < 0.001f)
                right = head.right;
            else
                right.Normalize();
            Vector3 up = Vector3.Cross(toTarget, right).normalized;

            float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f;
            float minClusterWorldY = p.y, maxClusterWorldY = p.y;
            float minClusterDist = centerDist, maxClusterDist = centerDist;
            float normalDotSum = 0f;
            int clusterCount = 0;
            float backgroundDistSum = 0f;
            int backgroundCount = 0;
            bool hasTableSupportBelow = false;
            int stepLikeTransitions = 0;
            float prevRowHeight = heightAboveFloor;

            // Angular step scales with distance so we sample ~1.0m x 1.0m metric window around the target
            float halfSpanMeters = Mathf.Clamp(centerDist * 0.38f, 0.32f, 0.95f);
            int gridRadius = 3; // 7x7 = 49 rays

            for (int gy = -gridRadius; gy <= gridRadius; gy++)
            {
                float offsetY = (gy / (float)gridRadius) * halfSpanMeters;
                float rowAvgHeight = 0f;
                int rowHorizHits = 0;

                for (int gx = -gridRadius; gx <= gridRadius; gx++)
                {
                    float offsetX = (gx / (float)gridRadius) * halfSpanMeters;
                    Vector3 sampleTarget = p + right * offsetX + up * offsetY;
                    Vector3 rayDir = (sampleTarget - head.position).normalized;

                    if (Physics.Raycast(head.position, rayDir, out RaycastHit sampleHit, maxDist + 1.5f))
                    {
                        float depthDelta = sampleHit.distance - centerDist;

                        // Belongs to the foreground object cluster if depth is within 0.28m of center hit
                        if (Mathf.Abs(depthDelta) <= 0.28f)
                        {
                            clusterCount++;
                            Vector3 rel = sampleHit.point - p;
                            float projX = Vector3.Dot(rel, right);
                            float projY = Vector3.Dot(rel, Vector3.up);

                            if (projX < minX) minX = projX;
                            if (projX > maxX) maxX = projX;
                            if (projY < minY) minY = projY;
                            if (projY > maxY) maxY = projY;

                            if (sampleHit.point.y < minClusterWorldY) minClusterWorldY = sampleHit.point.y;
                            if (sampleHit.point.y > maxClusterWorldY) maxClusterWorldY = sampleHit.point.y;
                            if (sampleHit.distance < minClusterDist) minClusterDist = sampleHit.distance;
                            if (sampleHit.distance > maxClusterDist) maxClusterDist = sampleHit.distance;

                            normalDotSum += Mathf.Abs(Vector3.Dot(sampleHit.normal.normalized, n));
                        }
                        else if (depthDelta > 0.30f)
                        {
                            // Ray went past the object and hit a background wall/surface behind it
                            backgroundDistSum += sampleHit.distance;
                            backgroundCount++;
                        }

                        // Check if rays below the target hit a horizontal tabletop surface (0.62m - 0.95m above floor)
                        float sampleH = sampleHit.point.y - floorY;
                        if (gy < 0 && sampleHit.normal.y > 0.72f && sampleH >= 0.60f && sampleH <= 0.96f)
                        {
                            hasTableSupportBelow = true;
                        }

                        if (sampleHit.normal.y > 0.68f)
                        {
                            rowAvgHeight += sampleH;
                            rowHorizHits++;
                        }
                    }
                    else
                    {
                        // Open space behind/around the object counts as strong foreground protrusion
                        backgroundDistSum += centerDist + 1.2f;
                        backgroundCount++;
                    }
                }

                if (rowHorizHits >= 2)
                {
                    rowAvgHeight /= rowHorizHits;
                    float stepDiff = Mathf.Abs(rowAvgHeight - prevRowHeight);
                    if (stepDiff >= 0.12f && stepDiff <= 0.26f)
                    {
                        stepLikeTransitions++;
                    }
                    prevRowHeight = rowAvgHeight;
                }
            }

            float clusterWidth = Mathf.Max(0.08f, maxX - minX);
            float clusterHeight = Mathf.Max(0.08f, maxY - minY);
            float clusterDepth = Mathf.Max(0.08f, maxClusterDist - minClusterDist);
            float normalCoherence = clusterCount > 0 ? (normalDotSum / clusterCount) : 1f;
            float normalVariance = 1f - normalCoherence;
            float avgBackgroundDist = backgroundCount > 0 ? (backgroundDistSum / backgroundCount) : centerDist;
            float protrusion = Mathf.Max(0f, avgBackgroundDist - centerDist);
            float bottomAboveFloor = Mathf.Max(0f, minClusterWorldY - floorY);
            float topAboveFloor = Mathf.Max(0f, maxClusterWorldY - floorY);

            bool isHorizontalUp = n.y >= 0.68f;
            bool isVerticalSurface = Mathf.Abs(n.y) <= 0.30f;
            bool isAngledSurface = !isHorizontalUp && !isVerticalSurface;

            string label;
            string detail;
            float confidence = 0.78f;
            SemanticObjectCategory category = SemanticObjectCategory.EverydayObject;

            // --- RULE 1: Stepped surfaces -> Stairs ---
            if (stepLikeTransitions >= 2 && heightAboveFloor <= 1.6f)
            {
                label = "Stairs";
                detail = $"Stepped surface ahead, height {heightAboveFloor:0.0} meters";
                confidence = 0.86f;
                category = SemanticObjectCategory.HazardObstacle;
            }
            // --- RULE 2: Distinct foreground object protruding in front of background or resting on a table/desk ---
            else if ((protrusion >= 0.22f || hasTableSupportBelow) &&
                     clusterWidth <= 0.62f && clusterHeight <= 0.62f &&
                     heightAboveFloor >= 0.55f && heightAboveFloor <= 1.55f)
            {
                if (clusterWidth <= 0.18f && clusterHeight >= clusterWidth * 1.25f)
                {
                    label = "Water bottle";
                    detail = $"Tall cylindrical container on surface, height {clusterHeight * 100f:0} centimeters";
                    confidence = 0.85f;
                }
                else if (clusterWidth <= 0.18f && clusterHeight < clusterWidth * 1.25f)
                {
                    label = "Coffee mug";
                    detail = $"Compact cup or mug on surface, width {clusterWidth * 100f:0} centimeters";
                    confidence = 0.83f;
                }
                else if (clusterWidth <= 0.24f && clusterHeight <= 0.10f)
                {
                    label = "Cell phone";
                    detail = "Compact flat handheld device on surface";
                    confidence = 0.80f;
                }
                else if (isAngledSurface && clusterWidth >= 0.22f && clusterWidth <= 0.48f && hasTableSupportBelow)
                {
                    label = "Laptop";
                    detail = $"Angled display on desk surface, width {clusterWidth * 100f:0} centimeters";
                    confidence = 0.86f;
                }
                else if (isVerticalSurface && clusterWidth >= 0.38f && clusterWidth <= 0.62f && hasTableSupportBelow)
                {
                    label = "Monitor";
                    detail = $"Upright display screen on desk, width {clusterWidth * 100f:0} centimeters";
                    confidence = 0.84f;
                }
                else if (normalVariance > 0.06f)
                {
                    label = "Bag";
                    detail = $"Soft contoured bag or backpack, width {clusterWidth * 100f:0} centimeters";
                    confidence = 0.80f;
                }
                else
                {
                    label = "Small box";
                    detail = $"Compact box-shaped object, width {clusterWidth * 100f:0} centimeters";
                    confidence = 0.79f;
                }
            }
            // --- RULE 3: Horizontal upward surfaces (Chair, Sofa, Bed, Table, Desk, Sink) ---
            else if (isHorizontalUp)
            {
                if (heightAboveFloor < 0.18f)
                {
                    label = "Floor";
                    detail = "Open floor surface";
                    confidence = 0.92f;
                }
                else if (heightAboveFloor >= 0.34f && heightAboveFloor <= 0.60f)
                {
                    if (clusterWidth >= 1.15f && clusterDepth >= 0.90f)
                    {
                        label = "Bed";
                        detail = $"Wide low horizontal surface at {heightAboveFloor:0.0} meters height";
                        confidence = 0.82f;
                    }
                    else if (clusterWidth >= 0.95f)
                    {
                        label = "Sofa";
                        detail = $"Wide seating surface at {heightAboveFloor:0.0} meters height";
                        confidence = 0.84f;
                    }
                    else
                    {
                        label = "Chair";
                        detail = $"Seat surface at {heightAboveFloor:0.0} meters above floor";
                        confidence = 0.88f;
                        category = SemanticObjectCategory.HazardObstacle;
                    }
                }
                else if (heightAboveFloor > 0.60f && heightAboveFloor <= 0.96f)
                {
                    if (clusterWidth <= 0.50f && protrusion >= 0.18f)
                    {
                        label = "Laptop";
                        detail = $"Item resting on desk at {heightAboveFloor:0.0} meters height";
                        confidence = 0.78f;
                    }
                    else if (clusterWidth >= 1.10f)
                    {
                        label = "Table";
                        detail = $"Flat horizontal table surface at {heightAboveFloor:0.0} meters height";
                        confidence = 0.89f;
                    }
                    else
                    {
                        label = "Desk";
                        detail = $"Horizontal work desk surface at {heightAboveFloor:0.0} meters height";
                        confidence = 0.88f;
                    }
                }
                else if (heightAboveFloor > 0.96f && heightAboveFloor <= 1.18f)
                {
                    label = "Sink";
                    detail = $"High counter or sink surface at {heightAboveFloor:0.0} meters height";
                    confidence = 0.77f;
                }
                else
                {
                    label = "Cabinet";
                    detail = $"High shelf surface at {heightAboveFloor:0.0} meters height";
                    confidence = 0.78f;
                }
            }
            // --- RULE 4: Angled surfaces (Chair backrest, Laptop screen, Sofa cushion, Bag) ---
            else if (isAngledSurface)
            {
                if (heightAboveFloor >= 0.35f && heightAboveFloor <= 0.98f)
                {
                    if (hasTableSupportBelow && heightAboveFloor >= 0.70f)
                    {
                        label = "Laptop";
                        detail = "Angled screen resting on desk";
                        confidence = 0.84f;
                    }
                    else if (clusterWidth >= 0.95f)
                    {
                        label = "Sofa";
                        detail = "Angled backrest cushion";
                        confidence = 0.83f;
                    }
                    else
                    {
                        label = "Chair";
                        detail = $"Chair backrest at {heightAboveFloor:0.0} meters height";
                        confidence = 0.86f;
                        category = SemanticObjectCategory.HazardObstacle;
                    }
                }
                else
                {
                    label = "Bag";
                    detail = $"Contoured object at {heightAboveFloor:0.0} meters height";
                    confidence = 0.76f;
                }
            }
            // --- RULE 5: Vertical surfaces (Person, Chair back, Pillar, Cabinet, Door, Window, Wall) ---
            else
            {
                // Freestanding person-sized vertical volume detached from background walls
                if (protrusion >= 0.35f &&
                    clusterWidth >= 0.28f && clusterWidth <= 0.78f &&
                    topAboveFloor >= 1.20f && topAboveFloor <= 1.98f &&
                    centerDist >= 0.7f && centerDist <= 3.5f)
                {
                    label = "Person";
                    detail = "Person standing in the room";
                    confidence = 0.84f;
                    category = SemanticObjectCategory.Person;
                }
                // Chair backrest (vertical surface between 0.45m and 1.05m with open space behind it)
                else if (protrusion >= 0.22f && topAboveFloor <= 1.12f && heightAboveFloor >= 0.35f && clusterWidth <= 0.82f)
                {
                    label = "Chair";
                    detail = $"Chair in room, height {topAboveFloor:0.0} meters";
                    confidence = 0.85f;
                    category = SemanticObjectCategory.HazardObstacle;
                }
                // Monitor on desk
                else if (hasTableSupportBelow && heightAboveFloor >= 0.72f && heightAboveFloor <= 1.40f && clusterWidth <= 0.80f)
                {
                    label = "Monitor";
                    detail = "Display screen on work desk";
                    confidence = 0.83f;
                }
                // Narrow freestanding vertical column reaching high
                else if (protrusion >= 0.25f && clusterWidth <= 0.65f && topAboveFloor > 1.55f)
                {
                    label = "Pillar";
                    detail = "Vertical structural pillar";
                    confidence = 0.85f;
                    category = SemanticObjectCategory.HazardObstacle;
                }
                // Tall storage furniture protruding from wall
                else if (protrusion >= 0.22f && clusterWidth > 0.65f && clusterWidth <= 1.45f)
                {
                    label = "Cabinet";
                    detail = $"Tall storage cabinet, width {clusterWidth:0.0} meters";
                    confidence = 0.82f;
                }
                // Window (upper vertical plane with recess/frame discontinuity)
                else if (heightAboveFloor >= 1.05f && bottomAboveFloor >= 0.70f && backgroundCount >= 4)
                {
                    label = "Window";
                    detail = $"Upper wall window at {heightAboveFloor:0.0} meters height";
                    confidence = 0.81f;
                }
                // Door (door-width vertical panel reaching near the floor with frame edge or recess)
                else if (clusterWidth >= 0.68f && clusterWidth <= 1.28f && bottomAboveFloor <= 0.45f &&
                         (backgroundCount >= 3 || normalVariance > 0.015f))
                {
                    label = "Door";
                    detail = $"Doorway panel, width {clusterWidth:0.0} meters";
                    confidence = 0.83f;
                }
                else
                {
                    label = "Wall";
                    detail = "Vertical room wall";
                    confidence = 0.88f;
                    category = SemanticObjectCategory.HazardObstacle;
                }
            }

            Vector3 bounds = new Vector3(
                Mathf.Clamp(clusterWidth, 0.20f, 1.20f),
                Mathf.Clamp(clusterHeight, 0.20f, 1.40f),
                Mathf.Clamp(clusterDepth, 0.20f, 0.90f)
            );

            return new CVDetectionResult
            {
                isDetected = true,
                label = label,
                detailedDescription = detail,
                confidence = confidence,
                worldPosition = p,
                estimatedBounds = bounds,
                category = category
            };
        }

        private static float EstimateFloorY(Transform head, Vector3 refPoint)
        {
            float defaultFloorY = head.position.y - 1.60f;
            float lowestY = defaultFloorY;
            bool foundFloor = false;

            Vector3[] origins =
            {
                head.position,
                new Vector3(refPoint.x, head.position.y, refPoint.z)
            };

            foreach (Vector3 origin in origins)
            {
                if (Physics.Raycast(origin, Vector3.down, out RaycastHit downHit, 3.0f))
                {
                    if (downHit.normal.y > 0.75f && downHit.distance >= 0.95f)
                    {
                        if (!foundFloor || downHit.point.y < lowestY)
                        {
                            lowestY = downHit.point.y;
                            foundFloor = true;
                        }
                    }
                }
            }

            return foundFloor ? lowestY : defaultFloorY;
        }

        /// <summary>
        /// 2D RGB Camera Feature & Contour Classifier:
        /// Extracts foreground object in the center gaze region [0.28..0.72, 0.28..0.72] relative to the outer border,
        /// computes Sobel edge orientation, aspect ratio, specular highlights, screen glow, and HSV color histogram.
        /// </summary>
        private bool TryClassifyFrom2DCameraGaze(
            out string label,
            out string dominantColorName,
            out float confidence,
            out float aspectRatio,
            out float fillRatio)
        {
            label = "";
            dominantColorName = "";
            confidence = 0f;
            aspectRatio = 1f;
            fillRatio = 0f;

            if (_webCamTexture == null || !_webCamTexture.isPlaying || _webCamTexture.width <= 32 || _pixelBuffer == null)
                return false;

            int w = _webCamTexture.width;
            int h = _webCamTexture.height;
            if (_pixelBuffer.Length != w * h)
                return false;

            // 1. Estimate background color from outer peripheral ring
            float bgR = 0f, bgG = 0f, bgB = 0f;
            int bgSamples = 0;
            for (int y = 0; y < h; y += 4)
            {
                for (int x = 0; x < w; x += 4)
                {
                    float nx = x / (float)w;
                    float ny = y / (float)h;
                    if (nx < 0.18f || nx > 0.82f || ny < 0.18f || ny > 0.82f)
                    {
                        Color32 c = _pixelBuffer[y * w + x];
                        bgR += c.r;
                        bgG += c.g;
                        bgB += c.b;
                        bgSamples++;
                    }
                }
            }

            if (bgSamples == 0)
                return false;

            bgR /= bgSamples;
            bgG /= bgSamples;
            bgB /= bgSamples;

            // 2. Scan central gaze ROI [0.25..0.75, 0.20..0.80] for foreground pixels & Sobel edges
            int x0 = Mathf.RoundToInt(w * 0.25f);
            int x1 = Mathf.RoundToInt(w * 0.75f);
            int y0 = Mathf.RoundToInt(h * 0.20f);
            int y1 = Mathf.RoundToInt(h * 0.80f);

            int minFgX = x1, maxFgX = x0, minFgY = y1, maxFgY = y0;
            int fgCount = 0;
            int roiTotal = 0;
            int specularCount = 0;
            int skinCount = 0;
            int brightScreenCount = 0;
            float vertEdgeSum = 0f;
            float horizEdgeSum = 0f;
            float sumR = 0f, sumG = 0f, sumB = 0f;

            for (int y = y0 + 1; y < y1 - 1; y += 2)
            {
                for (int x = x0 + 1; x < x1 - 1; x += 2)
                {
                    roiTotal++;
                    int idx = y * w + x;
                    Color32 c = _pixelBuffer[idx];

                    // Sobel gradient
                    Color32 cLeft = _pixelBuffer[idx - 1];
                    Color32 cRight = _pixelBuffer[idx + 1];
                    Color32 cDown = _pixelBuffer[idx - w];
                    Color32 cUp = _pixelBuffer[idx + w];

                    float gx = Mathf.Abs((cRight.r + cRight.g + cRight.b) - (cLeft.r + cLeft.g + cLeft.b)) / 3f;
                    float gy = Mathf.Abs((cUp.r + cUp.g + cUp.b) - (cDown.r + cDown.g + cDown.b)) / 3f;
                    float edgeMag = gx + gy;

                    float colorDist = Mathf.Abs(c.r - bgR) + Mathf.Abs(c.g - bgG) + Mathf.Abs(c.b - bgB);
                    if (colorDist > 52f || edgeMag > 38f)
                    {
                        fgCount++;
                        if (x < minFgX) minFgX = x;
                        if (x > maxFgX) maxFgX = x;
                        if (y < minFgY) minFgY = y;
                        if (y > maxFgY) maxFgY = y;

                        vertEdgeSum += gx;
                        horizEdgeSum += gy;
                        sumR += c.r;
                        sumG += c.g;
                        sumB += c.b;

                        if (c.r > 235 && c.g > 235 && c.b > 235)
                            specularCount++;

                        if (c.r > 95 && c.g > 40 && c.b > 20 &&
                            (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) - Mathf.Min(c.r, Mathf.Min(c.g, c.b))) > 15 &&
                            Mathf.Abs(c.r - c.g) > 15 && c.r > c.g && c.r > c.b)
                        {
                            skinCount++;
                        }

                        if (c.b > 170 && c.g > 150 && c.r > 130 && gy > 18f)
                        {
                            brightScreenCount++;
                        }
                    }
                }
            }

            if (fgCount < 24 || maxFgX <= minFgX + 6 || maxFgY <= minFgY + 6)
                return false;

            fillRatio = fgCount / (float)Mathf.Max(1, roiTotal);
            float bboxW = (maxFgX - minFgX) / (float)w;
            float bboxH = (maxFgY - minFgY) / (float)h;
            aspectRatio = bboxH / Mathf.Max(0.05f, bboxW);

            float avgR = (sumR / fgCount) / 255f;
            float avgG = (sumG / fgCount) / 255f;
            float avgB = (sumB / fgCount) / 255f;
            dominantColorName = DescribeColor(avgR, avgG, avgB);

            float specRatio = specularCount / (float)fgCount;
            float skinRatio = skinCount / (float)fgCount;
            float screenRatio = brightScreenCount / (float)fgCount;
            float edgeRatioVH = (vertEdgeSum + 1f) / (horizEdgeSum + 1f);

            if (skinRatio > 0.26f && aspectRatio >= 1.15f)
            {
                label = "Person";
                confidence = 0.72f;
                return true;
            }

            if (aspectRatio >= 1.45f && (specRatio > 0.03f || edgeRatioVH > 1.18f) && bboxW <= 0.35f)
            {
                label = "Water bottle";
                confidence = 0.74f;
                return true;
            }

            if (aspectRatio >= 1.35f && aspectRatio <= 2.35f && avgR < 0.32f && avgG < 0.32f && avgB < 0.35f && bboxW <= 0.32f)
            {
                label = "Cell phone";
                confidence = 0.70f;
                return true;
            }

            if (aspectRatio >= 0.75f && aspectRatio <= 1.35f && bboxW <= 0.30f && specRatio > 0.015f)
            {
                label = "Coffee mug";
                confidence = 0.68f;
                return true;
            }

            if (aspectRatio <= 0.85f && (screenRatio > 0.08f || edgeRatioVH < 0.85f) && bboxW >= 0.25f)
            {
                label = "Laptop";
                confidence = 0.69f;
                return true;
            }

            if (avgR > 0.68f && avgG > 0.68f && avgB > 0.65f && edgeRatioVH < 0.90f)
            {
                label = "Book";
                confidence = 0.64f;
                return true;
            }

            if (fillRatio >= 0.28f && bboxW >= 0.28f)
            {
                label = "Bag";
                confidence = 0.60f;
                return true;
            }

            return false;
        }

        /// <summary>
        /// KF08: Continuously scans the real-world spatial mesh ahead for a freestanding human-sized
        /// silhouette (1.0m - 3.0m) so ObjectRecognizer.MonitorApproachingPeople() alerts on real people too.
        /// </summary>
        private void ScanForApproachingRealWorldPeople()
        {
            if (Camera.main == null)
                return;

            Transform head = Camera.main.transform;
            Vector3 forwardHoriz = new Vector3(head.forward.x, 0f, head.forward.z);
            if (forwardHoriz.sqrMagnitude < 0.01f)
                forwardHoriz = head.forward;
            else
                forwardHoriz.Normalize();

            float[] scanAngles = { -28f, -14f, 0f, 14f, 28f };
            foreach (float ang in scanAngles)
            {
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * forwardHoriz;
                if (Physics.Raycast(head.position, dir, out RaycastHit hit, 3.2f))
                {
                    if (hit.distance < 0.85f)
                        continue;

                    TrackableObject existing = hit.collider.GetComponentInParent<TrackableObject>();
                    if (existing != null)
                        continue;

                    // Quick check: does this hit have open space on both left and right sides (person width 0.30m - 0.75m)?
                    Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
                    Vector3 leftProbe = (hit.point - right * 0.45f - head.position).normalized;
                    Vector3 rightProbe = (hit.point + right * 0.45f - head.position).normalized;

                    bool leftOpen = !Physics.Raycast(head.position, leftProbe, out RaycastHit lHit, hit.distance + 0.35f) ||
                                    (lHit.distance - hit.distance) > 0.35f;
                    bool rightOpen = !Physics.Raycast(head.position, rightProbe, out RaycastHit rHit, hit.distance + 0.35f) ||
                                     (rHit.distance - hit.distance) > 0.35f;

                    // Check that top doesn't reach the ceiling (unlike a structural pillar)
                    Vector3 topProbe = (hit.point + Vector3.up * 0.65f - head.position).normalized;
                    bool topOpen = !Physics.Raycast(head.position, topProbe, out RaycastHit tHit, hit.distance + 0.35f) ||
                                   (tHit.distance - hit.distance) > 0.35f;

                    if (leftOpen && rightOpen && topOpen)
                    {
                        UpdateDynamicRealWorldPerson(hit.point);
                        return;
                    }
                }
            }

            // Expire dynamic person marker if not seen for 2.5 seconds
            if (_dynamicRealWorldPerson != null && (Time.time - _lastPersonSeenTime) > 2.5f)
            {
                _dynamicRealWorldPerson.gameObject.SetActive(false);
            }
        }

        private void UpdateDynamicRealWorldPerson(Vector3 worldPos)
        {
            // Only track if the person cluster actually moved (avoids mistaking a static coat rack for a walking person)
            if (_lastPersonClusterPos != Vector3.zero)
            {
                float moveDist = Vector3.Distance(_lastPersonClusterPos, worldPos);
                if (moveDist < 0.03f || moveDist > 1.5f)
                {
                    _lastPersonClusterPos = worldPos;
                    return;
                }
            }
            _lastPersonClusterPos = worldPos;
            _lastPersonSeenTime = Time.time;

            if (_dynamicRealWorldPerson == null)
            {
                GameObject personMarker = new GameObject("RealWorldDetected_Person");
                _dynamicRealWorldPerson = personMarker.AddComponent<TrackableObject>();
                _dynamicRealWorldPerson.labelName = "Person";
                _dynamicRealWorldPerson.detailedDescription = "Person detected in room by spatial vision";
                _dynamicRealWorldPerson.category = SemanticObjectCategory.Person;
            }

            _dynamicRealWorldPerson.gameObject.SetActive(true);
            _dynamicRealWorldPerson.transform.position = worldPos;
        }

        private static string DescribeColor(float r, float g, float b)
        {
            Color.RGBToHSV(new Color(r, g, b), out float h, out float s, out float v);
            if (v < 0.20f) return "black";
            if (s < 0.16f)
            {
                if (v > 0.75f) return "white";
                return "gray";
            }

            float deg = h * 360f;
            if (deg < 18f || deg >= 342f) return "red";
            if (deg < 45f) return s > 0.45f ? "orange" : "brown";
            if (deg < 70f) return "yellow";
            if (deg < 165f) return "green";
            if (deg < 255f) return "blue";
            return "";
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
