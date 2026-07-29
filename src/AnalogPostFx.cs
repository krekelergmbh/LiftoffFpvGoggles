using System;
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

        private static bool _loggedLayer, _loggedNoResources;

        internal static void Update(Camera viewCamera, float signal)
        {
            if (viewCamera == null || !FpvGogglesPlugin.ImageProcessing.Value)
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

            // Colour goes before the picture does. On a weak analog link the chroma subcarrier
            // is the first casualty, so you fly on a black and white image that is otherwise
            // perfectly readable - and getting the colour back is how you know you are clear
            // again. One line, and it is the most convincing thing in this file.
            float fade = loss * Mathf.Clamp01(FpvGogglesPlugin.ColourLoss.Value);
            _grading.saturation.value = Mathf.Lerp(FpvGogglesPlugin.Saturation.Value, -100f, fade);
            _grading.contrast.value = FpvGogglesPlugin.Contrast.Value;
            _grading.temperature.value = FpvGogglesPlugin.Temperature.Value;

            float aberration = FpvGogglesPlugin.Aberration.Value;
            _aberration.enabled.value = aberration > 0.001f;
            _aberration.intensity.value = aberration;

            float distortion = FpvGogglesPlugin.Distortion.Value;
            _distortion.enabled.value = Mathf.Abs(distortion) > 0.01f;
            _distortion.intensity.value = distortion;

            float bloom = FpvGogglesPlugin.BloomIntensity.Value;
            _bloom.enabled.value = bloom > 0.001f;
            _bloom.intensity.value = bloom;

            _exposure.enabled.value = FpvGogglesPlugin.AutoExposure.Value;
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
            _bloom.threshold.Override(1.1f);
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

            // Priority well above anything the game is likely to use, so our overrides win.
            // Only the parameters we actually override are affected; the rest of the game's
            // grading blends through untouched.
            _volume = PostProcessManager.instance.QuickVolume(
                VolumeLayerIndex(), 1000f,
                _grading, _aberration, _distortion, _bloom, _exposure);

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

            // The layer is deliberately left alone. It may well be the game's own, and pulling
            // a component off a camera we did not build it on is how you break a game.
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
        }

        private static void DestroySetting(PostProcessEffectSettings settings)
        {
            if (settings != null) UnityEngine.Object.Destroy(settings);
        }
    }
}
