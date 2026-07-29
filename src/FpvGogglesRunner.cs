using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// Everything that needs a per-frame Update.
    ///
    /// Lives on its own GameObject and recreates itself when destroyed, because Liftoff tears
    /// down injected objects ("Custom code injection detected" in the log). UUVR does the same
    /// in UuvrCore.OnDestroy - without it the whole Update loop dies silently at startup while
    /// the Harmony patches keep working, which looks very confusing from the outside.
    /// </summary>
    internal class FpvGogglesRunner : MonoBehaviour
    {
        private static FpvGogglesRunner _instance;
        private static bool _quitting;
        private static int _recreateLogBudget = 3;
        private static bool _loggedAlive;

        // Static, so a key held while the runner is recreated cannot fake a fresh press.
        private static readonly Dictionary<int, bool> _keyWasDown = new Dictionary<int, bool>();

        private string _lastSceneName;
        private float _vrEnforceTimer;
        private float _patchModeTimer;
        private float _cameraSearchTimer;
        private float _trackingReapplyTimer;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        internal static void Create()
        {
            if (_quitting) return;
            if (_instance != null) return;
            new GameObject("LiftoffFpvGoggles").AddComponent<FpvGogglesRunner>();
        }

        private void Awake() { _instance = this; }

        private void OnApplicationQuit() { _quitting = true; }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_quitting) return;

            if (_recreateLogBudget > 0)
            {
                _recreateLogBudget--;
                FpvGogglesPlugin.Log.LogInfo("Runner was destroyed by the game, recreating it (same trick UUVR uses).");
            }

            Create();
        }

        private void Update()
        {
            if (!_loggedAlive)
            {
                _loggedAlive = true;
                FpvGogglesPlugin.Log.LogInfo("Update loop is alive.");
            }

            UpdateVrIntent();
            UpdateSceneState();
            UpdateUiPatchMode();
            HandleHotkeys();

            // UUVR sets this itself on level load, so keep re-asserting it.
            _trackingReapplyTimer -= Time.unscaledDeltaTime;
            if (_trackingReapplyTimer <= 0f)
            {
                _trackingReapplyTimer = 1f;
                ApplyPositionalTracking();
            }

            UpdateGoggles();
            HideHudElements();
        }

        // ------------------------------------------------------------------
        // Hiding single HUD parts
        // ------------------------------------------------------------------

        private float _hideTimer;

        /// <summary>
        /// Re-applied on a timer rather than once: the game rebuilds and re-enables HUD parts,
        /// and hiding only in a flight keeps a stray name match from breaking a menu.
        /// </summary>
        private void HideHudElements()
        {
            if (FpvGogglesPlugin.InMenu) return;

            string configured = FpvGogglesPlugin.HideHudElements.Value;
            if (string.IsNullOrEmpty(configured)) return;

            _hideTimer -= Time.unscaledDeltaTime;
            if (_hideTimer > 0f) return;
            _hideTimer = 1f;

            string[] names = configured.Split(',');
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();

            for (int i = 0; i < canvases.Length; i++)
            {
                HideMatching(canvases[i].transform, names, 0);
            }
        }

        private static void HideMatching(Transform parent, string[] names, int depth)
        {
            if (depth > 12) return;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);

                bool hidden = false;
                for (int n = 0; n < names.Length; n++)
                {
                    if (!NameMatches(child.name, names[n].Trim())) continue;

                    if (child.gameObject.activeSelf)
                    {
                        child.gameObject.SetActive(false);
                        FpvGogglesPlugin.Log.LogInfo("Hid HUD element '" + child.name + "'.");
                    }
                    hidden = true;
                    break;
                }

                if (!hidden) HideMatching(child, names, depth + 1);
            }
        }

        /// <summary>
        /// Only what is actually on screen. Liftoff keeps whole menus parked as inactive
        /// objects in the scene, and descending into those buried the flight HUD under
        /// hundreds of irrelevant lines.
        /// </summary>
        /// <summary>
        /// Exact by default, substring only when asked for with '*'. Liftoff names HUD parts
        /// things like "Center", which as a substring would happily match objects elsewhere
        /// that have nothing to do with the crosshair.
        /// </summary>
        private static bool NameMatches(string objectName, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            if (pattern.IndexOf('*') >= 0)
            {
                string core = pattern.Replace("*", "");
                if (core.Length == 0) return false;
                return objectName.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return string.Equals(objectName, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static void DumpHudTree()
        {
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            FpvGogglesPlugin.Log.LogInfo("=== HUD tree: " + canvases.Length + " canvases, active objects only ===");

            int budget = 800;
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                bool active = canvas.gameObject.activeInHierarchy;

                FpvGogglesPlugin.Log.LogInfo("CANVAS '" + canvas.name + "' (" + canvas.renderMode +
                    (active ? "" : ", inactive - skipped") + ")");

                if (active) DumpBranch(canvas.transform, 1, ref budget);
                if (budget <= 0) break;
            }

            FpvGogglesPlugin.Log.LogInfo("=== end of HUD tree" + (budget <= 0 ? " (truncated)" : "") + " ===");
        }

        private static void DumpBranch(Transform parent, int depth, ref int budget)
        {
            if (depth > 8) return;

            for (int i = 0; i < parent.childCount; i++)
            {
                if (budget <= 0) return;

                Transform child = parent.GetChild(i);
                if (!child.gameObject.activeSelf) continue; // parked menu, not on screen

                budget--;
                FpvGogglesPlugin.Log.LogInfo(new string(' ', depth * 2) + child.name);
                DumpBranch(child, depth + 1, ref budget);
            }
        }

        // ------------------------------------------------------------------
        // Holding VR off in menus
        // ------------------------------------------------------------------

        private static bool _loggedVrOff;

        /// <summary>
        /// Keeps VR off in menus and never touches it during a flight.
        ///
        /// An earlier version tried to track whether F3 had been pressed and stopped
        /// interfering after that. That was wrong: our key polling and UUVR's are separate,
        /// so one missed press left this code fighting the toggle the pilot had just made -
        /// VR switched on and was forced back off a fraction of a second later. Tying it to
        /// the scene removes the handshake entirely.
        /// </summary>
        private void UpdateVrIntent()
        {
            FpvGogglesPlugin.VrActive = IsXrRunning();

            if (!FpvGogglesPlugin.KeepVrOffInMenus.Value) return;
            if (!FpvGogglesPlugin.InMenu) return;

            _vrEnforceTimer -= Time.unscaledDeltaTime;
            if (_vrEnforceTimer > 0f) return;
            _vrEnforceTimer = 0.25f;

            object toggler = ResolveToggler();
            if (toggler == null || !ReadVrEnabled(toggler)) return;

            if (WriteVrEnabled(toggler, false) && !_loggedVrOff)
            {
                _loggedVrOff = true;
                FpvGogglesPlugin.Log.LogInfo("VR held off while in menus. In a flight, F3 switches it on and stays on.");
            }
        }

        private static bool IsXrRunning()
        {
            try { return UnityEngine.XR.XRSettings.enabled; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Resolved fresh every time, never cached. Liftoff destroys and recreates UUVR's
        /// core, and every new VrTogglerManager builds a brand new toggler that sets itself
        /// to "VR on" in its constructor. A cached toggler would be a dead object whose
        /// IsVrEnabled disagrees with the live one, which makes the next F3 toggle backwards.
        /// </summary>
        private static object ResolveToggler()
        {
            Type coreType = AccessTools.TypeByName("Uuvr.UuvrCore");
            if (coreType == null) return null;

            UnityEngine.Object core = UnityEngine.Object.FindObjectOfType(coreType);
            if (core == null) return null;

            FieldInfo managerField = AccessTools.Field(coreType, "_vrTogglerManager");
            if (managerField == null) return null;

            object manager = managerField.GetValue(core);
            if (manager == null) return null;

            FieldInfo togglerField = AccessTools.Field(manager.GetType(), "_toggler");
            if (togglerField == null) return null;

            return togglerField.GetValue(manager);
        }

        private static bool ReadVrEnabled(object toggler)
        {
            try
            {
                PropertyInfo property = AccessTools.Property(toggler.GetType(), "IsVrEnabled");
                if (property == null) return false;
                object value = property.GetValue(toggler, null);
                return value is bool && (bool)value;
            }
            catch (Exception) { return false; }
        }

        private static bool WriteVrEnabled(object toggler, bool enabled)
        {
            try
            {
                MethodInfo method = AccessTools.Method(toggler.GetType(), "SetVrEnabled", new Type[] { typeof(bool) });
                if (method == null) return false;
                method.Invoke(toggler, new object[] { enabled });
                return true;
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Failed to switch VR off: " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Scene / menu detection - everything else hangs off this
        // ------------------------------------------------------------------

        private void UpdateSceneState()
        {
            string sceneName;
            try { sceneName = SceneManager.GetActiveScene().name; }
            catch (Exception) { return; }

            if (sceneName == _lastSceneName) return;
            _lastSceneName = sceneName;

            bool wasInMenu = FpvGogglesPlugin.InMenu;
            FpvGogglesPlugin.InMenu = IsMenuScene(sceneName);

            FpvGogglesPlugin.Log.LogInfo("Scene '" + sceneName + "' -> " +
                (FpvGogglesPlugin.InMenu ? "menu" : "flight"));

            // The pilot stands wherever the next flight spawns its drone, not wherever the last
            // one happened to end.
            AnalogOverlay.ResetHome();

            if (wasInMenu != FpvGogglesPlugin.InMenu) ApplyPositionalTracking();
        }

        private static bool IsMenuScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;

            string configured = FpvGogglesPlugin.MenuSceneNames.Value;
            if (string.IsNullOrEmpty(configured)) return false;

            string[] parts = configured.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) continue;
                if (sceneName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // HUD capture mode
        // ------------------------------------------------------------------

        private static object _uuvrPatchModeEntry;
        private static PropertyInfo _uuvrPatchModeValue;

        /// <summary>
        /// CanvasRedirect is what makes the HUD resizable, because only the canvases end up on
        /// the VR plane. But it re-parents those canvases to UUVR's capture camera, so leaving
        /// it on outside a VR flight makes the menu vanish - on the flat screen too. Hence: on
        /// only while actually flying in VR, off everywhere else.
        /// </summary>
        private void UpdateUiPatchMode()
        {
            if (!FpvGogglesPlugin.HudOnVrPlane.Value) return;

            _patchModeTimer -= Time.unscaledDeltaTime;
            if (_patchModeTimer > 0f) return;
            _patchModeTimer = 0.5f;

            bool flyingInVr = !FpvGogglesPlugin.InMenu && FpvGogglesPlugin.VrActive;
            int desired = flyingInVr ? 2 : 0; // None = 0, Mirror = 1, CanvasRedirect = 2

            int current = ReadUiPatchMode();
            if (current < 0 || current == desired) return;

            WriteUiPatchMode(desired, flyingInVr ? "VR flight" : "menu or flat");
        }

        private static int ReadUiPatchMode()
        {
            if (_uuvrPatchModeValue == null &&
                !ResolveUuvrConfigEntry("PreferredUiPatchMode", out _uuvrPatchModeEntry, out _uuvrPatchModeValue))
            {
                return -1;
            }

            try { return Convert.ToInt32(_uuvrPatchModeValue.GetValue(_uuvrPatchModeEntry, null)); }
            catch (Exception) { _uuvrPatchModeValue = null; return -1; }
        }

        private static void WriteUiPatchMode(int mode, string reason)
        {
            if (_uuvrPatchModeValue == null) return;

            try
            {
                object current = _uuvrPatchModeValue.GetValue(_uuvrPatchModeEntry, null);
                object next = Enum.ToObject(current.GetType(), mode);
                _uuvrPatchModeValue.SetValue(_uuvrPatchModeEntry, next, null);
                FpvGogglesPlugin.Log.LogInfo("UUVR UI Patch Mode -> " + next + " (" + reason + ")");
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Failed to change UUVR's UI Patch Mode: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // Hotkeys
        // ------------------------------------------------------------------

        private void HandleHotkeys()
        {
            // Poll every key each frame, so a key held across a scene change cannot produce
            // a stale edge afterwards.
            bool toggleMask = WasPressed(FpvGogglesPlugin.ToggleMaskKey.Value);
            bool toggleAnalog = WasPressed(FpvGogglesPlugin.ToggleAnalogKey.Value);
            bool fovUp = WasPressed(FpvGogglesPlugin.FovUpKey.Value);
            bool fovDown = WasPressed(FpvGogglesPlugin.FovDownKey.Value);
            bool uiSmaller = WasPressed(FpvGogglesPlugin.UiSmallerKey.Value);
            bool uiBigger = WasPressed(FpvGogglesPlugin.UiBiggerKey.Value);
            bool hudLeft = WasPressed(FpvGogglesPlugin.UiLeftKey.Value);
            bool hudRight = WasPressed(FpvGogglesPlugin.UiRightKey.Value);
            bool hudUp = WasPressed(FpvGogglesPlugin.UiUpKey.Value);
            bool hudDown = WasPressed(FpvGogglesPlugin.UiDownKey.Value);

            // HUD size and position stay live everywhere - those are exactly the knobs you
            // need when something sits wrong.
            float step = FpvGogglesPlugin.OffsetStep.Value;
            if (uiSmaller) StepUiScale(-FpvGogglesPlugin.UiScaleStep.Value);
            if (uiBigger) StepUiScale(FpvGogglesPlugin.UiScaleStep.Value);
            if (hudLeft) StepUiOffset(-step, 0f);
            if (hudRight) StepUiOffset(step, 0f);
            if (hudUp) StepUiOffset(0f, step);
            if (hudDown) StepUiOffset(0f, -step);
            if (WasPressed(FpvGogglesPlugin.DumpHudKey.Value)) DumpHudTree();

            if (FpvGogglesPlugin.InMenu)
            {
                // Goggle mode is paused here, so these would change something you cannot see
                // and only notice during the next flight.
                if (toggleMask || toggleAnalog || fovUp || fovDown)
                {
                    FpvGogglesPlugin.Log.LogInfo("Goggle hotkey ignored: only works during a flight.");
                }
                return;
            }

            if (toggleMask)
            {
                bool enabled = !FpvGogglesPlugin.BaseMaskEnabled;
                FpvGogglesPlugin.SessionMask = enabled;
                FpvGogglesPlugin.Log.LogInfo("Goggle rendering " + (enabled ? "ON" : "OFF") + " for this session.");
            }

            if (toggleAnalog)
            {
                bool enabled = !FpvGogglesPlugin.BaseAnalogEnabled;
                FpvGogglesPlugin.SessionAnalog = enabled;
                FpvGogglesPlugin.Log.LogInfo("Analog video " + (enabled ? "ON" : "OFF") + " for this session.");
            }

            if (fovUp) StepFov(FpvGogglesPlugin.FovStep.Value);
            if (fovDown) StepFov(-FpvGogglesPlugin.FovStep.Value);

            if (WasPressed(FpvGogglesPlugin.HorizonBiggerKey.Value)) StepHorizonScale(0.1f);
            if (WasPressed(FpvGogglesPlugin.HorizonSmallerKey.Value)) StepHorizonScale(-0.1f);
            if (WasPressed(FpvGogglesPlugin.HorizonColourKey.Value)) CycleHorizonColour();
        }

        /// <summary>
        /// Which colour reads well is decided by the map, not by the pilot, so this is a knob
        /// you reach for mid-flight. Written to the config file rather than kept for the
        /// session: it is a preference, not a comparison.
        /// </summary>
        private static void CycleHorizonColour()
        {
            int count = Enum.GetValues(typeof(IndicatorColour)).Length;
            int next = ((int)FpvGogglesPlugin.HorizonColour.Value + 1) % count;

            FpvGogglesPlugin.HorizonColour.Value = (IndicatorColour)next;
            FpvGogglesPlugin.Log.LogInfo("Horizon colour: " + FpvGogglesPlugin.HorizonColour.Value);
        }

        private static void StepHorizonScale(float delta)
        {
            float value = Mathf.Clamp(FpvGogglesPlugin.HorizonScale.Value + delta, 0.1f, 3f);
            FpvGogglesPlugin.HorizonScale.Value = value;
            FpvGogglesPlugin.Log.LogInfo("Horizon scale: " + value.ToString("0.00"));
        }

        private bool WasPressed(HotKey key)
        {
            if (key == HotKey.None) return false;

            int vk = (int)key;
            bool isDown = (GetAsyncKeyState(vk) & 0x8000) != 0;

            bool wasDown;
            if (!_keyWasDown.TryGetValue(vk, out wasDown)) wasDown = false;
            _keyWasDown[vk] = isDown;

            return isDown && !wasDown;
        }

        private static void StepFov(float delta)
        {
            float value = Mathf.Clamp(FpvGogglesPlugin.MaskFovDiagonal.Value + delta, 10f, 160f);
            FpvGogglesPlugin.MaskFovDiagonal.Value = value;
            FpvGogglesPlugin.Log.LogInfo("Goggle FOV: " + value.ToString("0.#") + " deg");
        }

        // ------------------------------------------------------------------
        // UUVR settings we drive from here
        // ------------------------------------------------------------------

        private static object _uuvrUiScaleEntry;
        private static PropertyInfo _uuvrUiScaleValue;

        private static void StepUiScale(float delta)
        {
            if (_uuvrUiScaleValue == null &&
                !ResolveUuvrConfigEntry("VrUiScale", out _uuvrUiScaleEntry, out _uuvrUiScaleValue))
            {
                FpvGogglesPlugin.Log.LogWarning("Could not reach UUVR's 'VR UI Scale'.");
                return;
            }

            try
            {
                float next = Mathf.Clamp(Convert.ToSingle(_uuvrUiScaleValue.GetValue(_uuvrUiScaleEntry, null)) + delta, 0.2f, 3f);
                _uuvrUiScaleValue.SetValue(_uuvrUiScaleEntry, next, null);
                FpvGogglesPlugin.Log.LogInfo("HUD scale: " + next.ToString("0.00"));
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Failed to change the HUD scale: " + e.Message);
            }
        }

        private static void StepUiOffset(float deltaX, float deltaY)
        {
            object entry;
            PropertyInfo value;
            if (!ResolveUuvrConfigEntry("VrUiPosition", out entry, out value))
            {
                FpvGogglesPlugin.Log.LogWarning("Could not reach UUVR's 'VR UI Position'.");
                return;
            }

            try
            {
                Vector3 current = (Vector3)value.GetValue(entry, null);
                Vector3 next = new Vector3(
                    Mathf.Clamp(current.x + deltaX, -2f, 2f),
                    Mathf.Clamp(current.y + deltaY, -2f, 2f),
                    current.z);
                value.SetValue(entry, next, null);
                FpvGogglesPlugin.Log.LogInfo("HUD offset: x " + next.x.ToString("0.000") +
                    ", y " + next.y.ToString("0.000") + " m");
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Failed to move the HUD plane: " + e.Message);
            }
        }

        private static bool ResolveUuvrConfigEntry(string fieldName, out object entry, out PropertyInfo valueProperty)
        {
            entry = null;
            valueProperty = null;

            Type configType = AccessTools.TypeByName("Uuvr.ModConfiguration");
            if (configType == null) return false;

            FieldInfo instanceField = AccessTools.Field(configType, "Instance");
            if (instanceField == null) return false;

            object instance = instanceField.GetValue(null);
            if (instance == null) return false; // UUVR not up yet, try again next time

            FieldInfo field = AccessTools.Field(configType, fieldName);
            if (field == null) return false;

            entry = field.GetValue(instance);
            if (entry == null) return false;

            valueProperty = entry.GetType().GetProperty("Value");
            return valueProperty != null;
        }

        // ------------------------------------------------------------------
        // Positional tracking
        // ------------------------------------------------------------------

        private static void ApplyPositionalTracking()
        {
            if (FpvGogglesPlugin.DisablePositionalTrackingProperty == null) return;

            try
            {
                bool desired = FpvGogglesPlugin.HeadPositionLocked;
                object current = FpvGogglesPlugin.DisablePositionalTrackingProperty.GetValue(null, null);
                if (!(current is bool) || (bool)current != desired)
                {
                    FpvGogglesPlugin.DisablePositionalTrackingProperty.SetValue(null, desired, null);
                }
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Failed to set positional tracking: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // Optional goggle rendering
        // ------------------------------------------------------------------

        // Static, so a recreated runner adopts the existing objects instead of orphaning them.
        private static GameObject _maskObject;
        private static MeshFilter _maskFilter;
        private static Material _maskMaterial;
        private static Camera _maskCamera;
        private static bool _warnedNoCamera;
        private float _noCameraTimer;

        private void UpdateGoggles()
        {
            bool wantMask = FpvGogglesPlugin.GoggleRenderingActive;
            bool wantHorizon = FpvGogglesPlugin.HorizonActive;
            bool wantAnalog = FpvGogglesPlugin.AnalogActive;

            if (!wantMask && !wantHorizon && !wantAnalog)
            {
                HorizonIndicator.Teardown();
                AnalogOverlay.Teardown();
                if (_maskObject != null && _maskObject.activeSelf) _maskObject.SetActive(false);
                return;
            }

            bool needsCamera = _maskCamera == null || (wantMask && (_maskObject == null || _maskObject.transform.parent == null));
            _cameraSearchTimer -= Time.unscaledDeltaTime;

            if (needsCamera || _cameraSearchTimer <= 0f)
            {
                _cameraSearchTimer = 0.5f;
                Camera camera = FindTargetCamera();

                if (camera == null)
                {
                    _noCameraTimer += 0.5f;
                    if (_noCameraTimer > 10f && !_warnedNoCamera)
                    {
                        _warnedNoCamera = true;
                        FpvGogglesPlugin.Log.LogWarning("No UUVR camera found for goggle rendering.");
                    }
                    return;
                }

                _noCameraTimer = 0f;
                bool changed = camera != _maskCamera;
                _maskCamera = camera;
                if (wantMask && (changed || _maskObject == null)) AttachMask(camera);
            }

            if (_maskCamera == null) return;

            float halfWidth, halfHeight, distance;
            ComputeAperture(out halfWidth, out halfHeight, out distance);

            // The horizon is an OSD element and runs whether or not the goggle frame is on.
            if (wantHorizon) HorizonIndicator.Update(_maskCamera, halfWidth, halfHeight, distance);
            else HorizonIndicator.Teardown();

            // Drawn last of the three, and on purpose: on real hardware the OSD is generated at
            // the drone, so the horizon above goes through the radio link along with the picture
            // and picks up the same snow.
            if (wantAnalog)
            {
                AnalogOverlay.Update(_maskCamera, halfWidth, halfHeight, distance, wantMask);
                UpdatePostFx(_maskCamera);
            }
            else
            {
                AnalogOverlay.Teardown();
                UpdatePostFx(null);
            }

            if (!wantMask)
            {
                if (_maskObject != null && _maskObject.activeSelf) _maskObject.SetActive(false);
                return;
            }

            if (_maskObject == null) return;

            if (MaskSettingsChanged())
            {
                _maskFilter.sharedMesh = BuildMaskMesh(halfWidth, halfHeight, distance);
                RememberMaskSettings();
            }

            if (!_maskObject.activeSelf) _maskObject.SetActive(true);
        }

        private static bool _postFxBroken;

        /// <summary>
        /// Kept behind a guard because it is the one part of this plugin that depends on a
        /// package rather than on the engine. If Liftoff ever ships without the Post Processing
        /// Stack, loading AnalogPostFx throws here instead of taking the whole update loop with
        /// it, and everything else carries on.
        /// </summary>
        private static void UpdatePostFx(Camera camera)
        {
            if (_postFxBroken) return;

            try
            {
                if (camera == null) AnalogPostFx.Teardown();
                else AnalogPostFx.Update(camera, AnalogOverlay.Signal);
            }
            catch (Exception e)
            {
                _postFxBroken = true;
                FpvGogglesPlugin.Log.LogWarning(
                    "Image processing is unavailable, carrying on with the overlay only: " + e.Message);
            }
        }

        /// <summary>
        /// Only ever attach to a camera UUVR actually renders through. UUVR names its child
        /// camera "VrChildCamera" and copies the parent camera's settings onto it - including
        /// depth - so depth alone cannot tell the two apart.
        /// </summary>
        private static Camera FindTargetCamera()
        {
            Camera[] cameras = Camera.allCameras;
            Camera best = null;
            int bestScore = 0;

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera == null || !camera.enabled || !camera.gameObject.activeInHierarchy) continue;
                if (camera.targetTexture != null) continue;

                int score = 0;
                if (FpvGogglesPlugin.PoseDriverType != null &&
                    camera.GetComponent(FpvGogglesPlugin.PoseDriverType) != null) score += 1;
                if (camera.name == "VrChildCamera") score += 2;
                if (score == 0) continue;

                if (best == null || score > bestScore || (score == bestScore && camera.depth > best.depth))
                {
                    best = camera;
                    bestScore = score;
                }
            }

            return best;
        }

        private void AttachMask(Camera camera)
        {
            if (_maskObject != null) Destroy(_maskObject);

            _maskObject = new GameObject("FpvGoggleMask");
            _maskObject.transform.SetParent(camera.transform, false);
            _maskObject.transform.localPosition = Vector3.zero;
            _maskObject.transform.localRotation = Quaternion.identity;
            _maskObject.transform.localScale = Vector3.one;
            _maskObject.layer = camera.gameObject.layer;

            _maskFilter = _maskObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = _maskObject.AddComponent<MeshRenderer>();

            if (_maskMaterial == null) _maskMaterial = CreateMaskMaterial();
            renderer.sharedMaterial = _maskMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;

            _maskBuilt = false;
            FpvGogglesPlugin.Log.LogInfo("Goggle mask attached to camera '" + camera.name + "'.");
        }

        // Polled rather than driven by ConfigFile.SettingChanged: Liftoff destroys the BepInEx
        // plugin component, whose OnDestroy dutifully unsubscribes that event - after which
        // FOV changes silently stopped rebuilding the mesh.
        private static bool _maskBuilt;
        private static float _builtFov, _builtAspectWidth, _builtAspectHeight, _builtDistance;

        private static bool MaskSettingsChanged()
        {
            return !_maskBuilt
                || _builtFov != FpvGogglesPlugin.MaskFovDiagonal.Value
                || _builtAspectWidth != FpvGogglesPlugin.MaskAspectWidth.Value
                || _builtAspectHeight != FpvGogglesPlugin.MaskAspectHeight.Value
                || _builtDistance != FpvGogglesPlugin.MaskDistance.Value;
        }

        private static void RememberMaskSettings()
        {
            _maskBuilt = true;
            _builtFov = FpvGogglesPlugin.MaskFovDiagonal.Value;
            _builtAspectWidth = FpvGogglesPlugin.MaskAspectWidth.Value;
            _builtAspectHeight = FpvGogglesPlugin.MaskAspectHeight.Value;
            _builtDistance = FpvGogglesPlugin.MaskDistance.Value;
        }

        /// <summary>
        /// Size of the goggle window on a plane at the configured distance. Also what the analog
        /// layers use as the picture area whenever the mask is on.
        /// </summary>
        private static void ComputeAperture(out float halfWidth, out float halfHeight, out float distance)
        {
            distance = FpvGogglesPlugin.MaskDistance.Value;
            float aspectWidth = Mathf.Max(0.01f, FpvGogglesPlugin.MaskAspectWidth.Value);
            float aspectHeight = Mathf.Max(0.01f, FpvGogglesPlugin.MaskAspectHeight.Value);

            float diagonal = 2f * distance * Mathf.Tan(FpvGogglesPlugin.MaskFovDiagonal.Value * 0.5f * Mathf.Deg2Rad);
            float unit = diagonal / Mathf.Sqrt(aspectWidth * aspectWidth + aspectHeight * aspectHeight);
            halfWidth = aspectWidth * unit * 0.5f;
            halfHeight = aspectHeight * unit * 0.5f;
        }

        private static Mesh BuildMaskMesh(float halfWidth, float halfHeight, float distance)
        {
            // Far beyond any headset FOV, so the border always reaches the edge of vision.
            float outer = distance * 20f;

            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();

            AddQuad(vertices, triangles, -outer, -outer, -halfWidth, outer, distance);
            AddQuad(vertices, triangles, halfWidth, -outer, outer, outer, distance);
            AddQuad(vertices, triangles, -halfWidth, halfHeight, halfWidth, outer, distance);
            AddQuad(vertices, triangles, -halfWidth, -outer, halfWidth, -halfHeight, distance);

            Mesh mesh = new Mesh();
            mesh.name = "FpvGoggleMask";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return mesh;
        }

        /// <summary>Rectangle in the XY plane, wound both ways so backface culling cannot hide it.</summary>
        private static void AddQuad(List<Vector3> vertices, List<int> triangles,
            float x0, float y0, float x1, float y1, float z)
        {
            if (x1 <= x0 || y1 <= y0) return;

            int b = vertices.Count;
            vertices.Add(new Vector3(x0, y0, z));
            vertices.Add(new Vector3(x0, y1, z));
            vertices.Add(new Vector3(x1, y1, z));
            vertices.Add(new Vector3(x1, y0, z));

            triangles.Add(b + 0); triangles.Add(b + 1); triangles.Add(b + 2);
            triangles.Add(b + 0); triangles.Add(b + 2); triangles.Add(b + 3);
            triangles.Add(b + 2); triangles.Add(b + 1); triangles.Add(b + 0);
            triangles.Add(b + 3); triangles.Add(b + 2); triangles.Add(b + 0);
        }

        private static readonly string[] ShaderCandidates =
        {
            "Unlit/Color", "Hidden/Internal-Colored", "Sprites/Default",
            "UI/Default", "Universal Render Pipeline/Unlit", "Legacy Shaders/Diffuse",
        };

        private static Material CreateMaskMaterial()
        {
            Shader shader = null;
            for (int i = 0; i < ShaderCandidates.Length; i++)
            {
                shader = Shader.Find(ShaderCandidates[i]);
                if (shader != null)
                {
                    FpvGogglesPlugin.Log.LogInfo("Goggle mask using shader '" + ShaderCandidates[i] + "'.");
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
                FpvGogglesPlugin.Log.LogError("Found no usable shader for the goggle mask.");
                return null;
            }

            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.name = "FpvGoggleMask";

            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.black);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.black);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", Color.black);

            material.renderQueue = 5000;
            if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)CompareFunction.Always);
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Off);

            return material;
        }
    }
}
