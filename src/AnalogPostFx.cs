using HarmonyLib;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// The half of the analog look that has to read the rendered image back: colour response,
    /// chroma fringing, lens distortion, blown highlights, and the gain control hunting for an
    /// exposure. None of that can be painted on with a quad.
    ///
    /// It needs no custom shader all the same, because Liftoff ships the Post Processing Stack
    /// v2 and uses it itself - so the effect shaders are compiled into the game and nothing was
    /// stripped. All this does is add a volume of its own at a high priority. Note what is
    /// deliberately *not* overridden: the grading mode. The game may be grading in HDR with a
    /// tonemapper, and forcing that to LDR would throw its whole look away. Saturation, contrast
    /// and white balance apply in either mode.
    ///
    /// Everything here is reached through a direct assembly reference rather than reflection.
    /// If the package ever disappears from the game, loading this class throws - which the
    /// runner catches, switching the feature off and leaving the overlay running.
    /// </summary>
    internal static class AnalogPostFx
    {
        private static Camera _camera;
        private static PostProcessLayer _layer;
        private static PostProcessVolume _volume;

        private static ColorGrading _grading;
        private static ChromaticAberration _aberration;
        private static LensDistortion _distortion;
        private static Bloom _bloom;
        private static AutoExposure _exposure;
        private static CompositeVideo _composite;
        private static CompositeVideoOpaque _compositeOpaque;

        private static bool _loggedLayer, _loggedNoResources;

        /// <summary>Whether the PostProcessLayer on the camera is one we added, or the game's.</summary>
        private static bool _layerIsOurs;

        internal static void Update(Camera viewCamera, float signal)
        {
            // Both halves live in the same volume, so it stays up while either one wants it.
            // Gating it on the image processing switch alone tore the composite pass down with
            // it - and left its own switch doing nothing at all, which is a maddening way for a
            // feature to fail.
            bool wanted = FpvGogglesPlugin.ImageProcessing.Value || FpvGogglesPlugin.CompositeEnabled.Value;

            if (viewCamera == null || !wanted)
            {
                Teardown();
                return;
            }

            if (!EnsureLayer(viewCamera)) return;

            EnsureVolume();
            if (_volume == null) return;

            Apply(signal);
        }

        // ------------------------------------------------------------------
        // Settings, re-applied every frame so the config file stays live
        // ------------------------------------------------------------------

        private static void Apply(float signal)
        {
            float loss = 1f - Mathf.Clamp01(signal);

            // Everything below this line models the camera and the lens. The radio link is the
            // composite pass's job, and where the two overlap the composite pass wins - it is
            // doing the real thing rather than approximating it, and both at once would only
            // apply the same damage twice.
            bool link = FpvGogglesPlugin.CompositeRunning;

            // The camera and lens half, switched as a group. The composite pass below is not
            // part of it and keeps its own switch, which is the point of the change.
            bool image = FpvGogglesPlugin.ImageProcessing.Value;
            _grading.enabled.value = image;

            // Colour goes before the picture does. On a weak analog link the chroma subcarrier
            // is the first casualty, so you fly on a black and white image that is otherwise
            // perfectly readable - and getting the colour back is how you know you are clear
            // again. With composite video on this happens by itself: the noise lands on the
            // subcarrier and drowns it, so faking it here as well would strip the colour twice.
            float fade = link ? 0f : loss * Mathf.Clamp01(FpvGogglesPlugin.ColourLoss.Value);
            _grading.saturation.value = Mathf.Lerp(FpvGogglesPlugin.Saturation.Value, -100f, fade);
            _grading.contrast.value = FpvGogglesPlugin.Contrast.Value;
            _grading.temperature.value = FpvGogglesPlugin.Temperature.Value;

            // Chromatic aberration was standing in for chroma artefacts. The composite decoder
            // produces the genuine article, so the stand-in steps aside.
            float aberration = link ? 0f : FpvGogglesPlugin.Aberration.Value;
            _aberration.enabled.value = image && aberration > 0.001f;
            _aberration.intensity.value = aberration;

            float distortion = FpvGogglesPlugin.Distortion.Value;
            _distortion.enabled.value = image && Mathf.Abs(distortion) > 0.01f;
            _distortion.intensity.value = distortion;

            float bloom = FpvGogglesPlugin.BloomIntensity.Value;
            _bloom.enabled.value = image && bloom > 0.001f;
            _bloom.intensity.value = bloom;

            _exposure.enabled.value = image && FpvGogglesPlugin.AutoExposure.Value;

            ApplyComposite(loss);
        }

        private static void ApplyComposite(float loss)
        {
            bool wanted = FpvGogglesPlugin.CompositeEnabled.Value && CompositeShader.Shader != null;
            bool throughHud = FpvGogglesPlugin.CompositeAffectsHud.Value;

            _composite.enabled.value = wanted && throughHud;
            _compositeOpaque.enabled.value = wanted && !throughHud;
            FpvGogglesPlugin.CompositeRunning = wanted;
            if (!wanted) return;

            CompositeVideoSettings settings = throughHud ? (CompositeVideoSettings)_composite : _compositeOpaque;

            settings.lines.value = FpvGogglesPlugin.SignalLines.Value;
            settings.subcarrier.value = FpvGogglesPlugin.SubcarrierFrequency.Value;
            settings.chromaBleed.value = FpvGogglesPlugin.ChromaBleed.Value;
            settings.saturation.value = FpvGogglesPlugin.ChromaGain.Value;
            settings.softness.value = FpvGogglesPlugin.CompositeSoftness.Value;

            // A clean picture keeps a trace of noise, because a real one does. The rest arrives
            // with the link falling apart - and because it goes in before the decoder, it costs
            // the colour first and the picture second, without either being scripted.
            settings.noise.value = Mathf.Lerp(0.01f, FpvGogglesPlugin.SignalNoise.Value, loss * loss);

            // Squared as well, so lines only start sliding once reception is genuinely poor
            // rather than wobbling gently the whole flight.
            settings.jitter.value = FpvGogglesPlugin.LineJitter.Value * loss * loss;

            // Anything periodic would beat against the frame rate and read as a pattern, so the
            // phase is simply thrown somewhere new every frame.
            settings.seed.value = UnityEngine.Random.value;
        }

        private static void InitComposite(CompositeVideoSettings settings)
        {
            settings.enabled.Override(false);
            settings.lines.Override(480f);
            settings.subcarrier.Override(170f);
            settings.noise.Override(0f);
            settings.saturation.Override(1f);
            settings.chromaBleed.Override(0.8f);
            settings.jitter.Override(0f);
            settings.softness.Override(0.15f);
            settings.seed.Override(0f);
        }

        // ------------------------------------------------------------------
        // The volume
        // ------------------------------------------------------------------

        private static void EnsureVolume()
        {
            // Unity's overloaded == also catches a volume the game destroyed under us, which it
            // is entirely willing to do.
            if (_volume != null) return;

            DestroySettings();

            _grading = ScriptableObject.CreateInstance<ColorGrading>();
            _grading.enabled.Override(true);
            _grading.saturation.Override(0f);
            _grading.contrast.Override(0f);
            _grading.temperature.Override(0f);

            _aberration = ScriptableObject.CreateInstance<ChromaticAberration>();
            _aberration.enabled.Override(false);
            _aberration.intensity.Override(0f);
            // The accurate mode samples a spectral LUT and is not worth its cost for something
            // that is meant to look like interference in the first place.
            _aberration.fastMode.Override(true);

            _distortion = ScriptableObject.CreateInstance<LensDistortion>();
            _distortion.enabled.Override(false);
            _distortion.intensity.Override(0f);

            _bloom = ScriptableObject.CreateInstance<Bloom>();
            _bloom.enabled.Override(false);
            _bloom.intensity.Override(0f);
            // Above 1, so only genuine highlights bloom. At the usual 0.9 the bright specks of
            // the static overlay cross the threshold as well - post processing runs after
            // everything, snow included - and the picture washes out into a grey haze.
            _bloom.threshold.Override(1.3f);
            _bloom.softKnee.Override(0.6f);
            _bloom.fastMode.Override(true);
            // Fewer blur iterations. At the headset's 2544x2564 per eye the bloom pyramid is by
            // far the most expensive thing here, and the difference is barely visible.
            _bloom.diffusion.Override(5f);

            _exposure = ScriptableObject.CreateInstance<AutoExposure>();
            _exposure.enabled.Override(false);
            _exposure.eyeAdaptation.Override(EyeAdaptation.Progressive);
            // Slower to open up than to close down, which is what makes it read as a camera
            // being caught out rather than a brightness slider moving.
            _exposure.speedUp.Override(1.2f);
            _exposure.speedDown.Override(0.6f);
            _exposure.minLuminance.Override(-3f);
            _exposure.maxLuminance.Override(3f);

            // Our own effect, from the shipped bundle. Registered with the stack first: it scans
            // for effect types once and would otherwise never have seen this one.
            CompositeShader.Load();
            CompositeShader.RegisterWithStack();
            if (_layer != null) _layer.InitBundles();

            // Both stages are built; only one ever runs. Which one decides whether the HUD and
            // the horizon go through the link along with the picture.
            _composite = ScriptableObject.CreateInstance<CompositeVideo>();
            _compositeOpaque = ScriptableObject.CreateInstance<CompositeVideoOpaque>();
            InitComposite(_composite);
            InitComposite(_compositeOpaque);

            // Priority well above anything the game is likely to use, so our overrides win.
            // Only the parameters we actually override are affected; the rest of the game's
            // grading blends through untouched.
            _volume = PostProcessManager.instance.QuickVolume(
                VolumeLayerIndex(), 1000f,
                _grading, _aberration, _distortion, _bloom, _exposure, _composite, _compositeOpaque);

            FpvGogglesPlugin.Log.LogInfo("Analog image processing volume created.");
        }

        /// <summary>
        /// The volume object has to sit on a layer the camera's PostProcessLayer is actually
        /// watching, otherwise it is built, valid, and completely ignored.
        /// </summary>
        private static int VolumeLayerIndex()
        {
            int mask = _layer.volumeLayer.value;
            if (mask == 0 || mask == -1) return 0;

            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0) return i;
            }

            return 0;
        }

        // ------------------------------------------------------------------
        // The camera
        // ------------------------------------------------------------------

        private static bool EnsureLayer(Camera viewCamera)
        {
            if (_layer != null && _camera == viewCamera) return true;

            _camera = viewCamera;
            _layer = viewCamera.GetComponent<PostProcessLayer>();

            if (_layer != null)
            {
                _layerIsOurs = false;
                if (!_loggedLayer)
                {
                    _loggedLayer = true;
                    FpvGogglesPlugin.Log.LogInfo("Camera '" + viewCamera.name +
                        "' already has a PostProcessLayer; adding our volume to it.");
                }
                return true;
            }

            PostProcessResources resources = FindResources();
            if (resources == null)
            {
                if (!_loggedNoResources)
                {
                    _loggedNoResources = true;
                    FpvGogglesPlugin.Log.LogWarning(
                        "Found no PostProcessResources, so image processing is unavailable. The overlay still works.");
                }
                return false;
            }

            _layer = viewCamera.gameObject.AddComponent<PostProcessLayer>();
            _layerIsOurs = true;
            _layer.Init(resources);
            _layer.volumeTrigger = viewCamera.transform;
            _layer.volumeLayer = InheritedVolumeMask();

            // Antialiasing stays off. TAA in particular reprojects using the previous frame's
            // motion, and a head-locked camera in VR is exactly the case it handles worst.
            _layer.antialiasingMode = PostProcessLayer.Antialiasing.None;

            // A whole extra full screen pass to scrub NaNs out of the image, at headset
            // resolution, guarding against a problem this camera does not have.
            _layer.stopNaNPropagation = false;

            // AddComponent already ran OnEnable, at which point there were no resources yet.
            // Toggling it makes that run again now that there are.
            _layer.enabled = false;
            _layer.enabled = true;

            FpvGogglesPlugin.Log.LogInfo("Added a PostProcessLayer to camera '" + viewCamera.name + "'.");
            return true;
        }

        /// <summary>
        /// Copy whatever the game's own layer watches. Falling back to every layer would also
        /// drag in volumes the game deliberately keeps off this camera.
        /// </summary>
        private static LayerMask InheritedVolumeMask()
        {
            PostProcessLayer[] layers = Resources.FindObjectsOfTypeAll<PostProcessLayer>();
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == _layer) continue;
                if (layers[i].volumeLayer.value != 0) return layers[i].volumeLayer;
            }

            return ~0;
        }

        private static PostProcessResources FindResources()
        {
            // Straight from a layer the game already set up, which is the copy guaranteed to
            // match this build.
            FieldInfo field = AccessTools.Field(typeof(PostProcessLayer), "m_Resources");
            if (field != null)
            {
                PostProcessLayer[] layers = Resources.FindObjectsOfTypeAll<PostProcessLayer>();
                for (int i = 0; i < layers.Length; i++)
                {
                    PostProcessResources resources = field.GetValue(layers[i]) as PostProcessResources;
                    if (resources != null) return resources;
                }
            }

            PostProcessResources[] loose = Resources.FindObjectsOfTypeAll<PostProcessResources>();
            return loose.Length > 0 ? loose[0] : null;
        }

        // ------------------------------------------------------------------

        internal static void Teardown()
        {
            if (_volume != null)
            {
                UnityEngine.Object.Destroy(_volume.gameObject);
                _volume = null;
            }

            DestroySettings();

            // A layer we added goes with us: leaving one behind keeps the camera rendering
            // through the post processing path for nothing, and any volume the game happens to
            // have would then apply to a camera it was never meant for. A layer the game put
            // there is left strictly alone.
            if (_layerIsOurs && _layer != null) UnityEngine.Object.Destroy(_layer);

            _layerIsOurs = false;
            _camera = null;
            _layer = null;
        }

        private static void DestroySettings()
        {
            DestroySetting(_grading); _grading = null;
            DestroySetting(_aberration); _aberration = null;
            DestroySetting(_distortion); _distortion = null;
            DestroySetting(_bloom); _bloom = null;
            DestroySetting(_exposure); _exposure = null;
            DestroySetting(_composite); _composite = null;
            DestroySetting(_compositeOpaque); _compositeOpaque = null;
            FpvGogglesPlugin.CompositeRunning = false;
        }

        private static void DestroySetting(PostProcessEffectSettings settings)
        {
            if (settings != null) UnityEngine.Object.Destroy(settings);
        }
    }
}
