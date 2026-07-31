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

    /// <summary>
    /// Colours for the horizon indicator, in the order the cycle key walks through them.
    /// White reads well over grass and tarmac and vanishes against a bright sky or a concrete
    /// wall, which is the whole reason this exists.
    /// </summary>
    public enum IndicatorColour
    {
        White = 0,
        Green = 1,
        Red = 2,
        Yellow = 3,
        Off = 4,
    }

    [BepInPlugin(Guid, "Liftoff FPV Goggles", "4.10.0")]
    [BepInDependency("raicuparta.uuvr-modern", BepInDependency.DependencyFlags.HardDependency)]
    public class FpvGogglesPlugin : BaseUnityPlugin
    {
        public const string Guid = "maxwo.liftoff.fpvgoggles";

        internal static ManualLogSource Log;

        /// <summary>
        /// The settings file itself, so the menu can build itself out of the entries rather than
        /// listing them a second time and drifting apart from them.
        /// </summary>
        internal static ConfigFile Configuration;

        // --- General ---
        internal static ConfigEntry<bool> KeepVrOffInMenus;
        internal static ConfigEntry<string> MenuSceneNames;
        internal static ConfigEntry<HotKey> ToggleMenuKey;
        internal static ConfigEntry<string> ActiveProfile;

        // --- Head tracking (the actual point of this mod) ---
        internal static ConfigEntry<bool> LockRotation;
        internal static ConfigEntry<bool> LockPosition;

        // --- HUD ---
        internal static ConfigEntry<bool> HudOnVrPlane;
        internal static ConfigEntry<HotKey> UiSmallerKey;
        internal static ConfigEntry<HotKey> UiBiggerKey;
        internal static ConfigEntry<string> HideHudElements;
        internal static ConfigEntry<HotKey> DumpHudKey;

        // --- Centering ---
        internal static ConfigEntry<HotKey> UiLeftKey;
        internal static ConfigEntry<HotKey> UiRightKey;
        internal static ConfigEntry<HotKey> UiUpKey;
        internal static ConfigEntry<HotKey> UiDownKey;

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
        internal static ConfigEntry<IndicatorColour> HorizonColour;
        internal static ConfigEntry<HotKey> HorizonColourKey;
        internal static ConfigEntry<float> HorizonOutline;

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

        // --- Analog image (post processing) ---
        internal static ConfigEntry<bool> ImageProcessing;
        internal static ConfigEntry<float> Saturation;
        internal static ConfigEntry<float> ColourLoss;
        internal static ConfigEntry<float> Contrast;
        internal static ConfigEntry<float> Temperature;
        internal static ConfigEntry<float> Aberration;
        internal static ConfigEntry<float> Distortion;
        internal static ConfigEntry<float> BloomIntensity;
        internal static ConfigEntry<bool> AutoExposure;

        // --- Composite video (the shipped shader) ---
        internal static ConfigEntry<bool> CompositeEnabled;
        internal static ConfigEntry<int> SignalLines;
        internal static ConfigEntry<float> SubcarrierFrequency;
        internal static ConfigEntry<float> SignalNoise;
        internal static ConfigEntry<float> LineJitter;
        internal static ConfigEntry<float> ChromaBleed;
        internal static ConfigEntry<float> ChromaGain;
        internal static ConfigEntry<float> CompositeSoftness;
        internal static ConfigEntry<bool> CompositeAffectsHud;

        internal static Type PoseDriverType;
        internal static PropertyInfo DisablePositionalTrackingProperty;

        /// <summary>
        /// Set by the runner from the active scene name. Starts true so VR is held off from
        /// the very first frame, before any scene has been seen.
        /// </summary>
        internal static bool InMenu = true;

        /// <summary>Set by the runner from XRSettings.enabled.</summary>
        internal static bool VrActive;

        /// <summary>
        /// True while the composite pass is actually rendering. The overlay reads it to drop its
        /// own static, because noise mixed into the signal before decoding is the real article
        /// and painting more on top would only bury it.
        ///
        /// It lives here rather than on AnalogPostFx on purpose. That class has post processing
        /// types in its fields, so merely reading a flag from it would load them - and on a
        /// Liftoff without the package that throws, from inside the overlay, which is not
        /// guarded against it.
        /// </summary>
        internal static bool CompositeRunning;

        // Hotkey toggles are session-only: null means "follow the config file". Otherwise a
        // quick A/B comparison during one flight would silently become the permanent setting.
        internal static bool? SessionMask;
        internal static bool? SessionAnalog;

        internal static bool BaseRotationLocked
        {
            get { return LockRotation != null && LockRotation.Value; }
        }

        internal static bool BasePositionLocked
        {
            get { return LockPosition != null && LockPosition.Value; }
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
            Configuration = Config;

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

            // A menu beats a key for anything you set once and then leave alone, and with a
            // headset on it beats it by a mile - you cannot see the keyboard. Everything that
            // used to have its own key is in there, so those keys default to unbound.
            ToggleMenuKey = Config.Bind(
                "General", "Settings Menu Key", HotKey.F10,
                "Opens a settings menu you can click through with the mouse. It appears in the headset as well, because UUVR puts it on the same plane as the game's own interface.");

            // Only the name. The settings themselves stay in this file, exactly as they were
            // before profiles existed - a profile is a copy you can go back to, not a layer the
            // config now depends on. Delete the profiles file and everything still works.
            ActiveProfile = Config.Bind(
                "General", "Active Profile", "",
                "The profile last chosen in the settings menu. Empty means the settings that ship with the mod. Profiles themselves live in a file of their own next to this one.");

            // ---------------- HUD ----------------

            // CanvasRedirect puts only the game's canvases on the VR plane, so the HUD can be
            // resized without touching the world behind it. It must not be left on outside a
            // VR flight: it re-parents the canvases to UUVR's capture camera, and when that
            // camera is not rendering, the menu becomes invisible - flat screen included.
            HudOnVrPlane = Config.Bind(
                "HUD", "HUD On VR Plane In Flight", true,
                "During a VR flight, put the game's HUD on UUVR's UI plane so it can be resized. Outside a VR flight the capture mode is forced back to None, which keeps menus working.");

            // Unbound since the menu arrived. Sizing the HUD is something you do once, and
            // hunting for a key with a headset on is the thing the menu exists to avoid. Bind a
            // free key here if you would rather have it under your fingers.
            UiSmallerKey = Config.Bind("HUD", "Shrink HUD Key", HotKey.None, "Makes the HUD plane smaller. In the menu as well.");
            UiBiggerKey = Config.Bind("HUD", "Grow HUD Key", HotKey.None, "Makes the HUD plane bigger. In the menu as well.");

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
            UiLeftKey = Config.Bind("Centering", "Move HUD Left Key", HotKey.None,
                "Shifts the HUD plane left (UUVR's 'VR UI Position'). In the menu as well.");
            UiRightKey = Config.Bind("Centering", "Move HUD Right Key", HotKey.None,
                "Shifts the HUD plane right. In the menu as well.");
            UiUpKey = Config.Bind("Centering", "Move HUD Up Key", HotKey.None,
                "Shifts the HUD plane up. In the menu as well.");
            UiDownKey = Config.Bind("Centering", "Move HUD Down Key", HotKey.None,
                "Shifts the HUD plane down. In the menu as well.");

            // ---------------- Optional: goggle field of view ----------------

            MaskEnabled = Config.Bind(
                "Goggle Mask", "Enable Mask", false,
                "Draws a black border that narrows the image down to the field of view of a real goggle, and makes that window the area the analog artefacts are drawn on. Note what it does not do: it cuts a hole into a picture still rendered at headset field of view, so you see less of the world at the same scale - it does not squeeze a wide angle lens onto a small screen.");

            // F10 belongs to the menu now, and the mask is a switch in there.
            ToggleMaskKey = Config.Bind("Goggle Mask", "Toggle Key", HotKey.None,
                "Toggles the goggle mask during a flight. Session only, so it never ends up in this file. In the menu as well.");

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
                "Horizon", "Scale", 0.5f,
                new ConfigDescription("Master size factor over width and thickness together, so the whole indicator can be resized with one key.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            HorizonBiggerKey = Config.Bind("Horizon", "Grow Key", HotKey.None,
                "Makes the horizon indicator bigger. In the menu as well.");
            HorizonSmallerKey = Config.Bind("Horizon", "Shrink Key", HotKey.None,
                "Makes the horizon indicator smaller. In the menu as well.");

            // A single colour cannot work everywhere: white is unreadable against a bright sky
            // or a concrete hall, and the map decides that, not the pilot. So it cycles - and
            // unlike the session-only toggles this one is remembered, because it is a
            // preference rather than a comparison.
            HorizonColour = Config.Bind(
                "Horizon", "Colour", IndicatorColour.White,
                "Colour of the horizon indicator. 'Off' hides it without switching the feature off, so the cycle key can walk past it.");

            HorizonColourKey = Config.Bind(
                "Horizon", "Cycle Colour Key", HotKey.F9,
                "Steps through white, green, red, yellow and off. Saved to this file, unlike the session-only toggles.");

            // A thin bright line on a noisy picture is the one thing an OSD must not be, and
            // real ones solve it the same way: a black edge around every character.
            HorizonOutline = Config.Bind(
                "Horizon", "Outline Width", 1f,
                new ConfigDescription("Black edge drawn behind the indicator, as a multiple of the line thickness. This is what keeps it readable over static and over a bright sky. 0 switches it off.",
                    new AcceptableValueRange<float>(0f, 4f)));

            // ---------------- Analog video ----------------

            // What is drawn here are flat quads laid over the picture. That rules out anything
            // which has to read the rendered image back - desaturation, chroma smear, tearing -
            // because those need a custom shader, and a shader cannot be created from inside a
            // plugin. It would have to be shipped as an AssetBundle built in Liftoff's own Unity
            // version, and would break every time that version moves.
            AnalogEnabled = Config.Bind(
                "Analog Video", "Enable Analog Video", true,
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
                "Analog Video", "Base Grain", 0.02f,
                new ConfigDescription("Grain that stays on the picture even at full signal. Analog is never completely clean, and a picture without it looks digital - but a clean link should be quiet enough that you stop noticing it.",
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

            // ---------------- Analog image ----------------

            // This half runs through Liftoff's own copy of the Post Processing Stack v2, so it
            // reads the rendered image back and can do what an overlay cannot: colour, lens
            // shape, blown highlights, exposure. Costs nothing to ship - the game already uses
            // the package, so its shaders are in the build.
            ImageProcessing = Config.Bind(
                "Analog Image", "Enable Image Processing", true,
                "Runs the picture through the game's own post processing: colour response, chroma fringing, lens distortion, bloom and automatic exposure. Needs 'Enable Analog Video' as well. Switch this off if the image processing misbehaves - the overlay keeps working on its own.");

            Saturation = Config.Bind(
                "Analog Image", "Saturation", -20f,
                new ConfigDescription("Colour saturation at full signal. Analog is washed out next to a digital feed; negative values take colour away.",
                    new AcceptableValueRange<float>(-100f, 100f)));

            // The chroma subcarrier is the first thing a weak link loses, so a fading picture
            // turns black and white long before it turns to snow.
            ColourLoss = Config.Bind(
                "Analog Image", "Colour Loss", 1f,
                new ConfigDescription("How much of the colour dies with the signal. At 1 a lost link leaves a black and white picture, which is what really happens - the colour goes first and the brightness stays readable.",
                    new AcceptableValueRange<float>(0f, 1f)));

            Contrast = Config.Bind(
                "Analog Image", "Contrast", 10f,
                new ConfigDescription("Analog cameras are contrastier than a clean render, and lose the shadows for it.",
                    new AcceptableValueRange<float>(-100f, 100f)));

            Temperature = Config.Bind(
                "Analog Image", "Colour Temperature", 10f,
                new ConfigDescription("White balance. Cheap FPV cameras run warm; negative is cooler.",
                    new AcceptableValueRange<float>(-100f, 100f)));

            Aberration = Config.Bind(
                "Analog Image", "Chromatic Aberration", 0.35f,
                new ConfigDescription("Colour fringing at edges - a small plastic lens plus a composite signal. The closest this can get to real chroma smear without a custom shader.",
                    new AcceptableValueRange<float>(0f, 1f)));

            Distortion = Config.Bind(
                "Analog Image", "Lens Distortion", -20f,
                new ConfigDescription("Barrel distortion of the FPV lens. Negative bulges outwards, which is the direction a wide angle lens bends.",
                    new AcceptableValueRange<float>(-100f, 100f)));

            // Lower than it looks like it should be, because composite video runs before this
            // and its noise goes through the bloom as well. On a bright sky that is what turns
            // the whole picture into a white haze.
            BloomIntensity = Config.Bind(
                "Analog Image", "Bloom", 0.45f,
                new ConfigDescription("How hard bright spots blow out. Analog cameras handle a light source or a bright sky far worse than a render does. Keep it low with composite video on - the static passes through here too and a bright sky will wash the whole picture out.",
                    new AcceptableValueRange<float>(0f, 3f)));

            // The single most recognisable behaviour of a cheap FPV camera: point it at the sky
            // and everything else goes black, then slowly claws its way back.
            AutoExposure = Config.Bind(
                "Analog Image", "Auto Exposure", true,
                "Gain control that hunts for an exposure, the way an FPV camera does when you pitch up into the sky and back down again.");

            // ---------------- Composite video ----------------

            // The only part of this mod that ships a compiled shader, because it is the only
            // part that cannot be expressed as settings on somebody else's effect. It encodes
            // the picture into an analog signal, spoils it, and decodes it again - the artefacts
            // are what is left over rather than something drawn on.
            CompositeEnabled = Config.Bind(
                "Composite Video", "Enable Composite Video", true,
                "Runs the picture through a real composite video encode and decode: dot crawl, rainbow patterns on fine detail, colour smearing sideways, and colour dying before the picture does. Needs the 'fpvanalog' file next to the plugin DLL. Switch it off to get the 4.8 look back.");

            // Not 480, even though that is what analog video has. Those 480 lines fill a 46
            // degree goggle - about ten lines per degree - while a headset spreads the picture
            // over roughly a hundred degrees. Matching the real angular sharpness across that
            // much view takes about a thousand. Turn the goggle mask on and 480 becomes correct
            // again, because then the picture only fills 46 degrees, as it does on the bench.
            SignalLines = Config.Bind(
                "Composite Video", "Signal Lines", 1000,
                new ConfigDescription("Lines in the emulated signal, and the resolution the decode runs at - so this is the frame rate knob as well as the sharpness knob. Analog video has 480 lines, but they fill a 46 degree goggle; across a headset's much wider view it takes about a thousand to look equally sharp. Use 480 if you switch the goggle mask on. 0 means full headset resolution, which is expensive and no more accurate.",
                    new AcceptableValueRange<int>(0, 1600)));

            // Cycles across one line. Fewer cycles means larger, more obvious artefacts; more
            // cycles is closer to broadcast NTSC, where 227.5 is the real figure.
            // The real NTSC figure, and it is not just for authenticity. The subcarrier sits at
            // the top of the brightness band there, where there is least detail to interfere
            // with. Putting it lower - as the first version did at 170 - drops it into the
            // middle of the band, and every artefact gets coarser and more obvious for it.
            SubcarrierFrequency = Config.Bind(
                "Composite Video", "Subcarrier Frequency", 227.5f,
                new ConfigDescription("Colour subcarrier cycles across one line, which sets how fine the artefacts are. 227.5 is the real NTSC figure and puts the carrier where it interferes least. Lower makes dot crawl coarser and edge echoes wider.",
                    new AcceptableValueRange<float>(40f, 400f)));

            // Noise on a composite signal goes much further than noise painted over a picture:
            // the decoder turns one number into speckle, colour blotches and lost colour at
            // once. The first value carried over from the overlay was far too high for that.
            SignalNoise = Config.Bind(
                "Composite Video", "Signal Noise", 0.18f,
                new ConfigDescription("Noise mixed into the signal once the link is gone. It goes in before decoding, so it becomes speckle, colour blotches and lost colour all at once - a little goes a long way. Raise it if a dead link should be unflyable rather than just ugly.",
                    new AcceptableValueRange<float>(0f, 2f)));

            // A fraction of the picture width, per line, and it reads as sharp edges bending.
            // At 0.02 that is two per cent of the screen - forty pixels of sideways slide on
            // every line, which is a broken cable rather than a weak signal.
            LineJitter = Config.Bind(
                "Composite Video", "Line Jitter", 0.004f,
                new ConfigDescription("How far lines slide sideways when the signal is bad, as a fraction of the picture width. This is the horizontal tearing an overlay cannot do, and it is what makes straight edges look bent. Very small numbers go a long way.",
                    new AcceptableValueRange<float>(0f, 0.05f)));

            ChromaBleed = Config.Bind(
                "Composite Video", "Chroma Bleed", 0.8f,
                new ConfigDescription("How much wider the colour is smeared than the brightness. Composite gives colour far less bandwidth than luma, which is why analog colour runs past edges.",
                    new AcceptableValueRange<float>(0f, 1f)));

            ChromaGain = Config.Bind(
                "Composite Video", "Chroma Gain", 1f,
                new ConfigDescription("Gain on the decoded colour. Below 1 washes out, above 1 oversaturates the way a badly adjusted receiver does.",
                    new AcceptableValueRange<float>(0f, 2f)));

            // A receiver recovers brightness by subtracting the colour it just decoded from the
            // untouched signal, not by averaging the signal - which is why it stays sharp.
            // This blends back towards the averaged version for anyone who wants it softer.
            CompositeSoftness = Config.Bind(
                "Composite Video", "Softness", 0f,
                new ConfigDescription("How much brightness detail is given up. 0 is as sharp as the emulation gets, 1 averages it over the whole filter window and is very soft.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CompositeAffectsHud = Config.Bind(
                "Composite Video", "Affects HUD", false,
                "Off, the decode runs before the HUD and the horizon are drawn, so those stay clean - a goggle's own display is not what came down the radio link. On, everything goes through it, which is what real hardware does with a flight controller OSD, and is harder to read.");

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

            UuvrCanvasFix.Apply();

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

            WarnAboutDuplicateKeys();

            Log.LogInfo("Liftoff FPV Goggles ready.");
        }

        /// <summary>
        /// Two settings on one key means one press quietly does both things, and the second one
        /// is always the one you forgot about. Defaults change between versions while config
        /// files do not, so this is exactly where it happens.
        /// </summary>
        private void WarnAboutDuplicateKeys()
        {
            ConfigEntry<HotKey>[] keys =
            {
                UiSmallerKey, UiBiggerKey, DumpHudKey,
                UiLeftKey, UiRightKey, UiUpKey, UiDownKey,
                ToggleMaskKey, FovUpKey, FovDownKey,
                HorizonBiggerKey, HorizonSmallerKey, HorizonColourKey,
                ToggleAnalogKey,
            };

            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] == null || keys[i].Value == HotKey.None) continue;

                for (int j = i + 1; j < keys.Length; j++)
                {
                    if (keys[j] == null || keys[j].Value != keys[i].Value) continue;

                    Log.LogWarning("Both '" + keys[i].Definition.Section + "/" + keys[i].Definition.Key +
                        "' and '" + keys[j].Definition.Section + "/" + keys[j].Definition.Key +
                        "' are bound to " + keys[i].Value + ", so one press does both.");
                }
            }
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
