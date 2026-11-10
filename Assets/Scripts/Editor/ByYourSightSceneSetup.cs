#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using ByYourSight.Audio;
using ByYourSight.Perception;
using ByYourSight.Safety;
using ByYourSight.Navigation;
using ByYourSight.InputSystem;

namespace ByYourSight.EditorTools
{
    public class ByYourSightBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            ByYourSightSceneSetup.EnsureUwpCapabilitiesAndServerHostFile();
        }
    }

    [InitializeOnLoad]
    public static class ByYourSightSceneSetup
    {
        static ByYourSightSceneSetup()
        {
            EditorApplication.delayCall += () =>
            {
                EnsureUwpCapabilitiesAndServerHostFile();
                AutoSetupIfNeeded();
            };
            EditorSceneManager.sceneOpened += (scene, mode) => AutoSetupIfNeeded();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    if (GameObject.Find("ByYourSight_CoreSystem") == null && GameObject.Find("ByYourSight_PrototypeEnvironment") == null)
                    {
                        BuildPrototypeScene();
                    }
                }
            };
        }

        /// <summary>
        /// Ensures HoloLens 2 UWP builds have local network + internet + sensor capabilities enabled,
        /// and embeds the build laptop's local IPv4 addresses into Assets/Resources/DetectionServerHosts.txt
        /// so standalone UWP builds on HoloLens 2 automatically connect to detection_server.py on the laptop
        /// instead of failing on 127.0.0.1:8765.
        /// </summary>
        public static void EnsureUwpCapabilitiesAndServerHostFile()
        {
            try
            {
                PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.InternetClient, true);
                PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.InternetClientServer, true);
                PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.PrivateNetworkClientServer, true);
                PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.Microphone, true);
                PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.SpatialPerception, true);
                PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.WebCam, true);

                List<string> ips = new List<string>();
                foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        string s = ip.ToString();
                        if (!ips.Contains(s))
                            ips.Add(s);
                    }
                }

                if (ips.Count > 0)
                {
                    string resourcesDir = Path.Combine(Application.dataPath, "Resources");
                    if (!Directory.Exists(resourcesDir))
                        Directory.CreateDirectory(resourcesDir);

                    string hostFile = Path.Combine(resourcesDir, "DetectionServerHosts.txt");
                    string newContent = string.Join("\n", ips);
                    string oldContent = File.Exists(hostFile) ? File.ReadAllText(hostFile) : "";
                    if (oldContent != newContent)
                    {
                        File.WriteAllText(hostFile, newContent);
                        AssetDatabase.Refresh();
                        Debug.Log($"[ByYourSight] Embedded laptop IP(s) for HoloLens UWP detection server: {string.Join(", ", ips)}");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ByYourSight] Could not auto-update DetectionServerHosts.txt: " + e.Message);
            }
        }

        private static void AutoSetupIfNeeded()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            // Automatically populate the prototype scene if it hasn't been set up yet
            if (GameObject.Find("ByYourSight_CoreSystem") == null && GameObject.Find("ByYourSight_PrototypeEnvironment") == null)
            {
                BuildPrototypeScene();
            }
        }

        [MenuItem("By Your Sight/Setup Demo Prototype Scene (KF01 - KF08)", false, 1)]
        [MenuItem("Tools/By Your Sight - Setup Prototype Scene", false, 1)]
        [MenuItem("GameObject/By Your Sight - Setup Prototype Scene", false, 10)]
        public static void BuildPrototypeScene()
        {
            // Clean up previous prototype objects if any exist
            GameObject existingRoot = GameObject.Find("ByYourSight_PrototypeEnvironment");
            if (existingRoot != null)
            {
                Object.DestroyImmediate(existingRoot);
            }

            GameObject existingSystem = GameObject.Find("ByYourSight_CoreSystem");
            if (existingSystem != null)
            {
                Object.DestroyImmediate(existingSystem);
            }

            GameObject existingMesh = GameObject.Find("HoloLens_RealWorldSpatialMesh");
            if (existingMesh != null)
            {
                Object.DestroyImmediate(existingMesh);
            }

            // 1. Position Main Camera at human eye level (1.6m)
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                GameObject camObj = new GameObject("Main Camera");
                camObj.tag = "MainCamera";
                mainCam = camObj.AddComponent<Camera>();
                camObj.AddComponent<AudioListener>();
            }
            mainCam.transform.position = new Vector3(0f, 1.6f, -3.5f);
            mainCam.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
            mainCam.clearFlags = CameraClearFlags.SolidColor;
            mainCam.backgroundColor = new Color(0f, 0f, 0f, 0f); // Pure transparent black (0,0,0,0) for HoloLens 2 additive lens!

            // 2. Create Indoor Room Environment Root
            GameObject envRoot = new GameObject("ByYourSight_PrototypeEnvironment");

            // Indoor Floor
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Indoor_Floor";
            floor.transform.SetParent(envRoot.transform);
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(1.5f, 1f, 1.5f);
            SetObjectColor(floor, new Color(0.18f, 0.2f, 0.24f));
            // Remove floor collider from horizontal obstacle raycasts
            Object.DestroyImmediate(floor.GetComponent<Collider>());

            // 3. Everyday Objects for KF01 ("Ini apa?") & KF03 ("Cari...")
            // Botol minum di depan pengguna
            CreateTrackablePrimitive(
                PrimitiveType.Cylinder,
                "Water_Bottle",
                envRoot.transform,
                new Vector3(0f, 1.45f, -1.5f),
                new Vector3(0.22f, 0.32f, 0.22f),
                new Color(0.2f, 0.7f, 1f),
                "Botol minum",
                "Botol air minum biru 600 mililiter",
                SemanticObjectCategory.EverydayObject
            );

            // Cangkir di kanan
            CreateTrackablePrimitive(
                PrimitiveType.Cylinder,
                "Coffee_Mug",
                envRoot.transform,
                new Vector3(1.8f, 1.35f, -1.8f),
                new Vector3(0.24f, 0.2f, 0.24f),
                new Color(0.85f, 0.55f, 0.25f),
                "Cangkir",
                "Cangkir keramik hangat di sebelah kanan",
                SemanticObjectCategory.EverydayObject
            );

            // Kotak di kiri
            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Item_On_Left",
                envRoot.transform,
                new Vector3(-1.5f, 1.45f, -1.8f),
                new Vector3(0.28f, 0.28f, 0.28f),
                new Color(0.75f, 0.75f, 0.8f),
                "Kotak",
                "Kotak penyimpanan kecil di sebelah kiri",
                SemanticObjectCategory.EverydayObject
            );

            // 4. Hazard Obstacles for KF07 (P0 Danger Alarm: "Awas! Tiang, 1 meter, di depan")
            CreateTrackablePrimitive(
                PrimitiveType.Cylinder,
                "Support_Pillar",
                envRoot.transform,
                new Vector3(-0.9f, 1.2f, 0.5f),
                new Vector3(0.45f, 1.2f, 0.45f),
                new Color(0.85f, 0.3f, 0.25f),
                "Tiang",
                "Tiang penyangga beton di tengah ruangan",
                SemanticObjectCategory.HazardObstacle
            );

            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Chair_Obstacle",
                envRoot.transform,
                new Vector3(1.2f, 0.6f, 0.8f),
                new Vector3(0.65f, 0.6f, 0.65f),
                new Color(0.7f, 0.4f, 0.2f),
                "Kursi",
                "Kursi kayu menghalangi jalur jalan",
                SemanticObjectCategory.HazardObstacle
            );

            // 5. Person Nearby for KF08 (P2 Social Info: "Orang mendekat, di kanan")
            CreateTrackablePrimitive(
                PrimitiveType.Capsule,
                "Nearby_Person",
                envRoot.transform,
                new Vector3(2.4f, 1.1f, 1.2f),
                new Vector3(0.55f, 1.0f, 0.55f),
                new Color(0.4f, 0.85f, 0.55f),
                "Orang",
                "Seseorang berada di dekat Anda di dalam ruangan",
                SemanticObjectCategory.Person
            );

            // 6. Destinations & Waypoints for KF05 ("Antar ke Pintu" & "Antar ke Meja")
            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Exit_Door",
                envRoot.transform,
                new Vector3(0f, 1.3f, 4.2f),
                new Vector3(1.2f, 2.2f, 0.2f),
                new Color(0.25f, 0.85f, 0.9f),
                "Pintu",
                "Pintu keluar ruangan di dinding depan",
                SemanticObjectCategory.EverydayObject
            );

            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Work_Desk",
                envRoot.transform,
                new Vector3(2.8f, 0.8f, 2.5f),
                new Vector3(1.4f, 0.8f, 0.9f),
                new Color(0.55f, 0.45f, 0.3f),
                "Meja kerja",
                "Meja kerja di sudut kanan depan",
                SemanticObjectCategory.EverydayObject
            );

            // Waypoint Nodes
            Transform wpDoor1 = CreateWaypointNode("WP_Door_1", envRoot.transform, new Vector3(0.3f, 1.2f, -0.5f));
            Transform wpDoor2 = CreateWaypointNode("WP_Door_2", envRoot.transform, new Vector3(0.1f, 1.2f, 3.2f));

            Transform wpDesk1 = CreateWaypointNode("WP_Desk_1", envRoot.transform, new Vector3(0.6f, 1.2f, -0.5f));
            Transform wpDesk2 = CreateWaypointNode("WP_Desk_2", envRoot.transform, new Vector3(2.4f, 1.2f, 1.8f));

            // 7. Create Core System GameObject & Attach All By Your Sight Components + Real HoloLens Spatial Mesh Scanner
            GameObject coreSys = new GameObject("ByYourSight_CoreSystem");
            coreSys.AddComponent<PriorityAudioManager>();
            ObjectRecognizer recognizer = coreSys.AddComponent<ObjectRecognizer>();
            ObstacleDetector obstacleDetector = coreSys.AddComponent<ObstacleDetector>();
            SpatialBeaconAndNavigator navigator = coreSys.AddComponent<SpatialBeaconAndNavigator>();
            VoiceAndGestureController controller = coreSys.AddComponent<VoiceAndGestureController>();
            coreSys.AddComponent<RealWorldObjectDetector>(); // Opsi 2: camera object detection (Tools/ObjectDetectionServer)

            // Attach ARSession & ARMeshManager so HoloLens 2 scans real physical walls/obstacles in the room
            if (Object.FindObjectOfType<ARSession>() == null)
            {
                GameObject arSessionObj = new GameObject("HoloLens_ARSession");
                arSessionObj.transform.SetParent(coreSys.transform);
                arSessionObj.AddComponent<ARSession>();
                arSessionObj.AddComponent<ARInputManager>();
            }

            // AR Foundation 5 requires ARMeshManager (and the tracked camera) to live under an XR Origin,
            // otherwise no Spatial Mesh chunks are spawned on the real HoloLens 2 / Holographic Remoting.
            XROrigin xrOrigin = Object.FindObjectOfType<XROrigin>();
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
            mainCam.transform.SetParent(xrOrigin.CameraFloorOffsetObject.transform, true);
            xrOrigin.Camera = mainCam;

            GameObject meshManagerObj = new GameObject("HoloLens_RealWorldSpatialMesh");
            meshManagerObj.transform.SetParent(xrOrigin.transform, false);
            ARMeshManager meshManager = meshManagerObj.AddComponent<ARMeshManager>();

            // Create a runtime Spatial Mesh template with MeshFilter + MeshCollider for real-world obstacle raycasting
            GameObject meshTemplate = new GameObject("SpatialMeshChunkPrefab");
            meshTemplate.transform.SetParent(coreSys.transform);
            MeshFilter mf = meshTemplate.AddComponent<MeshFilter>();
            meshTemplate.AddComponent<MeshCollider>();
            meshManager.meshPrefab = mf;

            navigator.destinations = new List<NavigationDestination>
            {
                new NavigationDestination
                {
                    destinationName = "Pintu",
                    waypoints = new List<Transform> { wpDoor1, wpDoor2 }
                },
                new NavigationDestination
                {
                    destinationName = "Meja",
                    waypoints = new List<Transform> { wpDesk1, wpDesk2 }
                }
            };

            controller.objectRecognizer = recognizer;
            controller.navigator = navigator;
            controller.obstacleDetector = obstacleDetector;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            // On Windows (HoloLens 2 lab PC), default to Real-World HoloLens Mode (hide fake MacBook objects!)
            controller.isRealHoloLensRoomMode = true;
            envRoot.SetActive(false);
#endif

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = coreSys;

            Debug.Log("=== [By Your Sight] Prototipe Bahasa Indonesia (KF01 - KF08) siap! Tekan PLAY (▶) untuk menguji. ===");
        }

        private static GameObject CreateTrackablePrimitive(
            PrimitiveType type,
            string objName,
            Transform parent,
            Vector3 pos,
            Vector3 scale,
            Color color,
            string labelId,
            string detailId,
            SemanticObjectCategory category)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = objName;
            go.transform.SetParent(parent);
            go.transform.position = pos;
            go.transform.localScale = scale;
            SetObjectColor(go, color);

            TrackableObject trackable = go.AddComponent<TrackableObject>();
            trackable.labelName = labelId;
            trackable.detailedDescription = detailId;
            trackable.category = category;
            return go;
        }

        private static Transform CreateWaypointNode(string name, Transform parent, Vector3 position)
        {
            GameObject wp = new GameObject(name);
            wp.transform.SetParent(parent);
            wp.transform.position = position;
            return wp.transform;
        }

        private static void SetObjectColor(GameObject go, Color color)
        {
            Renderer r = go.GetComponent<Renderer>();
            if (r == null) return;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            Material mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            r.sharedMaterial = mat;
        }
    }
}
#endif
