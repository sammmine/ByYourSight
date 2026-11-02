#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using ByYourSight.Audio;
using ByYourSight.Perception;
using ByYourSight.Safety;
using ByYourSight.Navigation;
using ByYourSight.InputSystem;

namespace ByYourSight.EditorTools
{
    [InitializeOnLoad]
    public static class ByYourSightSceneSetup
    {
        static ByYourSightSceneSetup()
        {
            EditorApplication.delayCall += AutoSetupIfNeeded;
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
            mainCam.backgroundColor = new Color(0.06f, 0.07f, 0.1f, 1f); // Dark background so HoloLens high-contrast highlights stand out

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

            // 3. Everyday Objects for KF01 ("What is this?"), KF02 ("Save object"), & KF03 ("Find...")
            // Water bottle ahead of the user
            CreateTrackablePrimitive(
                PrimitiveType.Cylinder,
                "Water_Bottle",
                envRoot.transform,
                new Vector3(0f, 1.45f, -1.5f),
                new Vector3(0.22f, 0.32f, 0.22f),
                new Color(0.2f, 0.7f, 1f),
                "Water bottle",
                "Blue 600 milliliter water bottle",
                SemanticObjectCategory.EverydayObject
            );

            // Coffee mug on the right
            CreateTrackablePrimitive(
                PrimitiveType.Cylinder,
                "Coffee_Mug",
                envRoot.transform,
                new Vector3(1.8f, 1.35f, -1.8f),
                new Vector3(0.24f, 0.2f, 0.24f),
                new Color(0.85f, 0.55f, 0.25f),
                "Coffee mug",
                "Warm ceramic coffee mug on the right",
                SemanticObjectCategory.EverydayObject
            );

            // Unnamed/generic item on the left (for KF02 "Save object" demo)
            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Item_On_Left",
                envRoot.transform,
                new Vector3(-1.5f, 1.45f, -1.8f),
                new Vector3(0.28f, 0.28f, 0.28f),
                new Color(0.75f, 0.75f, 0.8f),
                "Small box",
                "Small storage box on the left",
                SemanticObjectCategory.EverydayObject
            );

            // 4. Hazard Obstacles for KF07 (P0 Danger Alarm: "Warning! Pillar, 1 meter, ahead")
            CreateTrackablePrimitive(
                PrimitiveType.Cylinder,
                "Support_Pillar",
                envRoot.transform,
                new Vector3(-0.9f, 1.2f, 0.5f),
                new Vector3(0.45f, 1.2f, 0.45f),
                new Color(0.85f, 0.3f, 0.25f),
                "Pillar",
                "Concrete support pillar in the middle of the room",
                SemanticObjectCategory.HazardObstacle
            );

            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Chair_Obstacle",
                envRoot.transform,
                new Vector3(1.2f, 0.6f, 0.8f),
                new Vector3(0.65f, 0.6f, 0.65f),
                new Color(0.7f, 0.4f, 0.2f),
                "Chair",
                "Wooden chair blocking the walkway",
                SemanticObjectCategory.HazardObstacle
            );

            // 5. Person Nearby for KF08 (P2 Social Info: "Person approaching, right")
            CreateTrackablePrimitive(
                PrimitiveType.Capsule,
                "Nearby_Person",
                envRoot.transform,
                new Vector3(2.4f, 1.1f, 1.2f),
                new Vector3(0.55f, 1.0f, 0.55f),
                new Color(0.4f, 0.85f, 0.55f),
                "Person",
                "Person nearby in the room",
                SemanticObjectCategory.Person
            );

            // 6. Destinations & Waypoints for KF05 ("Navigate to Door" & "Navigate to Desk")
            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Exit_Door",
                envRoot.transform,
                new Vector3(0f, 1.3f, 4.2f),
                new Vector3(1.2f, 2.2f, 0.2f),
                new Color(0.25f, 0.85f, 0.9f),
                "Exit door",
                "Room exit door at the front wall",
                SemanticObjectCategory.EverydayObject
            );

            CreateTrackablePrimitive(
                PrimitiveType.Cube,
                "Work_Desk",
                envRoot.transform,
                new Vector3(2.8f, 0.8f, 2.5f),
                new Vector3(1.4f, 0.8f, 0.9f),
                new Color(0.55f, 0.45f, 0.3f),
                "Work desk",
                "Work desk in the front-right corner",
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

            // Attach ARSession & ARMeshManager so HoloLens 2 scans real physical walls/obstacles in the room
            if (Object.FindObjectOfType<ARSession>() == null)
            {
                GameObject arSessionObj = new GameObject("HoloLens_ARSession");
                arSessionObj.transform.SetParent(coreSys.transform);
                arSessionObj.AddComponent<ARSession>();
                arSessionObj.AddComponent<ARInputManager>();
            }

            GameObject meshManagerObj = new GameObject("HoloLens_RealWorldSpatialMesh");
            meshManagerObj.transform.SetParent(coreSys.transform);
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
                    destinationName = "Door",
                    waypoints = new List<Transform> { wpDoor1, wpDoor2 }
                },
                new NavigationDestination
                {
                    destinationName = "Desk",
                    waypoints = new List<Transform> { wpDesk1, wpDesk2 }
                }
            };

            controller.objectRecognizer = recognizer;
            controller.navigator = navigator;
            controller.obstacleDetector = obstacleDetector;

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = coreSys;

            Debug.Log("=== [By Your Sight] Native HoloLens (English) Prototype Scene (KF01 - KF08) built! Press PLAY (▶) to test. ===");
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
