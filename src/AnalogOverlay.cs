using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// Analog video artefacts over the whole picture: lens vignette, RF static, and the
    /// scanlines of the goggle screen - painted in that order, because that is the order a real
    /// signal picks them up on its way from the camera to your eye.
    ///
    /// Everything here is flat quads in front of the camera, the same trick the mask and the
    /// horizon use. Reading the rendered image back would allow much more - desaturation,
    /// chroma smear, tearing - but that needs a custom shader, and a shader cannot be written
    /// from inside a BepInEx plugin. It would have to ship as an AssetBundle built in the
    /// matching Unity version, and would break on every engine update Liftoff makes.
    ///
    /// The layers sit above the mask and above the horizon, so the OSD is degraded along with
    /// the picture. That is not sloppiness: on real hardware the OSD is drawn by the flight
    /// controller, before the transmitter, so it goes through the same radio link.
    /// </summary>
    internal static class AnalogOverlay
    {
        // ------------------------------------------------------------------
        // Objects
        // ------------------------------------------------------------------

        private static GameObject _root;
        private static Layer _vignette, _static, _scanlines, _band;
        private static Camera _camera;

        private static float _builtHalfWidth, _builtHalfHeight, _builtDistance, _builtScanCount;

        private sealed class Layer
        {
            internal GameObject Object;
            internal MeshFilter Filter;
            internal Material Material;
            internal bool HasTint;
        }

        // ------------------------------------------------------------------
        // Link state
        // ------------------------------------------------------------------

        /// <summary>Where the pilot stands. Taken from the drone's own position at spawn.</summary>
        private static Vector3 _home;
        private static bool _homeSet;

        /// <summary>Smoothed link quality, 1 = clean picture, 0 = nothing left.</summary>
        private static float _signal = 1f;

        /// <summary>Read by the image processing, which fades the colour out along with it.</summary>
        internal static float Signal { get { return _signal; } }

        private static bool _blocked;
        private static float _obstacleTimer;
        private static float _burstTimer, _burstLeft, _burstFrom;
        private static float _noiseTimer;
        private static float _logTimer;

        // ------------------------------------------------------------------
        // Entry point
        // ------------------------------------------------------------------

        internal static void Update(Camera viewCamera, float apertureHalfWidth, float apertureHalfHeight,
            float distance, bool maskActive)
        {
            if (viewCamera == null)
            {
                Teardown();
                return;
            }

            if (_root == null || _camera != viewCamera || _root.transform.parent != viewCamera.transform)
            {
                Build(viewCamera);
                if (_root == null) return;
            }

            float halfWidth, halfHeight;
            ComputePicture(viewCamera, distance, maskActive,
                apertureHalfWidth, apertureHalfHeight, out halfWidth, out halfHeight);

            if (GeometryChanged(halfWidth, halfHeight, distance))
            {
                Rebuild(halfWidth, halfHeight, distance);
            }

            UpdateSignal(viewCamera);
            ApplyLayers(halfHeight);
        }

        /// <summary>
        /// Called on every scene change, so the next flight measures from its own spawn point
        /// instead of from wherever the last one ended.
        /// </summary>
        internal static void ResetHome()
        {
            _homeSet = false;
            _signal = 1f;
            _blocked = false;
            _burstLeft = 0f;
        }

        // ------------------------------------------------------------------
        // The radio link
        // ------------------------------------------------------------------

        /// <summary>
        /// A deliberately simple model, but driven by things you can see out of the goggles,
        /// which is what makes it read as real: distance, whether there is something between you
        /// and the drone, and how the transmitting antenna happens to be pointing.
        /// </summary>
        private static void UpdateSignal(Camera viewCamera)
        {
            float delta = Time.unscaledDeltaTime;
            Transform view = viewCamera.transform;
            Vector3 position = view.position;

            if (!_homeSet)
            {
                _home = position;
                _homeSet = true;
            }

            float range = Mathf.Max(10f, FpvGogglesPlugin.SignalRange.Value);
            float metres = Vector3.Distance(position, _home);
            float reach = metres / range;

            // Analog does not stop at an edge the way digital does. It stays clean for a good
            // while and then falls away, which is the whole reason people still fly it.
            float quality = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((reach - 0.35f) / 0.85f));

            if (FpvGogglesPlugin.AntennaNull.Value)
            {
                // A dipole radiates nothing along its own axis, so the worst place to be is
                // directly above the pilot with the drone level. Weighted by distance, because
                // close in there is signal to spare and the null never shows.
                Vector3 toPilot = _home - position;
                if (toPilot.sqrMagnitude > 1f)
                {
                    float alignment = Mathf.Abs(Vector3.Dot(toPilot.normalized, view.up));
                    float depth = alignment * alignment * alignment * Mathf.Clamp01(reach);
                    quality *= 1f - 0.6f * depth;
                }
            }

            // Ten times a second is plenty for something you fly through in a fraction of a
            // second, and it keeps the cast off the critical path.
            _obstacleTimer -= delta;
            if (_obstacleTimer <= 0f)
            {
                _obstacleTimer = 0.1f;
                _blocked = FpvGogglesPlugin.ObstaclesBlock.Value && IsBlocked(position);
            }
            if (_blocked) quality *= 0.35f;

            // Real links wander. Scaled by how much is already lost, so a strong signal stays
            // rock solid instead of shimmering for no reason.
            float wander = Mathf.PerlinNoise(Time.unscaledTime * 0.7f, 0.5f) - 0.5f;
            quality = Mathf.Clamp01(quality + wander * 0.35f * (1f - quality));

            // Losing the picture is abrupt, getting it back takes a moment - that asymmetry is
            // most of what makes a bad link feel bad.
            float rate = quality < _signal ? 6f : 2f;
            _signal = Mathf.MoveTowards(_signal, quality, delta * rate);

            UpdateBreakup(delta);
            LogSignal(delta, metres);
        }

        private static bool IsBlocked(Vector3 dronePosition)
        {
            // Head height, because that is where you hold the goggles and their antennas.
            Vector3 pilot = _home + Vector3.up * 1.6f;
            Vector3 delta = dronePosition - pilot;
            float length = delta.magnitude;
            if (length < 5f) return false;

            // Stop short of the drone so its own colliders do not count as an obstacle.
            Vector3 target = pilot + delta / length * (length - 2f);

            try
            {
                return Physics.Linecast(pilot, target, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Short bursts where the picture tears up completely. A steady amount of snow reads as
        /// a filter; it is the breakup that reads as radio.
        /// </summary>
        private static void UpdateBreakup(float delta)
        {
            if (_burstLeft > 0f) _burstLeft -= delta;

            if (!FpvGogglesPlugin.Breakup.Value)
            {
                _burstLeft = 0f;
                return;
            }

            _burstTimer -= delta;
            if (_burstTimer > 0f) return;

            float pressure = Mathf.Clamp01((0.8f - _signal) / 0.8f);
            _burstTimer = UnityEngine.Random.Range(0.3f, 1.2f) + Mathf.Lerp(12f, 0f, pressure);

            if (pressure > 0.15f && UnityEngine.Random.value < pressure)
            {
                _burstLeft = UnityEngine.Random.Range(0.05f, 0.05f + 0.3f * pressure);
                _burstFrom = UnityEngine.Random.Range(-1f, 1f);
            }
        }

        private static void LogSignal(float delta, float metres)
        {
            if (!FpvGogglesPlugin.LogSignal.Value) return;

            _logTimer -= delta;
            if (_logTimer > 0f) return;
            _logTimer = 2f;

            FpvGogglesPlugin.Log.LogInfo("Link: " + Mathf.RoundToInt(_signal * 100f) + "% at " +
                metres.ToString("0") + " m" + (_blocked ? ", line of sight blocked" : ""));
        }

        // ------------------------------------------------------------------
        // Drawing
        // ------------------------------------------------------------------

        private static void ApplyLayers(float halfHeight)
        {
            float vignette = Mathf.Clamp01(FpvGogglesPlugin.VignetteStrength.Value);
            SetLayer(_vignette, vignette > 0.001f, new Color(0f, 0f, 0f, vignette));

            float scanlines = Mathf.Clamp01(FpvGogglesPlugin.ScanlineStrength.Value);
            SetLayer(_scanlines, scanlines > 0.001f, new Color(0f, 0f, 0f, scanlines));

            // Snow rises as the link falls, but never quite goes away: even a perfect analog
            // picture has grain on it.
            float grain = Mathf.Clamp01(FpvGogglesPlugin.BaseGrain.Value);
            float heavy = Mathf.Clamp01(FpvGogglesPlugin.StaticStrength.Value);
            float amount = Mathf.Lerp(grain, heavy, 1f - _signal);
            if (_burstLeft > 0f) amount = Mathf.Max(amount, heavy * 0.9f);

            SetLayer(_static, amount > 0.001f, new Color(1f, 1f, 1f, amount));
            if (amount > 0.001f) CycleNoise();

            // The sync bar only appears during a breakup, sweeping upwards the way a picture
            // does when it loses its lock.
            bool bar = _burstLeft > 0f && _band != null;
            SetLayer(_band, bar, new Color(1f, 1f, 1f, 0.5f));
            if (bar)
            {
                float progress = 1f - Mathf.Clamp01(_burstLeft / 0.35f);
                float y = Mathf.Lerp(_burstFrom, _burstFrom + 2f, progress);
                if (y > 1f) y -= 2f;
                _band.Object.transform.localPosition = new Vector3(0f, y * halfHeight, 0f);
            }
        }

        private static void SetLayer(Layer layer, bool visible, Color tint)
        {
            if (layer == null || layer.Object == null) return;

            if (layer.Object.activeSelf != visible) layer.Object.SetActive(visible);
            if (!visible || !layer.HasTint) return;

            if (layer.Material.HasProperty("_Color")) layer.Material.SetColor("_Color", tint);
            if (layer.Material.HasProperty("_TintColor")) layer.Material.SetColor("_TintColor", tint);
        }

        /// <summary>
        /// Swaps in one of the pre-generated noise frames. Generating a fresh texture every
        /// frame would be the obvious way and is far too slow at 90 Hz; a pool picked at random
        /// is indistinguishable, because nobody can recognise a frame of snow.
        /// </summary>
        private static void CycleNoise()
        {
            _noiseTimer -= Time.unscaledDeltaTime;
            if (_noiseTimer > 0f) return;
            _noiseTimer = 1f / 30f;

            if (_noiseFrames == null || _static == null || _static.Material == null) return;
            _static.Material.mainTexture = _noiseFrames[UnityEngine.Random.Range(0, _noiseFrames.Length)];
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        /// <summary>
        /// How much of your view counts as "the picture". With the goggle mask on that is the
        /// mask aperture, so the artefacts land exactly on the visible window and never touch
        /// the black border. Without it, everything you can see is the picture.
        /// </summary>
        private static void ComputePicture(Camera viewCamera, float distance, bool maskActive,
            float apertureHalfWidth, float apertureHalfHeight, out float halfWidth, out float halfHeight)
        {
            if (maskActive)
            {
                halfWidth = apertureHalfWidth;
                halfHeight = apertureHalfHeight;
                return;
            }

            float fov = viewCamera.fieldOfView;
            if (fov < 20f || fov > 175f) fov = 110f;
            halfHeight = distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.25f;

            float aspect = viewCamera.aspect;
            if (aspect < 0.5f || aspect > 4f) aspect = 1f;
            halfWidth = halfHeight * Mathf.Max(1.3f, aspect);
        }

        private static bool GeometryChanged(float halfWidth, float halfHeight, float distance)
        {
            return _builtHalfWidth != halfWidth
                || _builtHalfHeight != halfHeight
                || _builtDistance != distance
                || _builtScanCount != FpvGogglesPlugin.ScanlineCount.Value;
        }

        private static void Rebuild(float halfWidth, float halfHeight, float distance)
        {
            // One tile of the gradient over the whole picture.
            SetMesh(_vignette, halfWidth, halfHeight, 1f, 1f);

            // Square texels, so the snow does not come out stretched on a wide picture.
            float rows = 12f;
            SetMesh(_static, halfWidth, halfHeight, rows * halfWidth / Mathf.Max(0.001f, halfHeight), rows);

            // The configured number of lines across the picture height, which is what the
            // setting promises.
            float lines = Mathf.Max(20f, FpvGogglesPlugin.ScanlineCount.Value);
            SetMesh(_scanlines, halfWidth, halfHeight, 1f, lines);

            SetMesh(_band, halfWidth, halfHeight * 0.06f, 1f, 1f);

            _root.transform.localPosition = new Vector3(0f, 0f, distance);

            _builtHalfWidth = halfWidth;
            _builtHalfHeight = halfHeight;
            _builtDistance = distance;
            _builtScanCount = FpvGogglesPlugin.ScanlineCount.Value;
        }

        private static void SetMesh(Layer layer, float halfWidth, float halfHeight, float uTiles, float vTiles)
        {
            if (layer == null || layer.Filter == null) return;

            Mesh old = layer.Filter.sharedMesh;
            if (old != null) UnityEngine.Object.Destroy(old);

            Mesh mesh = new Mesh();
            mesh.name = "FpvAnalogQuad";
            mesh.hideFlags = HideFlags.HideAndDontSave;

            mesh.vertices = new Vector3[]
            {
                new Vector3(-halfWidth, -halfHeight, 0f),
                new Vector3(-halfWidth, halfHeight, 0f),
                new Vector3(halfWidth, halfHeight, 0f),
                new Vector3(halfWidth, -halfHeight, 0f),
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f), new Vector2(0f, vTiles),
                new Vector2(uTiles, vTiles), new Vector2(uTiles, 0f),
            };

            // Sprites/Default multiplies by the vertex colour, and a mesh without a colour
            // stream can come through as transparent black - which would make the whole layer
            // invisible for no obvious reason.
            Color32 white = new Color32(255, 255, 255, 255);
            mesh.colors32 = new Color32[] { white, white, white, white };

            // Wound one way only. The mask gets away with double winding because it is opaque;
            // here a second pass would blend on top of the first and double every strength.
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            layer.Filter.sharedMesh = mesh;
        }

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        private static void Build(Camera viewCamera)
        {
            Teardown();
            EnsureTextures();

            _camera = viewCamera;

            _root = new GameObject("FpvAnalogOverlay");
            _root.transform.SetParent(viewCamera.transform, false);
            _root.transform.localPosition = Vector3.zero;
            _root.transform.localRotation = Quaternion.identity;
            _root.transform.localScale = Vector3.one;
            _root.layer = viewCamera.gameObject.layer;

            // Render order is the signal path: the lens vignettes first, the radio link adds
            // snow on top of that, and the goggle screen draws the lot as scanlines. All of it
            // above the mask (5000) and the horizon (5001).
            _vignette = NewLayer("Vignette", _vignetteTexture, 5002, TextureWrapMode.Clamp, FilterMode.Bilinear);
            _static = NewLayer("Static", _noiseFrames != null ? _noiseFrames[0] : null, 5003,
                TextureWrapMode.Repeat, FilterMode.Point);
            _scanlines = NewLayer("Scanlines", _scanlineTexture, 5004, TextureWrapMode.Repeat, FilterMode.Bilinear);
            _band = NewLayer("SyncBar", _bandTexture, 5005, TextureWrapMode.Clamp, FilterMode.Bilinear);

            _builtHalfWidth = -1f;
        }

        private static Layer NewLayer(string name, Texture texture, int queue,
            TextureWrapMode wrap, FilterMode filter)
        {
            Material material = CreateMaterial(name, queue);
            if (material == null) return null;

            if (texture != null)
            {
                texture.wrapMode = wrap;
                texture.filterMode = filter;
                material.mainTexture = texture;
            }

            GameObject part = new GameObject(name);
            part.transform.SetParent(_root.transform, false);
            part.layer = _root.layer;

            Layer layer = new Layer();
            layer.Object = part;
            layer.Material = material;
            layer.Filter = part.AddComponent<MeshFilter>();
            layer.HasTint = material.HasProperty("_Color") || material.HasProperty("_TintColor");

            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;

            part.SetActive(false);
            return layer;
        }

        /// <summary>
        /// A textured, alpha blended, tintable material. Deliberately a different candidate list
        /// from the mask's: Unlit/Color is opaque and has no texture slot at all, which is fine
        /// for a black border and useless here.
        /// </summary>
        /// <summary>
        /// UI/Default comes first, and not because it is the likeliest to exist. It declares
        /// ZTest [unity_GUIZTestMode], which is the only handle these quads have on the depth
        /// test. Sprites/Default looks like the obvious choice and has no depth control at all -
        /// it leaves the whole effect hiding behind anything you fly close to, painted onto the
        /// far scenery only.
        /// </summary>
        private static readonly string[] ShaderCandidates =
        {
            "UI/Default", "Particles/Standard Unlit",
            "Legacy Shaders/Particles/Alpha Blended", "Mobile/Particles/Alpha Blended",
            "Sprites/Default",
        };

        private static bool _loggedShader;

        private static Material CreateMaterial(string name, int queue)
        {
            Shader shader = null;
            for (int i = 0; i < ShaderCandidates.Length; i++)
            {
                shader = Shader.Find(ShaderCandidates[i]);
                if (shader != null) break;
            }

            if (shader == null)
            {
                Material canvasMaterial = Canvas.GetDefaultCanvasMaterial();
                if (canvasMaterial != null) shader = canvasMaterial.shader;
            }
            if (shader == null)
            {
                FpvGogglesPlugin.Log.LogError("Found no transparent shader for the analog overlay.");
                return null;
            }

            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.name = "FpvAnalog" + name;

            material.renderQueue = queue;
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Off);

            // Both spellings. unity_GUIZTestMode is not declared as a property - HasProperty
            // returns false for it even on shaders that read it - so it has to be set blind.
            bool ownsDepth = material.HasProperty("_ZTest");
            if (ownsDepth) material.SetInt("_ZTest", (int)CompareFunction.Always);
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);

            if (!_loggedShader)
            {
                _loggedShader = true;
                FpvGogglesPlugin.Log.LogInfo("Analog overlay using shader '" + shader.name + "'.");

                if (!ownsDepth && shader.name.IndexOf("UI/Default", StringComparison.Ordinal) < 0)
                {
                    FpvGogglesPlugin.Log.LogWarning(
                        "That shader has no depth test override, so the analog layers will be hidden behind anything closer to the camera than the overlay plane.");
                }
            }

            return material;
        }

        // ------------------------------------------------------------------
        // Textures, generated once and kept for the life of the process
        // ------------------------------------------------------------------

        private const int NoiseFrames = 20;
        private const int NoiseSize = 128;

        private static Texture2D[] _noiseFrames;
        private static Texture2D _scanlineTexture, _vignetteTexture, _bandTexture;

        private static void EnsureTextures()
        {
            if (_noiseFrames != null) return;

            _noiseFrames = new Texture2D[NoiseFrames];
            Color32[] pixels = new Color32[NoiseSize * NoiseSize];

            for (int frame = 0; frame < NoiseFrames; frame++)
            {
                for (int y = 0; y < NoiseSize; y++)
                {
                    int x = 0;
                    while (x < NoiseSize)
                    {
                        // Snow smears along the scan line, so a speck is wider than it is tall.
                        // Square dots look like film grain instead of radio noise.
                        int run = UnityEngine.Random.Range(1, 4);
                        byte level = (byte)UnityEngine.Random.Range(90, 256);

                        // Squared, so most of the frame stays clear and the specks are sparse -
                        // the overall density is set by the layer's tint alpha.
                        float chance = UnityEngine.Random.value;
                        byte alpha = (byte)(chance * chance * 255f);

                        Color32 colour = new Color32(level, level, level, alpha);
                        for (int i = 0; i < run && x < NoiseSize; i++, x++)
                        {
                            pixels[y * NoiseSize + x] = colour;
                        }
                    }
                }

                Texture2D noise = NewTexture("FpvAnalogNoise" + frame, NoiseSize, NoiseSize, pixels);

                // Every frame needs this, not just the one handed to the layer at build time:
                // the material swaps between all of them. Point sampling keeps the specks crisp,
                // which is what snow looks like - blurred, it turns into fog.
                noise.wrapMode = TextureWrapMode.Repeat;
                noise.filterMode = FilterMode.Point;
                _noiseFrames[frame] = noise;
            }

            _scanlineTexture = BuildScanlines();
            _vignetteTexture = BuildVignette();
            _bandTexture = BuildBand();

            FpvGogglesPlugin.Log.LogInfo("Analog overlay textures generated.");
        }

        /// <summary>One soft dark line per tile. Hard edges would alias into moire patterns.</summary>
        private static Texture2D BuildScanlines()
        {
            const int height = 16;
            Color32[] pixels = new Color32[2 * height];

            for (int y = 0; y < height; y++)
            {
                float t = (y + 0.5f) / height;
                byte alpha = (byte)(255f * (0.5f - 0.5f * Mathf.Cos(t * 2f * Mathf.PI)));
                pixels[y * 2] = new Color32(0, 0, 0, alpha);
                pixels[y * 2 + 1] = new Color32(0, 0, 0, alpha);
            }

            return NewTexture("FpvAnalogScanlines", 2, height, pixels);
        }

        /// <summary>Radial falloff: clear in the middle, dark towards the corners.</summary>
        private static Texture2D BuildVignette()
        {
            const int size = 128;
            Color32[] pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                float dy = (y + 0.5f) / size * 2f - 1f;
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float radius = Mathf.Sqrt(dx * dx + dy * dy) / 1.41421f;
                    float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, radius));
                    pixels[y * size + x] = new Color32(0, 0, 0, (byte)(alpha * 255f));
                }
            }

            return NewTexture("FpvAnalogVignette", size, size, pixels);
        }

        /// <summary>A soft bright band, brightest in the middle and fading out top and bottom.</summary>
        private static Texture2D BuildBand()
        {
            const int height = 32;
            Color32[] pixels = new Color32[2 * height];

            for (int y = 0; y < height; y++)
            {
                float t = (y + 0.5f) / height;
                float profile = Mathf.Sin(t * Mathf.PI);
                byte alpha = (byte)(255f * profile * profile * profile);
                pixels[y * 2] = new Color32(255, 255, 255, alpha);
                pixels[y * 2 + 1] = new Color32(255, 255, 255, alpha);
            }

            return NewTexture("FpvAnalogBand", 2, height, pixels);
        }

        private static Texture2D NewTexture(string name, int width, int height, Color32[] pixels)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = name;
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixels32(pixels);

            // No mipmaps, and drop the CPU side copy: these are written once and only ever read
            // by the GPU after that.
            texture.Apply(false, true);
            return texture;
        }

        // ------------------------------------------------------------------

        internal static void Teardown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _vignette = null;
            _static = null;
            _scanlines = null;
            _band = null;
            _camera = null;
            _builtHalfWidth = -1f;
        }
    }
}
