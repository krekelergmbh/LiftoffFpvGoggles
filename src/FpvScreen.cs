using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// The real thing: camera lens -> screen -> eye.
    ///
    /// A dedicated capture camera renders the world from the drone camera's pose with the
    /// lens field of view of a real FPV camera (wide, 4:3) into a render texture. That
    /// texture is shown on a flat, head-fixed quad that covers only the field of view of a
    /// real goggle. So a wide angle image gets squeezed into a small window - exactly what
    /// makes flying FPV hard, and exactly what a mask cropping the VR view cannot reproduce.
    ///
    /// The VR camera itself is told to render nothing but our quad on a black background.
    /// That kills two birds: no second copy of the world behind the screen, and no feedback
    /// loop from the capture camera filming our own quad.
    /// </summary>
    internal static class FpvScreen
    {
        private static Camera _captureCamera;
        private static RenderTexture _texture;
        private static GameObject _quad;
        private static MeshFilter _quadFilter;
        private static Material _material;

        private static Camera _vrCamera;
        private static int _originalCullingMask;
        private static CameraClearFlags _originalClearFlags;
        private static Color _originalBackground;
        private static bool _vrCameraCaptured;

        private static int _layer = -1;
        private static float _builtHalfWidth, _builtHalfHeight, _builtDistance;
        private static bool _quadBuilt;
        private static bool _warned;

        internal static bool IsActive { get { return _quad != null; } }

        internal static void Update(Camera vrCamera, float halfWidth, float halfHeight, float distance)
        {
            if (vrCamera == null) { Teardown(); return; }

            Camera droneCamera = ResolveDroneCamera(vrCamera);
            if (droneCamera == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    FpvGogglesPlugin.Log.LogWarning("Could not find the game camera behind UUVR's VR camera, so the virtual screen stays off.");
                }
                Teardown();
                return;
            }

            if (_layer < 0) _layer = FindFreeLayer();

            EnsureTexture();
            if (_texture == null) return;

            EnsureCaptureCamera(droneCamera);
            EnsureQuad(vrCamera, halfWidth, halfHeight, distance);
            TakeOverVrCamera(vrCamera);

            // The capture camera rides on the drone camera, never on the head. Head movement
            // must not move the image, the same way turning your head does not aim a real
            // drone's camera.
            _captureCamera.transform.position = droneCamera.transform.position;
            _captureCamera.transform.rotation = droneCamera.transform.rotation;
            _captureCamera.fieldOfView = VerticalFovFromDiagonal(
                FpvGogglesPlugin.LensFovDiagonal.Value,
                FpvGogglesPlugin.MaskAspectWidth.Value,
                FpvGogglesPlugin.MaskAspectHeight.Value);
            _captureCamera.nearClipPlane = droneCamera.nearClipPlane;
            _captureCamera.farClipPlane = droneCamera.farClipPlane;
        }

        /// <summary>
        /// UUVR's hierarchy is: game camera -> VrCameraOffset -> VrChildCamera. We want the
        /// game camera, because that one is the drone and is unaffected by head tracking.
        /// </summary>
        private static Camera ResolveDroneCamera(Camera vrCamera)
        {
            Transform transform = vrCamera.transform;

            if (transform.parent != null && transform.parent.parent != null)
            {
                Camera camera = transform.parent.parent.GetComponent<Camera>();
                if (camera != null) return camera;
            }

            if (transform.parent != null)
            {
                Camera camera = transform.parent.GetComponent<Camera>();
                if (camera != null) return camera;
            }

            return null;
        }

        /// <summary>Unity's fieldOfView is vertical; FPV cameras are sold by diagonal FOV.</summary>
        private static float VerticalFovFromDiagonal(float diagonalDegrees, float aspectWidth, float aspectHeight)
        {
            float width = Mathf.Max(0.01f, aspectWidth);
            float height = Mathf.Max(0.01f, aspectHeight);

            float halfDiagonal = Mathf.Tan(Mathf.Clamp(diagonalDegrees, 10f, 175f) * 0.5f * Mathf.Deg2Rad);
            float halfHeight = halfDiagonal * height / Mathf.Sqrt(width * width + height * height);

            return Mathf.Clamp(2f * Mathf.Atan(halfHeight) * Mathf.Rad2Deg, 1f, 179f);
        }

        private static void EnsureTexture()
        {
            int height = FpvGogglesPlugin.CaptureHeight.Value;
            float aspect = Mathf.Max(0.01f, FpvGogglesPlugin.MaskAspectWidth.Value) /
                           Mathf.Max(0.01f, FpvGogglesPlugin.MaskAspectHeight.Value);
            int width = Mathf.Max(16, Mathf.RoundToInt(height * aspect));

            if (_texture != null && _texture.width == width && _texture.height == height) return;

            if (_texture != null)
            {
                if (_captureCamera != null) _captureCamera.targetTexture = null;
                _texture.Release();
                UnityEngine.Object.Destroy(_texture);
            }

            _texture = new RenderTexture(width, height, 24, RenderTextureFormat.Default);
            _texture.name = "FpvScreenTexture";
            _texture.hideFlags = HideFlags.HideAndDontSave;
            _texture.filterMode = FilterMode.Bilinear;
            _texture.Create();

            if (_captureCamera != null) _captureCamera.targetTexture = _texture;
            if (_material != null) _material.mainTexture = _texture;

            FpvGogglesPlugin.Log.LogInfo("FPV screen texture: " + width + "x" + height + ".");
        }

        private static void EnsureCaptureCamera(Camera droneCamera)
        {
            if (_captureCamera != null) return;

            GameObject holder = new GameObject("FpvCaptureCamera");
            UnityEngine.Object.DontDestroyOnLoad(holder);

            _captureCamera = holder.AddComponent<Camera>();
            _captureCamera.CopyFrom(droneCamera);
            _captureCamera.targetTexture = _texture;
            _captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCamera.aspect = Mathf.Max(0.01f, FpvGogglesPlugin.MaskAspectWidth.Value) /
                                   Mathf.Max(0.01f, FpvGogglesPlugin.MaskAspectHeight.Value);
            // Never film our own screen.
            _captureCamera.cullingMask = droneCamera.cullingMask & ~(1 << _layer);
            _captureCamera.depth = droneCamera.depth - 10f;
            _captureCamera.enabled = true;

            IgnoreInUuvr(_captureCamera);

            FpvGogglesPlugin.Log.LogInfo("FPV capture camera created, copying '" + droneCamera.name + "'.");
        }

        /// <summary>
        /// UUVR turns every camera it finds into a VR camera. Ours must stay a plain flat
        /// camera, so we put it into UUVR's own ignore list.
        /// </summary>
        private static void IgnoreInUuvr(Camera camera)
        {
            try
            {
                Type type = HarmonyLib.AccessTools.TypeByName("Uuvr.VrCamera.VrCamera");
                if (type == null) return;

                System.Reflection.FieldInfo field = HarmonyLib.AccessTools.Field(type, "IgnoredCameras");
                if (field == null) return;

                object set = field.GetValue(null);
                if (set == null) return;

                System.Reflection.MethodInfo add = set.GetType().GetMethod("Add");
                if (add != null) add.Invoke(set, new object[] { camera });
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Could not add the capture camera to UUVR's ignore list: " + e.Message);
            }
        }

        private static void EnsureQuad(Camera vrCamera, float halfWidth, float halfHeight, float distance)
        {
            if (_quad == null || _quad.transform.parent != vrCamera.transform)
            {
                if (_quad != null) UnityEngine.Object.Destroy(_quad);

                _quad = new GameObject("FpvScreen");
                _quad.transform.SetParent(vrCamera.transform, false);
                _quad.layer = _layer;

                _quadFilter = _quad.AddComponent<MeshFilter>();
                MeshRenderer renderer = _quad.AddComponent<MeshRenderer>();

                if (_material == null) _material = CreateMaterial();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                _quadBuilt = false;
                FpvGogglesPlugin.Log.LogInfo("FPV screen quad created on layer " + _layer + ".");
            }

            _quad.transform.localPosition = Vector3.zero;
            _quad.transform.localRotation = Quaternion.identity;
            _quad.transform.localScale = Vector3.one;

            if (!_quadBuilt || _builtHalfWidth != halfWidth || _builtHalfHeight != halfHeight || _builtDistance != distance)
            {
                _quadFilter.sharedMesh = BuildQuadMesh(halfWidth, halfHeight, distance);
                _builtHalfWidth = halfWidth;
                _builtHalfHeight = halfHeight;
                _builtDistance = distance;
                _quadBuilt = true;
            }
        }

        private static Mesh BuildQuadMesh(float halfWidth, float halfHeight, float distance)
        {
            bool flip = FpvGogglesPlugin.FlipScreen.Value;
            float bottom = flip ? 1f : 0f;
            float top = flip ? 0f : 1f;

            Vector3[] vertices =
            {
                new Vector3(-halfWidth, -halfHeight, distance),
                new Vector3(-halfWidth, halfHeight, distance),
                new Vector3(halfWidth, halfHeight, distance),
                new Vector3(halfWidth, -halfHeight, distance),
            };

            Vector2[] uv =
            {
                new Vector2(0f, bottom), new Vector2(0f, top),
                new Vector2(1f, top), new Vector2(1f, bottom),
            };

            // Wound both ways, so backface culling can never blank the screen.
            int[] triangles = { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 };

            Mesh mesh = new Mesh();
            mesh.name = "FpvScreen";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return mesh;
        }

        private static Material CreateMaterial()
        {
            string[] candidates = { "Unlit/Texture", "Sprites/Default", "UI/Default", "Unlit/Transparent" };

            Shader shader = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                shader = Shader.Find(candidates[i]);
                if (shader != null)
                {
                    FpvGogglesPlugin.Log.LogInfo("FPV screen using shader '" + candidates[i] + "'.");
                    break;
                }
            }

            if (shader == null)
            {
                Material canvasMaterial = Canvas.GetDefaultCanvasMaterial();
                if (canvasMaterial != null) shader = canvasMaterial.shader;
            }
            if (shader == null)
            {
                FpvGogglesPlugin.Log.LogError("Found no shader that can display a texture. The FPV screen will stay black.");
                return null;
            }

            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.name = "FpvScreen";
            material.mainTexture = _texture;
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            material.renderQueue = 5000;
            if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)CompareFunction.Always);
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Off);
            return material;
        }

        /// <summary>
        /// Makes the VR camera show nothing but our screen on black. Without this the world
        /// would be drawn a second time behind the screen, at headset field of view - which
        /// is precisely the effect we are getting rid of.
        /// </summary>
        private static void TakeOverVrCamera(Camera vrCamera)
        {
            if (!_vrCameraCaptured || _vrCamera != vrCamera)
            {
                _vrCamera = vrCamera;
                _originalCullingMask = vrCamera.cullingMask;
                _originalClearFlags = vrCamera.clearFlags;
                _originalBackground = vrCamera.backgroundColor;
                _vrCameraCaptured = true;
            }

            // Re-applied every frame: UUVR copies settings from the parent camera and would
            // otherwise undo this.
            vrCamera.cullingMask = 1 << _layer;
            vrCamera.clearFlags = CameraClearFlags.SolidColor;
            vrCamera.backgroundColor = Color.black;
        }

        private static int FindFreeLayer()
        {
            for (int layer = 31; layer >= 8; layer--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            }

            FpvGogglesPlugin.Log.LogWarning("No unused layer found, falling back to layer 31 for the FPV screen.");
            return 31;
        }

        internal static void Teardown()
        {
            if (_vrCameraCaptured && _vrCamera != null)
            {
                _vrCamera.cullingMask = _originalCullingMask;
                _vrCamera.clearFlags = _originalClearFlags;
                _vrCamera.backgroundColor = _originalBackground;
            }
            _vrCameraCaptured = false;
            _vrCamera = null;

            if (_quad != null) { UnityEngine.Object.Destroy(_quad); _quad = null; }
            if (_captureCamera != null) { UnityEngine.Object.Destroy(_captureCamera.gameObject); _captureCamera = null; }
            _quadBuilt = false;
        }
    }
}
