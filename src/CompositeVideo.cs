using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// The composite video pass: a custom effect plugged into the game's own post processing
    /// stack, running a shader shipped alongside the plugin as an AssetBundle.
    ///
    /// Everything else in this mod paints artefacts on top of a finished picture. This encodes
    /// the picture into an analog signal, spoils that signal, and decodes it again - so the
    /// artefacts are not drawn, they are what is left over. See the shader for the detail.
    /// </summary>
    public abstract class CompositeVideoSettings : PostProcessEffectSettings
    {
        public FloatParameter subcarrier = new FloatParameter { value = 170f };
        public FloatParameter lines = new FloatParameter { value = 480f };
        public FloatParameter noise = new FloatParameter { value = 0f };
        public FloatParameter saturation = new FloatParameter { value = 1f };
        public FloatParameter chromaBleed = new FloatParameter { value = 0.8f };
        public FloatParameter jitter = new FloatParameter { value = 0f };
        public FloatParameter softness = new FloatParameter { value = 0.15f };
        public FloatParameter seed = new FloatParameter { value = 0f };

        public override bool IsEnabledAndSupported(PostProcessRenderContext context)
        {
            return enabled.value && CompositeShader.Shader != null;
        }
    }

    /// <summary>
    /// Runs before transparent geometry is drawn, which leaves the HUD and the artificial
    /// horizon out of it - they are transparent surfaces and get drawn afterwards, clean. This
    /// is the default: a goggle's own display is not what came down the radio link.
    /// </summary>
    [PostProcess(typeof(CompositeVideoOpaqueRenderer), PostProcessEvent.BeforeTransparent, "FPV/Composite Video (Opaque)")]
    public sealed class CompositeVideoOpaque : CompositeVideoSettings { }

    /// <summary>
    /// Runs at the end instead, so everything on screen goes through the link, OSD included -
    /// which is what happens on real hardware, where the flight controller draws the OSD before
    /// the transmitter. Accurate, and harder to read.
    ///
    /// Two types rather than one setting because the stage is baked into the attribute and
    /// cannot be changed once the stack has read it.
    /// </summary>
    [PostProcess(typeof(CompositeVideoRenderer), PostProcessEvent.AfterStack, "FPV/Composite Video")]
    public sealed class CompositeVideo : CompositeVideoSettings { }

    public sealed class CompositeVideoOpaqueRenderer : PostProcessEffectRenderer<CompositeVideoOpaque>
    {
        public override void Render(PostProcessRenderContext context)
        {
            CompositeVideoPass.Render(context, settings);
        }
    }

    public sealed class CompositeVideoRenderer : PostProcessEffectRenderer<CompositeVideo>
    {
        public override void Render(PostProcessRenderContext context)
        {
            CompositeVideoPass.Render(context, settings);
        }
    }

    internal static class CompositeVideoPass
    {
        private static readonly int SmallRt = Shader.PropertyToID("_FpvCompositeSmall");

        private static readonly int SubcarrierId = Shader.PropertyToID("_Subcarrier");
        private static readonly int LinesId = Shader.PropertyToID("_Lines");
        private static readonly int NoiseId = Shader.PropertyToID("_Noise");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        private static readonly int ChromaBleedId = Shader.PropertyToID("_ChromaBleed");
        private static readonly int JitterId = Shader.PropertyToID("_Jitter");
        private static readonly int SoftnessId = Shader.PropertyToID("_Softness");
        private static readonly int SeedId = Shader.PropertyToID("_Seed");

        internal static void Render(PostProcessRenderContext context, CompositeVideoSettings settings)
        {
            Shader shader = CompositeShader.Shader;
            if (shader == null)
            {
                context.command.BlitFullscreenTriangle(context.source, context.destination);
                return;
            }

            PropertySheet sheet = context.propertySheets.Get(shader);

            int lines = Mathf.RoundToInt(settings.lines.value);
            bool reduce = lines >= 120 && lines < context.height;

            // The emulated signal has as many lines as the buffer it is decoded into, so the
            // line structure comes out exactly right rather than being drawn on afterwards.
            sheet.properties.SetFloat(LinesId, reduce ? lines : context.height);
            sheet.properties.SetFloat(SubcarrierId, Mathf.Max(8f, settings.subcarrier.value));
            sheet.properties.SetFloat(NoiseId, settings.noise.value);
            sheet.properties.SetFloat(SaturationId, settings.saturation.value);
            sheet.properties.SetFloat(ChromaBleedId, settings.chromaBleed.value);
            sheet.properties.SetFloat(JitterId, settings.jitter.value);
            sheet.properties.SetFloat(SoftnessId, settings.softness.value);
            sheet.properties.SetFloat(SeedId, settings.seed.value);

            if (!reduce)
            {
                context.command.BlitFullscreenTriangle(context.source, context.destination, sheet, 0);
                return;
            }

            // Decoding straight into a small buffer is both the cheap option and the accurate
            // one. Analog really is about 480 lines, and the twelve weighted taps the decoder
            // already takes are a far better downscale filter than a bilinear blit would be -
            // so the reduction costs nothing extra and is done properly.
            int width = Mathf.Max(64, Mathf.RoundToInt((float)context.width / context.height * lines));

            context.GetScreenSpaceTemporaryRT(context.command, SmallRt, 0, context.sourceFormat,
                RenderTextureReadWrite.Default, FilterMode.Bilinear, width, lines);

            context.command.BlitFullscreenTriangle(context.source, SmallRt, sheet, 0);
            context.command.BlitFullscreenTriangle(SmallRt, context.destination, sheet, 1);
            context.command.ReleaseTemporaryRT(SmallRt);
        }
    }

    /// <summary>
    /// Loads the shader out of the AssetBundle next to the plugin DLL.
    ///
    /// A shader cannot be written from inside a plugin, so it is built in the editor and shipped
    /// as a bundle. That bundle is the one file in this project tied to a Unity version: built
    /// against 2022.3, the version Liftoff uses. If a future Liftoff moves far enough that the
    /// bundle stops loading, this reports it and the rest of the mod carries on.
    /// </summary>
    internal static class CompositeShader
    {
        internal const string BundleName = "fpvanalog";

        private static Shader _shader;
        private static AssetBundle _bundle;
        private static bool _tried;

        internal static Shader Shader { get { return _shader; } }

        internal static bool Load()
        {
            if (_tried) return _shader != null;
            _tried = true;

            string path = BundlePath();
            if (path == null || !File.Exists(path))
            {
                FpvGogglesPlugin.Log.LogWarning("No '" + BundleName + "' next to the plugin DLL, so composite video is unavailable. " +
                    "Re-run install.ps1, or build it with build-bundle.ps1.");
                return false;
            }

            try
            {
                _bundle = AssetBundle.LoadFromFile(path);
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Could not read the shader bundle: " + e.Message);
                return false;
            }

            if (_bundle == null)
            {
                FpvGogglesPlugin.Log.LogWarning("The shader bundle at " + path + " did not load. It was most likely built " +
                    "with a different Unity version than the game uses.");
                return false;
            }

            // By name would be tidier, but bundle asset names are lower cased full paths and one
            // rename in the editor would break it silently.
            Shader[] shaders = _bundle.LoadAllAssets<Shader>();
            if (shaders.Length == 0)
            {
                FpvGogglesPlugin.Log.LogWarning("The shader bundle loaded but contains no shader.");
                return false;
            }

            _shader = shaders[0];
            FpvGogglesPlugin.Log.LogInfo("Composite video shader loaded: '" + _shader.name + "'.");
            return true;
        }

        private static string BundlePath()
        {
            try
            {
                string dll = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(dll)) return null;
                return Path.Combine(Path.GetDirectoryName(dll), BundleName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The stack scans the loaded assemblies for effect types exactly once, the first time
        /// anything touches PostProcessManager. If the game got there before BepInEx loaded us,
        /// our effect is simply not in its list - and the volume would be built, valid, applied,
        /// and quietly do nothing. So the scan is asked to run again.
        /// </summary>
        internal static void RegisterWithStack()
        {
            try
            {
                MethodInfo reload = AccessTools.Method(typeof(PostProcessManager), "ReloadBaseTypes");
                if (reload == null)
                {
                    FpvGogglesPlugin.Log.LogWarning("Could not re-scan the post processing effect types.");
                    return;
                }

                reload.Invoke(PostProcessManager.instance, null);
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Re-scanning the post processing effect types failed: " + e.Message);
            }
        }
    }
}
