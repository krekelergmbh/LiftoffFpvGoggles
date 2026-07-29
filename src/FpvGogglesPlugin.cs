using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// Virtual key codes, used directly as enum values so we can feed them to
    /// GetAsyncKeyState. UUVR reads the keyboard the same way (P/Invoke instead of
    /// UnityEngine.Input), which keeps working no matter which input system the game uses -
    /// and Liftoff drives its input through Rewired, so UnityEngine.Input is not an option.
    ///
    /// The catch: the game receives the same key press. Anything Liftoff binds itself will do
    /// both things at once, which rules out most F-keys for our purposes.
    /// </summary>
    public enum HotKey
    {
        None = 0,
        F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
        F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
        Insert = 0x2D, Delete = 0x2E, Home = 0x24, End = 0x23,
        PageUp = 0x21, PageDown = 0x22,
        ArrowLeft = 0x25, ArrowUp = 0x26, ArrowRight = 0x27, ArrowDown = 0x28,
        NumPad0 = 0x60, NumPad1 = 0x61, NumPad2 = 0x62, NumPad3 = 0x63, NumPad4 = 0x64,
        NumPad5 = 0x65, NumPad6 = 0x66, NumPad7 = 0x67, NumPad8 = 0x68, NumPad9 = 0x69,
        NumPadMultiply = 0x6A, NumPadPlus = 0x6B, NumPadMinus = 0x6D, NumPadDivide = 0x6F,
    }

    [BepInPlugin(Guid, "Liftoff FPV Goggles", "4.8.0")]
    [BepInDependency("raicuparta.uuvr-modern", BepInDependency.DependencyFlags.HardDependency)]
    public class FpvGogglesPlugin : BaseUnityPlugin
    {
        public const string Guid = "maxwo.liftoff.fpvgoggles";

        internal static ManualLogSource Log;

        // --- General ---
        internal static ConfigEntry<bool> KeepVrOffInMenus;
        internal static ConfigEntry<string> MenuSceneNames;

        // --- Head tracking (the actual point of this mod) ---
        internal static ConfigEntry<bool> LockRotation;
        internal static ConfigEntry<bool> LockPosition;
        internal static ConfigEntry<HotKey> ToggleTrackingKey;

        // --- HUD ---
        internal static ConfigEntry<bool> HudOnVrPlane;
        internal static ConfigEntry<HotKey> UiSmallerKey;
        internal static ConfigEntry<HotKey> UiBiggerKey;
        internal static ConfigEntry<float> UiScaleStep;
        internal static ConfigEntry<string> HideHudElements;
        internal static ConfigEntry<HotKey> DumpHudKey;

        // --- Centering ---
        internal static ConfigEntry<HotKey> UiLeftKey;
        internal static ConfigEntry<HotKey> UiRightKey;
        internal static ConfigEntry<HotKey> UiUpKey;
        internal static ConfigEntry<HotKey> UiDownKey;
        internal static ConfigEntry<float> OffsetStep;

        // --- Optional: goggle field of view ---
        internal static ConfigEntry<bool> MaskEnabled;
        internal static ConfigEntry<HotKey> ToggleMaskKey;
        internal static ConfigEntry<float> MaskFovDiagonal;
        internal static ConfigEntry<float> MaskAspectWidth;
        internal static ConfigEntry<float> MaskAspectHeight;
        internal static ConfigEntry<float> MaskDistance;
        internal static ConfigEntry<HotKey> FovUpKey;
        internal static ConfigEntry<HotKey> FovDownKey;
        internal static ConfigEntry<float> FovStep;

        // --- Horizon indicator ---
        internal static ConfigEntry<bool> HorizonEnabled;
        internal static ConfigEntry<float> HorizonRange;
        internal static ConfigEntry<float> HorizonWidth;
        internal static ConfigEntry<float> HorizonGap;
        internal static ConfigEntry<float> HorizonThickness;
        internal static ConfigEntry<float> HorizonScale;
        internal static ConfigEntry<float> HorizonCameraTilt;
        internal static ConfigEntry<HotKey> HorizonBiggerKey;
        internal static ConfigEntry<HotKey> HorizonSmallerKey;

        // --- Analog video ---
        internal static ConfigEntry<bool> AnalogEnabled;
        internal static ConfigEntry<HotKey> ToggleAnalogKey;
        internal static ConfigEntry<float> StaticStrength;
        internal static ConfigEntry<float> BaseGrain;
        internal static ConfigEntry<float> ScanlineStrength;
        internal static ConfigEntry<int> ScanlineCount;
        internal static ConfigEntry<float> VignetteStrength;
        internal static ConfigEntry<float> SignalRange;
        internal static ConfigEntry<bool> ObstaclesBlock;
        internal static ConfigEntry<bool> AntennaNull;
        internal static ConfigEntry<bool> Breakup;
        internal static ConfigEntry<bool> LogSignal;

        // --- Optional: full simulation ---
        internal static ConfigEntry<bool> VirtualScreen;
        internal static ConfigEntry<float> LensFovDiagonal;
        internal static ConfigEntry<int> CaptureHeight;
        internal static ConfigEntry<bool> FlipScreen;
        internal static ConfigEntry<HotKey> LensFovUpKey;
        internal static ConfigEntry<HotKey> LensFovDownKey;

        internal static Type PoseDriverType;
        internal static PropertyInfo DisablePositionalTrackingProperty;

        /// <summary>
        /// Set by the runner from the active scene name. Starts true so VR is held off from
        /// the very first frame, before any scene has been seen.
        /// </summary>
        internal static bool InMenu = true;

        /// <summary>Set by the runner from XRSettings.enabled.</summary>
        internal static bool VrActive;

        // Hotkey toggles are session-only: null means "follow the config file". Otherwise a
        // quick A/B comparison during one flight would silently become the permanent setting.
        internal static bool? SessionRotationLock;
        internal static bool? SessionPositionLock;
        internal static bool? SessionMask;
        internal static bool? SessionAnalog;

        internal static bool BaseRotationLocked
        {
            get { return SessionRotationLock.HasValue ? SessionRotationLock.Value : (LockRotation != null && LockRotation.Value); }
        }

        internal static bool BasePositionLocked
        {
            get { return SessionPositionLock.HasValue ? SessionPositionLock.Value : (LockPosition != null && LockPosition.Value); }
        }

        internal static bool BaseMaskEnabled
        {
            get { return SessionMask.HasValue ? SessionMask.Value : (MaskEnabled != null && MaskEnabled.Value); }
        }

        internal static bool BaseAnalogEnabled
        {
            get { return SessionAnalog.HasValue ? SessionAnalog.Value : (AnalogEnabled != null && AnalogEnabled.Value); }
        }

        internal static bool HeadRotationLocked { get { return BaseRotationLocked && !InMenu; } }

        internal static bool HeadPositionLocked { get { return BasePositionLocked && !InMenu; } }

        /// <summary>
        /// Goggle rendering only makes sense during a VR flight. UUVR builds its camera
        /// components even while VR is off, so without the VrActive check the mask would be
        /// painted over the flat monitor image too.
        /// </summary>
        internal static bool GoggleRenderingActive
        {
            get { return BaseMaskEnabled && !InMenu && VrActive; }
        }

        /// <summary>
        /// Deliberately independent of the mask: the horizon is an OSD element and makes sense
        /// with or without a goggle frame around the picture.
        /// </summary>
        internal static bool HorizonActive
        {
            get { return HorizonEnabled != null && HorizonEnabled.Value && !InMenu && VrActive; }
        }

        /// <summary>
        /// Also independent of the mask: the artefacts land on whatever counts as the picture,
        /// which is the mask aperture when there is one and your whole view when there is not.
        /// </summary>
        internal static bool AnalogActive
        {
            get { return BaseAnalogEnabled && !InMenu && VrActive; }
        }

        private void Awake()
        {
            Log = Logger;

            // ---------------- General ----------------

            // UUVR forces VR on in VrTogglerManager's constructor, and Liftoff keeps
            // destroying and recreating UUVR's core - so VR comes back on by itself. Holding
            // it off in menus makes them behave as if no mod were installed, which is the
            // only reliable way to pick a track: the VR menu is cropped by an aspect ratio
            // mismatch between UUVR's capture target and the game's 16:9 layout.
            KeepVrOffInMenus = Config.Bind(
                "General", "Keep VR Off In Menus", true,
                "Holds VR off while in menus, so they behave exactly as if no VR mod were installed. During a flight this plugin never touches VR, so UUVR's F3 switches it on and it stays on.");

            MenuSceneNames = Config.Bind(
                "General", "Menu Scene Names", "menu,splash,lobby,loading",
                "Comma separated. A scene counts as a menu if its name contains any of these (case insensitive). Everything else counts as a flight. The active scene name is written to the log on every change.");

            // ---------------- Head tracking ----------------

            LockRotation = Config.Bind(
                "Head Tracking", "Disable Head Rotation", true,
                "Ignores head rotation, so the image stays glued to your head like the screen of a real FPV goggle. This is the main setting.");

            LockPosition = Config.Bind(
                "Head Tracking", "Disable Head Position", true,
                "Ignores head position, so leaning or moving your head no longer shifts the camera.");

            ToggleTrackingKey = Config.Bind(
                "Head Tracking", "Toggle Key", HotKey.F9,
                "Toggles head tracking during a flight, for comparison. Session only, never written to this file.");

            // ---------------- HUD ----------------

            // CanvasRedirect puts only the game's canvases on the VR plane, so the HUD can be
            // resized without touching the world behind it. It must not be left on outside a
            // VR flight: it re-parents the canvases to UUVR's capture camera, and when that
            // camera is not rendering, the menu becomes invisible - flat screen included.
            HudOnVrPlane = Config.Bind(
                "HUD", "HUD On VR Plane In Flight", true,
                "During a VR flight, put the game's HUD on UUVR's UI plane so it can be resized. Outside a VR flight the capture mode is forced back to None, which keeps menus working.");

            UiSmallerKey = Config.Bind("HUD", "Shrink HUD Key", HotKey.F7, "Makes the HUD plane smaller.");
            UiBiggerKey = Config.Bind("HUD", "Grow HUD Key", HotKey.F8, "Makes the HUD plane bigger.");
            UiScaleStep = Config.Bind("HUD", "HUD Scale Step", 0.1f,
                new ConfigDescription("How much one key press changes UUVR's 'VR UI Scale'.",
                    new AcceptableValueRange<float>(0.02f, 1f)));

            // A real analog goggle shows no crosshair, so being able to drop single HUD parts
            // is a realism feature, not just cosmetics. Names have to be read from the game
            // rather than guessed - hence the dump key.
            HideHudElements = Config.Bind(
                "HUD", "Hide Elements", "",
                "Comma separated GameObject names to hide from the HUD during a flight. Press the dump key to write the HUD tree to the log and read the real names from there. Names must match exactly (case insensitive); add a * for substring matching, e.g. 'Recording*'. Useful ones in Liftoff: Center (the crosshair), ArmedDisplay (crosshair plus drone name), AxisVisualizer (the stick crosses), RecordingIndicator, XSDroneHUD (everything).");

            // Unbound: this was the tool for finding element names, and that job is done.
            // Bind a free key again if you need to look up more names.
            DumpHudKey = Config.Bind(
                "HUD", "Dump HUD Tree Key", HotKey.None,
                "Writes the names of all HUD objects to the BepInEx log, so you can find out what to put into 'Hide Elements'. Note that elements which only appear once the drone is armed are only in the tree if you dump while armed.");

            // ---------------- Centering ----------------

            // Deliberately not F-keys: Liftoff binds several of those itself, and since we
            // read the keyboard past the game, both react to the same press.
            //
            // Only the HUD plane can be moved. Shifting the whole VR view was tried and
            // dropped: UUVR's camera offset made no visible difference, because what sits off
            // centre is the HUD, not the world.
            UiLeftKey = Config.Bind("Centering", "Move HUD Left Key", HotKey.Home,
                "Shifts the HUD plane left (UUVR's 'VR UI Position').");
            UiRightKey = Config.Bind("Centering", "Move HUD Right Key", HotKey.End,
                "Shifts the HUD plane right.");
            UiUpKey = Config.Bind("Centering", "Move HUD Up Key", HotKey.PageUp,
                "Shifts the HUD plane up.");
            UiDownKey = Config.Bind("Centering", "Move HUD Down Key", HotKey.PageDown,
                "Shifts the HUD plane down.");
            OffsetStep = Config.Bind("Centering", "Offset Step", 0.02f,
                new ConfigDescription("Metres per key press.", new AcceptableValueRange<float>(0.002f, 0.2f)));

            // ---------------- Optional: goggle field of view ----------------

            MaskEnabled = Config.Bind(
                "Goggle Mask", "Enable Mask", false,
                "Draws a black border that narrows the image down to the field of view of a real goggle. Note what this does and does not do: it cuts a hole into a picture still rendered at headset field of view, so you see less of the world - it does not squeeze a wide angle lens onto a small screen. For that, see 'Enable Virtual Screen'.");

            ToggleMaskKey = Config.Bind("Goggle Mask", "Toggle Key", HotKey.F10,
                "Toggles goggle rendering (mask, or virtual screen if enabled) during a flight. Session only.");

            MaskFovDiagonal = Config.Bind(
                "Goggle Mask", "Diagonal FOV", 46f,
                new ConfigDescription("Diagonal field of view of the visible window in degrees. Skyzone SKY04O Pro is 46.",
                    new AcceptableValueRange<float>(10f, 160f)));

            MaskAspectWidth = Config.Bind("Goggle Mask", "Aspect Width", 4f,
                new ConfigDescription("Analog video (Meteor75 Pro) is 4:3.", new AcceptableValueRange<float>(1f, 32f)));
            MaskAspectHeight = Config.Bind("Goggle Mask", "Aspect Height", 3f,
                new ConfigDescription("Analog video (Meteor75 Pro) is 4:3.", new AcceptableValueRange<float>(1f, 32f)));

            MaskDistance = Config.Bind("Goggle Mask", "Screen Distance", 3f,
                new ConfigDescription("Distance of the goggle screen in metres. Affects eye comfort only.",
                    new AcceptableValueRange<float>(0.05f, 50f)));

            // Unbound: Insert/Delete are worth more for the horizon, which is on. Pick free
            // keys here if you switch the mask back on and want to size it live.
            FovUpKey = Config.Bind("Goggle Mask", "Increase FOV Key", HotKey.None,
                "Widens the goggle window. Unbound by default.");
            FovDownKey = Config.Bind("Goggle Mask", "Decrease FOV Key", HotKey.None,
                "Narrows the goggle window. Unbound by default.");
            FovStep = Config.Bind("Goggle Mask", "FOV Step", 2f,
                new ConfigDescription("Degrees per key press.", new AcceptableValueRange<float>(0.5f, 20f)));

            // ---------------- Horizon indicator ----------------

            HorizonEnabled = Config.Bind(
                "Horizon", "Enable Horizon", true,
                "Artificial horizon in the style of a Betaflight OSD: a bar that rolls and slides with the drone's attitude, with a fixed centre mark as reference. Replaces the crosshair you would otherwise hide away.");

            HorizonRange = Config.Bind(
                "Horizon", "Pitch Range", 30f,
                new ConfigDescription("Degrees of pitch between the centre and the top edge of the window. Smaller = the bar reacts more strongly.",
                    new AcceptableValueRange<float>(5f, 90f)));

            // Liftoff lets you tilt the FPV camera upwards, and a real Betaflight OSD does not
            // know about that: it draws the craft's attitude, not the camera's. So with a 10
            // degree tilt the bar sits centred while the visible horizon is lower - exactly
            // what you get on real hardware. Set this to the angle configured in Liftoff.
            HorizonCameraTilt = Config.Bind(
                "Horizon", "Camera Tilt", 0f,
                new ConfigDescription("Upward tilt of the FPV camera in degrees, as set in Liftoff. Compensated so the bar shows the drone's attitude rather than the camera's.",
                    new AcceptableValueRange<float>(0f, 60f)));

            HorizonWidth = Config.Bind(
                "Horizon", "Bar Width", 0.45f,
                new ConfigDescription("Length of the bar as a fraction of the window half width.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            HorizonGap = Config.Bind(
                "Horizon", "Centre Gap", 0.10f,
                new ConfigDescription("Gap in the middle of the bar, as a fraction of the window half width.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            HorizonThickness = Config.Bind(
                "Horizon", "Line Thickness", 0.005f,
                new ConfigDescription("Line thickness as a fraction of the window half height.",
                    new AcceptableValueRange<float>(0.0005f, 0.1f)));

            HorizonScale = Config.Bind(
                "Horizon", "Scale", 1f,
                new ConfigDescription("Master size factor over width and thickness together, so the whole indicator can be resized with one key.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            HorizonBiggerKey = Config.Bind("Horizon", "Grow Key", HotKey.Insert, "Makes the horizon indicator bigger.");
            HorizonSmallerKey = Config.Bind("Horizon", "Shrink Key", HotKey.Delete, "Makes the horizon indicator smaller.");

            // ---------------- Analog video ----------------

            // What is drawn here are flat quads laid over the picture. That rules out anything
            // which has to read the rendered image back - desaturation, chroma smear, tearing -
            // because those need a custom shader, and a shader cannot be created from inside a
            // plugin. It would have to be shipped as an AssetBundle built in Liftoff's own Unity
            // version, and would break every time that version moves.
            AnalogEnabled = Config.Bind(
                "Analog Video", "Enable Analog Video", false,
                "Lays the artefacts of an analog video link over the picture: lens vignette, RF static, and the scanlines of the goggle screen. How much static you get depends on where you are flying - see 'Signal Range' below.");

            // F6 because the neighbours are taken: Liftoff toggles its own HUD on F4, F5, F11
            // and F12, Steam grabs F12 for screenshots, and F7 to F10 are ours already.
            ToggleAnalogKey = Config.Bind(
                "Analog Video", "Toggle Key", HotKey.F6,
                "Switches the analog look on and off during a flight, so you can compare. Session only, never written to this file.");

            StaticStrength = Config.Bind(
                "Analog Video", "Static Strength", 0.55f,
                new ConfigDescription("How much snow there is once the link is gone. Lower this first if it gets in the way of actually flying.",
                    new AcceptableValueRange<float>(0f, 1f)));

            BaseGrain = Config.Bind(
                "Analog Video", "Base Grain", 0.05f,
                new ConfigDescription("Grain that stays on the picture even at full signal. Analog is never completely clean, and a picture without it looks digital.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            ScanlineStrength = Config.Bind(
                "Analog Video", "Scanline Strength", 0.15f,
                new ConfigDescription("Darkness of the scanlines. Above about 0.3 it starts to cost you real detail.",
                    new AcceptableValueRange<float>(0f, 1f)));

            ScanlineCount = Config.Bind(
                "Analog Video", "Scanline Count", 240,
                new ConfigDescription("Lines across the height of the picture. Analog video is around 480 lines, of which you see roughly half as dark ones.",
                    new AcceptableValueRange<int>(40, 600)));

            VignetteStrength = Config.Bind(
                "Analog Video", "Vignette Strength", 0.30f,
                new ConfigDescription("Darkening towards the corners, the way a small FPV lens does it.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // Measured from where the drone spawns, which is close enough to where you would be
            // standing. Turn on 'Log Signal' and fly out until it looks the way yours does.
            SignalRange = Config.Bind(
                "Analog Video", "Signal Range", 250f,
                new ConfigDescription("Distance in metres at which the picture is about gone. The default is a middle of the road setup; a 25 mW whoop on its stock antenna is more like 120, a decent 400 mW setup several times that.",
                    new AcceptableValueRange<float>(20f, 3000f)));

            ObstaclesBlock = Config.Bind(
                "Analog Video", "Obstacles Block Signal", true,
                "Checks the line between you and the drone. Going behind a building costs you the picture, which is the single most recognisable thing about flying analog.");

            AntennaNull = Config.Bind(
                "Analog Video", "Antenna Null", true,
                "A dipole radiates nothing along its own axis, so a level drone directly above you is in the worst possible spot. Only noticeable at distance, as on real hardware.");

            Breakup = Config.Bind(
                "Analog Video", "Signal Breakup", true,
                "Short bursts where the picture tears up completely, with a sync bar rolling through. A steady amount of snow reads as a filter; the breakup is what reads as radio.");

            LogSignal = Config.Bind(
                "Analog Video", "Log Signal", false,
                "Writes link quality and distance to the BepInEx log every two seconds. Use it to set 'Signal Range' to something that matches your own gear.");

            // ---------------- Optional: full simulation ----------------

            VirtualScreen = Config.Bind(
                "FPV Screen", "Enable Virtual Screen", false,
                "OPTIONAL, needs 'Enable Mask' as well. Renders the drone camera with a real lens field of view into a texture and shows it on a flat screen in front of your eyes - a wide angle image squeezed onto a small screen, like a real goggle.");

            LensFovDiagonal = Config.Bind(
                "FPV Screen", "Camera Lens FOV", 120f,
                new ConfigDescription("Diagonal field of view of the drone camera. Typical FPV cameras are 120-150.",
                    new AcceptableValueRange<float>(30f, 175f)));

            CaptureHeight = Config.Bind(
                "FPV Screen", "Capture Height", 720,
                new ConfigDescription("Vertical resolution the drone camera renders at. Analog video is around 480 lines.",
                    new AcceptableValueRange<int>(240, 2160)));

            FlipScreen = Config.Bind("FPV Screen", "Flip Screen Vertically", false,
                "Only needed if the picture ends up upside down.");

            LensFovUpKey = Config.Bind("FPV Screen", "Increase Lens FOV Key", HotKey.None,
                "Unbound by default - pick a free key if you switch the virtual screen on.");
            LensFovDownKey = Config.Bind("FPV Screen", "Decrease Lens FOV Key", HotKey.None,
                "Unbound by default.");

            // ---------------- Patches ----------------

            PoseDriverType = AccessTools.TypeByName("Uuvr.UuvrPoseDriver");
            if (PoseDriverType == null)
            {
                Log.LogError("Could not find Uuvr.UuvrPoseDriver. Head tracking will NOT be disabled. Is UUVR installed?");
            }
            else
            {
                MethodInfo target = AccessTools.Method(PoseDriverType, "UpdateTransform");
                if (target == null)
                {
                    Log.LogError("Could not find Uuvr.UuvrPoseDriver.UpdateTransform. Head tracking will NOT be disabled.");
                }
                else
                {
                    MethodInfo prefix = AccessTools.Method(typeof(FpvGogglesPlugin), "UpdateTransformPrefix");
                    new Harmony(Guid).Patch(target, new HarmonyMethod(prefix));
                    Log.LogInfo("Patched Uuvr.UuvrPoseDriver.UpdateTransform - head rotation is under our control now.");
                }
            }

            Type inputTracking = AccessTools.TypeByName("UnityEngine.XR.InputTracking");
            if (inputTracking != null)
            {
                DisablePositionalTrackingProperty = inputTracking.GetProperty(
                    "disablePositionalTracking", BindingFlags.Public | BindingFlags.Static);
            }
            if (DisablePositionalTrackingProperty == null)
            {
                Log.LogWarning("Could not find InputTracking.disablePositionalTracking. Positional head tracking cannot be disabled.");
            }

            // Everything that needs a per-frame Update lives on its own self-healing
            // GameObject, because Liftoff destroys injected objects - including the BepInEx
            // manager object this component sits on. UUVR hits the same wall and solves it
            // the same way.
            FpvGogglesRunner.Create();

            Log.LogInfo("Liftoff FPV Goggles ready.");
        }

        /// <summary>
        /// Replaces UUVR's "copy the headset rotation onto the camera" with "stay aligned
        /// with the parent camera", which is the drone camera. Returning false skips the
        /// original method entirely.
        /// </summary>
        private static bool UpdateTransformPrefix(object __instance)
        {
            if (!HeadRotationLocked) return true;

            Component component = __instance as Component;
            if (component == null) return true;

            component.transform.localRotation = Quaternion.identity;
            return false;
        }
    }
}
