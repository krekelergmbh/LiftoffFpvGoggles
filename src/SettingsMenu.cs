using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// A settings panel you click through with the mouse, instead of a keyboard shortcut per
    /// setting. With a headset on you cannot see the keyboard, which is the entire reason this
    /// exists - and it is why nearly every hotkey now defaults to unbound.
    ///
    /// It shows up in VR without doing anything special. It is an ordinary screen space canvas,
    /// so UUVR's canvas redirection collects it exactly like the game's own interface and puts
    /// it on the plane in front of you. The mouse keeps working for the same reason: the canvas
    /// still lives in screen coordinates, whatever it is being drawn onto.
    ///
    /// The rows are built from the settings file rather than listed here. A menu that repeats
    /// the settings by hand is a menu that quietly falls behind them.
    /// </summary>
    internal static class SettingsMenu
    {
        private const int SortingOrder = 30000;

        private static GameObject _root;
        private static RectTransform _content;
        private static TMPro.TMP_FontAsset _font;
        private static bool _open;

        private static CursorLockMode _savedLock;
        private static bool _savedCursorVisible;

        // Re-read when the panel opens, because a hotkey or the game may have moved something
        // underneath it while it was closed.
        private static readonly List<Action> _refreshers = new List<Action>();

        // Small per frame animations - the switch knobs sliding across, and nothing else so far.
        // Registered by the row that owns them and dropped together with the panel.
        private static readonly List<Action> _tickers = new List<Action>();

        internal static bool IsOpen { get { return _open; } }

        // ------------------------------------------------------------------
        // Driven from the runner
        // ------------------------------------------------------------------

        internal static void Toggle()
        {
            SetOpen(!_open);
        }

        internal static void Update()
        {
            if (!_open) return;

            // Liftoff destroys injected objects, and it takes the panel with it. Rebuilding is
            // cheap and beats a menu that silently stopped existing.
            if (_root == null)
            {
                Build();
                if (_root == null) { _open = false; return; }
            }

            // Re-applied every frame: the game grabs the cursor back during a flight, and once
            // it is captured the panel is there but unusable.
            //
            // Hidden, not shown. The arrow below is drawn onto our own canvas, which is the one
            // that lands where the click is actually tested - the system pointer sits somewhere
            // else entirely and having both on screen was two pointers disagreeing.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = false;

            UpdateCursor();
            UpdateScrolling();
            UpdateVisibility();
            Tick();
            KeepOnTop();

            // Re-applied rather than set once: UUVR reassigns the plane's shader on every setting
            // change, and unity_GUIZTestMode is not a declared property, so it does not survive.
            // Moving a slider in this panel is a setting change, which is the worst possible
            // moment for the panel to sink back into the floor.
            try { FpvGogglesRunner.SetUiPlaneAlwaysVisible(true); }
            catch (Exception) { }
        }

        private static void Tick()
        {
            for (int i = 0; i < _tickers.Count; i++)
            {
                try { _tickers[i](); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Our own layers, out of the way of the panel.
        ///
        /// In VR the panel is not drawn over the picture at all - it is drawn into UUVR's capture
        /// texture and shown on a quad inside the scene, at the ordinary canvas queue. Both the
        /// analog overlay and the horizon are meshes we deliberately put above everything, so
        /// both were landing on top of it: the snow and the sync bar washed the panel out, and the
        /// horizon crossed it. Neither can be answered from this class, because sorting order only
        /// settles arguments between canvases.
        /// </summary>
        private static void SetDrawnBehind(bool behind)
        {
            try { AnalogOverlay.SetBehindUi(behind); }
            catch (Exception) { }

            try { HorizonIndicator.SetBehindUi(behind); }
            catch (Exception) { }

            // And the floor, which is a different problem with the same symptom: the plane hangs
            // a metre away and is depth tested, so looking down on the ground buries the lower
            // half of the panel in the hangar floor.
            try { FpvGogglesRunner.SetUiPlaneAlwaysVisible(behind); }
            catch (Exception) { }
        }

        private static float _topTimer;

        /// <summary>
        /// Keeps the panel above everything else being drawn.
        ///
        /// A fixed sorting order was a guess: the game's own overlays have their own numbers,
        /// parts of the HUD only appear once the drone is armed, and one of them sat over the
        /// panel. Asking what the highest one currently is and going one better cannot be
        /// out-guessed by a canvas that turns up later.
        /// </summary>
        private static void KeepOnTop()
        {
            if (_canvas == null) return;

            _topTimer -= Time.unscaledDeltaTime;
            if (_topTimer > 0f) return;
            _topTimer = 1f;

            int highest = 0;
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();

            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas other = canvases[i];
                if (other == null || other == _canvas) continue;
                if (other.sortingOrder > highest) highest = other.sortingOrder;
            }

            // Unity keeps the sorting order in a short, so this is as high as it goes.
            int wanted = Mathf.Min(highest + 10, 32767);
            if (_canvas.sortingOrder < wanted) _canvas.sortingOrder = wanted;
        }

        private static void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;

            if (open)
            {
                Build();
                if (_root == null)
                {
                    _open = false;
                    return;
                }

                _savedLock = Cursor.lockState;
                _savedCursorVisible = Cursor.visible;

                SetDrawnBehind(true);

                Refresh();
                FpvGogglesPlugin.Log.LogInfo("Settings menu open.");
            }
            else
            {
                RestorePointer();
                SetDrawnBehind(false);

                // Leaving the HUD blown up after closing would be a trap: you would fly off with
                // a third more HUD than you set and no obvious way back.
                if (_zoomedFrom >= 0f)
                {
                    FpvGogglesRunner.SetUiScale(_zoomedFrom);
                    _zoomedFrom = -1f;
                }

                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
                _content = null;
                _refreshers.Clear();
                _tickers.Clear();

                Cursor.lockState = _savedLock;
                Cursor.visible = _savedCursorVisible;
            }
        }

        private static void Refresh()
        {
            for (int i = 0; i < _refreshers.Count; i++)
            {
                try { _refreshers[i](); }
                catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------
        // Building the panel
        // ------------------------------------------------------------------

        private static void Build()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _refreshers.Clear();

            EnsureEventSystem();
            EnsureFont();

            _root = new GameObject("FpvSettingsMenu");
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;

            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _root.AddComponent<GraphicRaycaster>();

            // No dimming layer behind the panel. It seemed like it would help the list read, and
            // in the headset it just puts a grey veil over the entire HUD - which is the one
            // thing you are still trying to see while the menu is open.
            Image panel = NewRounded("Panel", _root.transform, PanelBack, 16);
            RectTransform panelRect = panel.rectTransform;
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(920f, 760f);
            panelRect.anchoredPosition = Vector2.zero;

            // Sits over the panel rather than replacing its corners, so the two shapes cannot
            // disagree about where the edge is.
            Image edge = NewImage("Edge", panelRect, PanelEdge);
            edge.sprite = Outline();
            edge.type = Image.Type.Sliced;
            edge.fillCenter = false;
            edge.raycastTarget = false;
            Stretch(edge.rectTransform);

            BuildHeader(panelRect);
            BuildScrollArea(panelRect);
            BuildRows();

            BuildCursor(canvas);
            AttachToVrPlane(canvas);

            // The list is built one row at a time and the layout only settles at the end of the
            // frame, by which point the scroll view has decided it is sitting at the bottom.
            // Forcing the rebuild now and starting at the top means the panel opens where you
            // would expect to read it from.
            UpdateVisibility();
            SnapScrollIntoRange();
            _scrollTarget = 1f;
            _scrollApplied = 1f;
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
        }

        private static RectTransform _cursor;
        private static Canvas _canvas;

        /// <summary>
        /// Our own pointer, drawn onto our own canvas.
        ///
        /// UUVR forces a software cursor, which Unity paints onto the screen rather than into a
        /// canvas - and the capture camera only ever sees canvases. In mirror mode that works out
        /// because the whole screen is copied; in canvas redirect mode there is simply no pointer
        /// in the headset at all. Drawing one here costs two images and removes the guesswork.
        /// </summary>
        private static void BuildCursor(Canvas canvas)
        {
            _canvas = canvas;

            Image arrow = NewImage("Cursor", canvas.transform, Color.white);
            arrow.raycastTarget = false;
            arrow.sprite = ArrowSprite();
            arrow.type = Image.Type.Simple;
            arrow.preserveAspect = true;

            RectTransform rect = arrow.rectTransform;

            // Anchored to the centre, pivoted at the top left. Those are two different things,
            // and confusing them is what broke the pointer: the position that comes back from
            // ScreenPointToLocalPointInRectangle is measured from the centre of the canvas, so
            // anchoring to a corner puts the arrow half a screen out - and it runs out of room
            // long before the mouse does. The pivot belongs at the top left because that is
            // where the tip is, and the tip is the point being clicked.
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(ArrowWidth * 2f, ArrowHeight * 2f);

            _cursor = rect;
            _cursor.SetAsLastSibling();
        }

        // '.' nothing, 'X' the black edge, 'O' the white body. Top row first, which is upside
        // down from a texture's point of view - the loop below flips it back.
        private static readonly string[] ArrowShape =
        {
            "X...........",
            "XX..........",
            "XOX.........",
            "XOOX........",
            "XOOOX.......",
            "XOOOOX......",
            "XOOOOOX.....",
            "XOOOOOOX....",
            "XOOOOOOOX...",
            "XOOOOOOOOX..",
            "XOOOOOXXXXX.",
            "XOOXOOX.....",
            "XOX.XOOX....",
            "XX..XOOX....",
            "X....XOOX...",
            ".....XOOX...",
            "......XOX...",
            "......XX....",
        };

        private const int ArrowWidth = 12;
        private const int ArrowHeight = 18;

        private static Sprite _arrow;

        /// <summary>
        /// The pointer, drawn pixel by pixel. A coloured square was readable but told you
        /// nothing about where exactly it was pointing; an arrow has a tip.
        /// </summary>
        private static Sprite ArrowSprite()
        {
            if (_arrow != null) return _arrow;

            Texture2D texture = new Texture2D(ArrowWidth, ArrowHeight, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;

            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 edge = new Color32(0, 0, 0, 255);
            Color32 body = new Color32(255, 255, 255, 255);

            for (int y = 0; y < ArrowHeight; y++)
            {
                string row = ArrowShape[ArrowHeight - 1 - y];
                for (int x = 0; x < ArrowWidth; x++)
                {
                    char c = x < row.Length ? row[x] : '.';
                    texture.SetPixel(x, y, c == 'X' ? (Color)edge : c == 'O' ? (Color)body : (Color)clear);
                }
            }

            texture.Apply(false, false);

            _arrow = Sprite.Create(texture, new Rect(0f, 0f, ArrowWidth, ArrowHeight), new Vector2(0f, 1f), 100f);
            _arrow.hideFlags = HideFlags.HideAndDontSave;
            return _arrow;
        }

        private static void UpdateCursor()
        {
            if (_cursor == null || _canvas == null) return;

            RectTransform canvasRect = _canvas.transform as RectTransform;
            if (canvasRect == null) return;

            Vector2 local;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, Input.mousePosition, _canvas.worldCamera, out local))
            {
                _cursor.anchoredPosition = local;
            }
        }

        /// <summary>
        /// Hands the panel to UUVR so it lands on the plane in front of you instead of on a
        /// desktop nobody is looking at.
        ///
        /// UUVR does sweep for new canvases by itself, once per frame, over everything in the
        /// graphic registry - so in principle this is unnecessary. In practice a menu that only
        /// works outside the headset is the one bug that makes the whole feature pointless, and
        /// asking directly costs one reflection call.
        /// </summary>
        private static void AttachToVrPlane(Canvas canvas)
        {
            if (!FpvGogglesPlugin.VrActive) return;

            try
            {
                Type patchModeType = HarmonyLib.AccessTools.TypeByName("Uuvr.VrUi.PatchModes.CanvasRedirectPatchMode");
                Type redirectType = HarmonyLib.AccessTools.TypeByName("Uuvr.VrUi.PatchModes.CanvasRedirect");
                if (patchModeType == null || redirectType == null) return;

                System.Reflection.FieldInfo cameraField =
                    HarmonyLib.AccessTools.Field(patchModeType, "_uiCaptureCamera");
                System.Reflection.MethodInfo create =
                    HarmonyLib.AccessTools.Method(redirectType, "Create", new Type[] { typeof(Canvas), typeof(Camera) });
                if (cameraField == null || create == null) return;

                UnityEngine.Object[] modes = Resources.FindObjectsOfTypeAll(patchModeType);
                for (int i = 0; i < modes.Length; i++)
                {
                    Camera camera = cameraField.GetValue(modes[i]) as Camera;
                    if (camera == null) continue;

                    create.Invoke(null, new object[] { canvas, camera });
                    RescalePointer(camera);

                    RenderTexture target = camera.targetTexture;
                    FpvGogglesPlugin.Log.LogInfo("Settings menu on UUVR's capture camera '" + camera.name +
                        "'. Screen " + Screen.width + "x" + Screen.height +
                        ", capture " + (target != null ? target.width + "x" + target.height : "no texture") + ".");
                    return;
                }

                FpvGogglesPlugin.Log.LogWarning("Found no UUVR capture camera; the menu will only show on the flat screen.");
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Could not hand the settings menu to UUVR: " + e.Message);
            }
        }

        /// <summary>
        /// Rescales the mouse position into the coordinates the capture camera thinks in.
        ///
        /// This is why nothing was clickable in the headset. Once UUVR redirects the canvas it
        /// is drawn by a camera rendering into a texture of its own size - 2544 by 2564 in this
        /// setup - while the pointer still arrives in game window pixels, at most 1920 by 1080.
        /// The raycaster divides one by the other, so the cursor you see near the Close button
        /// is tested against a point three quarters of the way across and less than half way up.
        /// It misses everything, silently.
        /// </summary>
        private sealed class ScaledPointer : BaseInput
        {
            internal Camera Capture;

            public override Vector2 mousePosition
            {
                get
                {
                    Vector2 position = base.mousePosition;
                    if (Capture == null) return position;

                    RenderTexture target = Capture.targetTexture;
                    if (target == null || Screen.width <= 0 || Screen.height <= 0) return position;

                    return new Vector2(
                        position.x * target.width / Screen.width,
                        position.y * target.height / Screen.height);
                }
            }
        }

        private static ScaledPointer _pointer;
        private static BaseInput _savedInput;
        private static PointerInputModule _patchedModule;

        private static void RescalePointer(Camera capture)
        {
            EventSystem system = EventSystem.current;
            if (system == null) return;

            PointerInputModule module = system.currentInputModule as PointerInputModule;
            if (module == null) module = system.GetComponent<PointerInputModule>();
            if (module == null) return;

            if (_pointer == null)
            {
                _pointer = module.gameObject.AddComponent<ScaledPointer>();
            }

            _pointer.Capture = capture;
            _savedInput = module.inputOverride;
            _patchedModule = module;
            module.inputOverride = _pointer;
        }

        private static void RestorePointer()
        {
            if (_patchedModule != null) _patchedModule.inputOverride = _savedInput;
            _patchedModule = null;
            _savedInput = null;
        }

        private static void BuildHeader(RectTransform panel)
        {
            TMPro.TextMeshProUGUI title = NewText("Title", panel, "FPV Goggles", 25, TextAnchor.MiddleLeft);
            title.color = TextPrimary;
            title.fontStyle = TMPro.FontStyles.Bold;
            RectTransform rect = title.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(30f, -62f);
            rect.offsetMax = new Vector2(-372f, -22f);

            TMPro.TextMeshProUGUI hint = NewText("Hint", panel,
                "Changes apply straight away and are saved to the config file.", 14, TextAnchor.MiddleLeft);
            hint.color = TextMuted;
            RectTransform hintRect = hint.rectTransform;
            hintRect.anchorMin = new Vector2(0f, 1f);
            hintRect.anchorMax = new Vector2(1f, 1f);
            hintRect.pivot = new Vector2(0.5f, 1f);
            hintRect.offsetMin = new Vector2(31f, -88f);
            hintRect.offsetMax = new Vector2(-372f, -64f);

            Button close = NewButton("Close", panel, "Close", delegate { SetOpen(false); });
            PlaceHeaderButton(close, -26f, 88f);

            _zoomButton = NewButton("Zoom", panel, ZoomLabel(), delegate { ToggleZoom(); });
            PlaceHeaderButton(_zoomButton, -124f, 112f);

            Button reset = NewButton("Reset", panel, "Reset all", delegate { ResetAll(); });
            PlaceHeaderButton(reset, -246f, 104f);

            // Separates the title from the list without a heavy bar across the panel.
            Image line = NewImage("HeaderLine", panel, Divider);
            line.raycastTarget = false;
            RectTransform lineRect = line.rectTransform;
            lineRect.anchorMin = new Vector2(0f, 1f);
            lineRect.anchorMax = new Vector2(1f, 1f);
            lineRect.pivot = new Vector2(0.5f, 1f);
            lineRect.offsetMin = new Vector2(26f, -105f);
            lineRect.offsetMax = new Vector2(-26f, -104f);
        }

        private static void PlaceHeaderButton(Button button, float x, float width)
        {
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(width, 34f);
            rect.anchoredPosition = new Vector2(x, -26f);
        }

        // ------------------------------------------------------------------
        // Reading the panel, and putting everything back
        // ------------------------------------------------------------------

        private static Button _zoomButton;
        private static float _zoomedFrom = -1f;

        private static string ZoomLabel()
        {
            return _zoomedFrom >= 0f ? "Normal size" : "Read it";
        }

        /// <summary>
        /// Blows the HUD plane up by a third while you are reading the panel, and puts it back.
        ///
        /// The size you fly with and the size you can read a list of settings at are not the
        /// same size, and having to reset the first one by hand after adjusting for the second
        /// is how a menu ends up worse than the keys it replaced.
        /// </summary>
        private static void ToggleZoom()
        {
            if (_zoomedFrom >= 0f)
            {
                FpvGogglesRunner.SetUiScale(_zoomedFrom);
                _zoomedFrom = -1f;
            }
            else
            {
                _zoomedFrom = FpvGogglesRunner.GetUiScale();
                FpvGogglesRunner.SetUiScale(_zoomedFrom * 1.5f);
            }

            if (_zoomButton != null)
            {
                TMPro.TextMeshProUGUI label = _zoomButton.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (label != null) label.text = ZoomLabel();
            }

            Refresh();
        }

        /// <summary>
        /// Every setting on this panel back to what it ships as, including the two that belong to
        /// UUVR.
        ///
        /// Worth more than it sounds: the sliders apply as you drag them, so a pass through the
        /// list to see what everything does leaves a trail of values nobody chose. This is the
        /// way back from that.
        ///
        /// Strictly what the panel shows, though. Resetting the whole file also cleared the
        /// hidden HUD elements and every key binding - settings that are deliberately not in the
        /// list, so nothing here even hinted they had gone, and the crosshair simply reappeared.
        /// A reset button may only undo what the button's own screen can put back.
        /// </summary>
        private static void ResetAll()
        {
            ConfigFile config = FpvGogglesPlugin.Configuration;
            if (config != null)
            {
                foreach (ConfigDefinition definition in config.Keys)
                {
                    try
                    {
                        ConfigEntryBase entry = config[definition];
                        if (entry == null || entry.DefaultValue == null) continue;
                        if (!IsShown(entry)) continue;

                        entry.BoxedValue = entry.DefaultValue;
                    }
                    catch (Exception) { }
                }
            }

            // Not ours to have a default for, so these are simply the values that work: the HUD
            // centred, at the size it is legible at on the plane.
            _zoomedFrom = -1f;
            FpvGogglesRunner.SetUiScale(0.5f);
            FpvGogglesRunner.SetUiOffset(Vector2.zero);

            Refresh();
            FpvGogglesPlugin.Log.LogInfo("All settings reset to their defaults.");
        }

        private static void BuildScrollArea(RectTransform panel)
        {
            // Narrower on the right, to leave room for the bar.
            GameObject viewport = NewUi("Viewport", panel);
            RectTransform viewRect = viewport.GetComponent<RectTransform>();
            viewRect.anchorMin = new Vector2(0f, 0f);
            viewRect.anchorMax = new Vector2(1f, 1f);
            viewRect.offsetMin = new Vector2(20f, 20f);
            viewRect.offsetMax = new Vector2(-34f, -116f);

            // RectMask2D rather than Mask: no stencil buffer, one less thing to go wrong on a
            // canvas somebody else is capturing.
            viewport.AddComponent<RectMask2D>();

            // Invisible, but it has to be here. A scroll wheel event goes to whatever graphic is
            // under the pointer and then travels up the hierarchy - with nothing to hit inside
            // the viewport it lands on the panel behind instead, which is not the scroll view's
            // parent, so the list simply refused to move.
            Image catcher = NewHitArea("Catcher", viewRect);
            Stretch(catcher.rectTransform);

            GameObject content = NewUi("Content", viewRect);
            _content = content.GetComponent<RectTransform>();
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);

            // A fresh RectTransform starts at 100 by 100, not at zero. With anchors stretched
            // across the viewport that leaves the content a hundred pixels too wide, centred,
            // so fifty hang off each side - which is what cut the left edge off every label.
            _content.sizeDelta = Vector2.zero;
            _content.anchoredPosition = Vector2.zero;

            VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(6, 6, 2, 10);
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childControlWidth = true;

            ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = viewport.AddComponent<ScrollRect>();
            scroll.viewport = viewRect;
            scroll.content = _content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            // Unity's own wheel handling moves the list in one hard step per notch, which is
            // what made it feel like it was jumping. Zero here takes it out of the loop and the
            // wheel is read below instead, easing towards a target.
            scroll.scrollSensitivity = 0f;

            scroll.verticalScrollbar = BuildScrollbar(panel);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            _scroll = scroll;
            _scrollTarget = 1f;
            _scrollApplied = 1f;
        }

        private static ScrollRect _scroll;
        private static float _scrollTarget = 1f;
        private static float _scrollApplied = 1f;

        /// <summary>
        /// Wheel input eased towards a target rather than applied to the position directly. The
        /// difference is only a couple of lines and it is the whole difference between a list
        /// that jumps and one that moves.
        /// </summary>
        private static void UpdateScrolling()
        {
            if (_scroll == null || _content == null) return;

            float current = _scroll.verticalNormalizedPosition;

            // Somebody else moved it - the scrollbar being dragged, most likely. Follow rather
            // than fight, or the two would pull against each other every frame.
            if (Mathf.Abs(current - _scrollApplied) > 0.0005f) _scrollTarget = current;

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                float hidden = Mathf.Max(1f, _content.rect.height - _scroll.viewport.rect.height);
                _scrollTarget = Mathf.Clamp01(_scrollTarget + wheel * (90f / hidden));
            }

            // Frame rate independent easing, so it feels the same at 36 and at 90.
            float blend = 1f - Mathf.Exp(-16f * Time.unscaledDeltaTime);
            _scrollApplied = Mathf.Lerp(current, _scrollTarget, blend);
            _scroll.verticalNormalizedPosition = _scrollApplied;
        }

        /// <summary>
        /// A bar you can also drag. The wheel moves in notches however it is tuned - fine steps
        /// feel slow, coarse ones jump - and neither tells you where in the list you are.
        /// </summary>
        private static Scrollbar BuildScrollbar(RectTransform panel)
        {
            Image track = NewRounded("Scrollbar", panel, new Color(0.114f, 0.125f, 0.145f, 1f), 3);
            RectTransform trackRect = track.rectTransform;
            trackRect.anchorMin = new Vector2(1f, 0f);
            trackRect.anchorMax = new Vector2(1f, 1f);
            trackRect.pivot = new Vector2(1f, 0.5f);

            // Top and bottom insets match the viewport's, so the bar starts and ends level with
            // the list rather than floating past it.
            trackRect.sizeDelta = new Vector2(6f, -136f);
            trackRect.anchoredPosition = new Vector2(-14f, -48f);

            GameObject area = NewUi("SlidingArea", trackRect);
            RectTransform areaRect = area.GetComponent<RectTransform>();
            Stretch(areaRect);

            Image handleImage = NewRounded("Handle", areaRect, new Color(0.31f, 0.34f, 0.39f, 1f), 3);
            RectTransform handle = handleImage.rectTransform;
            handle.sizeDelta = Vector2.zero;

            Scrollbar scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;

            return scrollbar;
        }

        // ------------------------------------------------------------------
        // The rows
        // ------------------------------------------------------------------

        // Rows that only do something under certain conditions, and the condition. Checked every
        // frame while the panel is open: a knob that currently changes nothing is worse than a
        // missing one, because you turn it and conclude the mod is broken.
        private static readonly List<KeyValuePair<GameObject, Func<bool>>> _conditional =
            new List<KeyValuePair<GameObject, Func<bool>>>();

        private static void ShowWhen(GameObject row, Func<bool> condition)
        {
            _conditional.Add(new KeyValuePair<GameObject, Func<bool>>(row, condition));
        }

        private static void UpdateVisibility()
        {
            bool changed = false;

            for (int i = 0; i < _conditional.Count; i++)
            {
                GameObject row = _conditional[i].Key;
                if (row == null) continue;

                bool wanted;
                try { wanted = _conditional[i].Value(); }
                catch (Exception) { wanted = true; }

                if (row.activeSelf == wanted) continue;

                row.SetActive(wanted);
                changed = true;
            }

            // Switching off the bottom section makes the list shorter than the position you are
            // scrolled to, and the panel ends up showing empty space with no way back - the
            // scroll range has already shrunk past where you are. Rebuilding straight away and
            // pulling the target back into range keeps the list under you.
            if (changed) SnapScrollIntoRange();
        }

        private static void SnapScrollIntoRange()
        {
            if (_scroll == null || _content == null) return;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            if (_content.rect.height <= _scroll.viewport.rect.height)
            {
                _scrollTarget = 1f;
            }
            else
            {
                _scrollTarget = Mathf.Clamp01(_scrollTarget);
            }

            _scroll.verticalNormalizedPosition = _scrollTarget;
            _scrollApplied = _scrollTarget;
        }

        // ---- the conditions themselves, in one place so they can be read together ----

        private static bool AnalogOn()
        {
            return FpvGogglesPlugin.AnalogEnabled.Value;
        }

        /// <summary>
        /// True while the composite pass is doing the radio link for real. Everything it covers
        /// is switched off in the overlay and in the post processing, so the settings for those
        /// stand-ins have nothing left to change.
        /// </summary>
        private static bool CompositeOn()
        {
            return AnalogOn() && FpvGogglesPlugin.CompositeEnabled.Value;
        }

        private static bool OverlayStandInOn()
        {
            return AnalogOn() && !FpvGogglesPlugin.CompositeEnabled.Value;
        }

        private static bool ImageOn()
        {
            return AnalogOn() && FpvGogglesPlugin.ImageProcessing.Value;
        }

        /// <summary>Sections that vanish entirely, heading and all, when nothing in them applies.</summary>
        private static Func<bool> SectionCondition(string section)
        {
            if (section == "Analog Image" || section == "Composite Video") return AnalogOn;
            return null;
        }

        private static Func<bool> ConditionFor(ConfigEntryBase entry)
        {
            string section = entry.Definition.Section;
            string key = entry.Definition.Key;

            if (section == "Composite Video")
            {
                return key == "Enable Composite Video" ? (Func<bool>)AnalogOn : CompositeOn;
            }

            if (section == "Analog Image")
            {
                if (key == "Enable Image Processing") return AnalogOn;

                // Both of these are the composite pass's job once it is running: it loses the
                // colour because the noise drowns the subcarrier, and it produces real chroma
                // artefacts rather than an approximation of them.
                if (key == "Colour Loss" || key == "Chromatic Aberration") return OverlayStandInOn;

                return ImageOn;
            }

            if (section == "Analog Video")
            {
                if (key == "Enable Analog Video") return null;

                // Painted snow and painted scanlines. The composite decode has both for real.
                if (key == "Static Strength" || key == "Base Grain" ||
                    key == "Scanline Strength" || key == "Scanline Count")
                {
                    return OverlayStandInOn;
                }

                return AnalogOn;
            }

            if (section == "Goggle Mask" && key != "Enable Mask")
            {
                return delegate { return FpvGogglesPlugin.MaskEnabled.Value; };
            }

            if (section == "Horizon" && key != "Enable Horizon")
            {
                return delegate { return FpvGogglesPlugin.HorizonEnabled.Value; };
            }

            return null;
        }

        private static void BuildRows()
        {
            _conditional.Clear();
            BuildUuvrRows();

            ConfigFile config = FpvGogglesPlugin.Configuration;
            if (config == null) return;

            // Grouped the way the file is, so anyone who has edited it by hand recognises the
            // order. Sections arrive in insertion order, which is the order they were declared.
            string section = null;

            foreach (ConfigDefinition definition in config.Keys)
            {
                ConfigEntryBase entry;
                try { entry = config[definition]; }
                catch (Exception) { continue; }

                if (entry == null || !IsShown(entry)) continue;

                if (definition.Section != section)
                {
                    section = definition.Section;

                    GameObject heading = AddHeading(section);
                    Func<bool> sectionCondition = SectionCondition(section);
                    if (sectionCondition != null) ShowWhen(heading, sectionCondition);
                }

                GameObject row = AddEntryRow(entry);
                Func<bool> condition = ConditionFor(entry);
                if (row != null && condition != null) ShowWhen(row, condition);
            }
        }

        /// <summary>
        /// Key bindings and free text stay in the file. A slider cannot express either, and a
        /// menu that asks you to type is a menu you cannot use with a headset on.
        /// </summary>
        private static bool IsShown(ConfigEntryBase entry)
        {
            Type type = entry.SettingType;

            if (type == typeof(HotKey)) return false;
            if (type == typeof(string)) return false;

            // Not a setting to flick while flying. It re-parents the game's canvases to UUVR's
            // capture camera, and turning it off mid-flight leaves the HUD in a state UUVR does
            // not put back - the same thing that stops VR being cleanly switchable mid-flight.
            if (entry.Definition.Key == "HUD On VR Plane In Flight") return false;

            // The head tracking switches are the reason this mod exists. Turning them off in a
            // menu you opened mid-flight would hand your head back control of the camera while
            // you are flying, which is not a mistake worth making easy to make.
            if (entry.Definition.Section == "Head Tracking") return false;

            return type == typeof(bool) || type == typeof(float) || type == typeof(int) || type.IsEnum;
        }

        /// <summary>
        /// Two settings that are not ours. HUD size and position live in UUVR's own config, and
        /// they are the two people reach for most - leaving them out would defeat the point.
        /// </summary>
        private static void BuildUuvrRows()
        {
            AddHeading("HUD Plane (UUVR)");

            AddSliderRow("Size", "How large the HUD is on the plane in front of you.",
                0.2f, 3f, false,
                delegate { return FpvGogglesRunner.GetUiScale(); },
                delegate(float v) { FpvGogglesRunner.SetUiScale(v); });

            // A narrow range on purpose. Changes apply as you drag, and a metre either way was
            // enough to push the HUD clean out of view - at which point you are dragging a
            // slider to find something you can no longer see.
            AddSliderRow("Horizontal position", "Shifts the HUD plane left and right, in metres.",
                -0.35f, 0.35f, false,
                delegate { return FpvGogglesRunner.GetUiOffset().x; },
                delegate(float v)
                {
                    Vector2 offset = FpvGogglesRunner.GetUiOffset();
                    FpvGogglesRunner.SetUiOffset(new Vector2(v, offset.y));
                });

            AddSliderRow("Vertical position", "Shifts the HUD plane up and down, in metres.",
                -0.35f, 0.35f, false,
                delegate { return FpvGogglesRunner.GetUiOffset().y; },
                delegate(float v)
                {
                    Vector2 offset = FpvGogglesRunner.GetUiOffset();
                    FpvGogglesRunner.SetUiOffset(new Vector2(offset.x, v));
                });
        }

        private static GameObject AddEntryRow(ConfigEntryBase entry)
        {
            string label = entry.Definition.Key;
            string tip = entry.Description != null ? entry.Description.Description : null;
            Type type = entry.SettingType;

            if (type == typeof(bool))
            {
                return AddToggleRow(label, tip,
                    delegate { return (bool)entry.BoxedValue; },
                    delegate(bool v) { entry.BoxedValue = v; });
            }

            if (type == typeof(float) || type == typeof(int))
            {
                float min, max;
                Range(entry, type, out min, out max);
                bool whole = type == typeof(int);

                return AddSliderRow(label, tip, min, max, whole,
                    delegate { return Convert.ToSingle(entry.BoxedValue); },
                    delegate(float v)
                    {
                        entry.BoxedValue = whole ? (object)Mathf.RoundToInt(v) : (object)v;
                    });
            }

            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                return AddStepperRow(label, tip,
                    delegate { return entry.BoxedValue.ToString(); },
                    delegate(int direction)
                    {
                        int index = Array.IndexOf(values, entry.BoxedValue);
                        if (index < 0) index = 0;
                        index = (index + direction + values.Length) % values.Length;
                        entry.BoxedValue = values.GetValue(index);
                    });
            }

            return null;
        }

        private static void Range(ConfigEntryBase entry, Type type, out float min, out float max)
        {
            min = 0f;
            max = 1f;

            AcceptableValueBase acceptable = entry.Description != null ? entry.Description.AcceptableValues : null;

            AcceptableValueRange<float> floats = acceptable as AcceptableValueRange<float>;
            if (floats != null)
            {
                min = floats.MinValue;
                max = floats.MaxValue;
                return;
            }

            AcceptableValueRange<int> ints = acceptable as AcceptableValueRange<int>;
            if (ints != null)
            {
                min = ints.MinValue;
                max = ints.MaxValue;
                return;
            }

            // No declared range. A guess beats no slider, and the value shown next to it makes
            // clear what actually happened.
            if (type == typeof(int)) { min = 0f; max = 100f; }
        }

        // ------------------------------------------------------------------
        // Row shapes
        // ------------------------------------------------------------------

        private static GameObject AddHeading(string text)
        {
            GameObject row = NewRow(46f, false);
            RectTransform rect = row.GetComponent<RectTransform>();

            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(16, 14, 0, 8);

            // Small, spaced capitals rather than a colour. It reads as a heading without being
            // the loudest thing on the panel.
            TMPro.TextMeshProUGUI heading = NewText("Heading", rect, text.ToUpper(), 13, TextAnchor.LowerLeft);
            heading.color = TextMuted;
            heading.characterSpacing = 6f;
            heading.fontStyle = TMPro.FontStyles.Bold;
            return row;
        }

        /// <summary>
        /// A sliding switch, not a tick box.
        ///
        /// The old one was a square that filled the row and put a green square inside it - big,
        /// blunt, and it told you nothing at a glance about which way was on. A knob that sits
        /// left or right reads correctly from the corner of your eye, which is how you read it
        /// with a headset on.
        /// </summary>
        private static GameObject AddToggleRow(string label, string tip, Func<bool> get, Action<bool> set)
        {
            GameObject row = NewRow(RowHeight, true);
            RectTransform rect = row.GetComponent<RectTransform>();

            AddLabel(rect, label, tip);

            Image hit = NewHitArea("Switch", rect);
            SetLayout(hit.gameObject, 52f);

            Image track = NewRounded("Track", hit.rectTransform, TrackBack, 13);
            RectTransform trackRect = track.rectTransform;
            track.raycastTarget = false;
            trackRect.anchorMin = new Vector2(0.5f, 0.5f);
            trackRect.anchorMax = new Vector2(0.5f, 0.5f);
            trackRect.pivot = new Vector2(0.5f, 0.5f);
            trackRect.sizeDelta = new Vector2(46f, 26f);
            trackRect.anchoredPosition = Vector2.zero;

            Image knob = NewCircle("Knob", trackRect, KnobOff, 20);
            RectTransform knobRect = knob.rectTransform;
            knob.raycastTarget = false;
            knobRect.anchorMin = new Vector2(0.5f, 0.5f);
            knobRect.anchorMax = new Vector2(0.5f, 0.5f);
            knobRect.pivot = new Vector2(0.5f, 0.5f);
            knobRect.sizeDelta = new Vector2(20f, 20f);

            Toggle toggle = hit.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = track;
            toggle.graphic = null;
            // Fully qualified: this class has a Toggle() method of its own, and the name of a
            // method wins over the name of a type when both are in scope.
            toggle.toggleTransition = UnityEngine.UI.Toggle.ToggleTransition.None;
            toggle.isOn = get();
            toggle.onValueChanged.AddListener(delegate(bool v) { set(v); });
            Tint(toggle);

            // Where the knob is right now, as opposed to where it belongs. Set outright here so
            // the panel opens with the switches already in position rather than sliding into it.
            float slide = toggle.isOn ? 1f : 0f;
            ApplySwitch(knobRect, track, knob, slide);

            _tickers.Add(delegate
            {
                if (track == null || knob == null) return;

                float wanted = toggle.isOn ? 1f : 0f;
                if (Mathf.Abs(slide - wanted) < 0.001f)
                {
                    if (slide != wanted) { slide = wanted; ApplySwitch(knobRect, track, knob, slide); }
                    return;
                }

                slide = Mathf.Lerp(slide, wanted, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
                ApplySwitch(knobRect, track, knob, slide);
            });

            _refreshers.Add(delegate { toggle.isOn = get(); });
            return row;
        }

        private static void ApplySwitch(RectTransform knobRect, Image track, Image knob, float slide)
        {
            knobRect.anchoredPosition = new Vector2(Mathf.Lerp(-10f, 10f, slide), 0f);
            track.color = Color.Lerp(TrackBack, Accent, slide);
            knob.color = Color.Lerp(KnobOff, KnobOn, slide);
        }

        /// <summary>
        /// Hover and press feedback, kept to a brightness change. Anything more definite means
        /// picking a second colour for every control, and they would all drift apart.
        /// </summary>
        private static void Tint(Selectable selectable)
        {
            ColorBlock colours = selectable.colors;
            colours.normalColor = Color.white;
            colours.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colours.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colours.selectedColor = Color.white;
            colours.fadeDuration = 0.08f;
            selectable.colors = colours;
        }

        private static GameObject AddSliderRow(string label, string tip, float min, float max, bool whole,
            Func<float> get, Action<float> set)
        {
            GameObject row = NewRow(RowHeight, true);
            RectTransform rect = row.GetComponent<RectTransform>();

            AddLabel(rect, label, tip);

            // The slider component sits on the full height cell so the whole strip is draggable,
            // while the bar you see is a thin shape centred inside it. Aiming at a six pixel line
            // through a headset is not a game anyone wants to play.
            Image hit = NewHitArea("Slider", rect);
            SetLayout(hit.gameObject, 300f);

            Image track = NewRounded("Bar", hit.rectTransform, TrackBack, 3);
            RectTransform trackRect = track.rectTransform;
            track.raycastTarget = false;
            trackRect.anchorMin = new Vector2(0f, 0.5f);
            trackRect.anchorMax = new Vector2(1f, 0.5f);
            trackRect.pivot = new Vector2(0.5f, 0.5f);

            // Inset by the handle's radius at each end, so the handle stays inside the cell at
            // both extremes instead of hanging over the label next to it.
            trackRect.sizeDelta = new Vector2(-18f, 6f);
            trackRect.anchoredPosition = Vector2.zero;

            Image fill = NewRounded("Fill", trackRect, Accent, 3);
            RectTransform fillRect = fill.rectTransform;
            fill.raycastTarget = false;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            Image handle = NewCircle("Handle", trackRect, new Color(0.94f, 0.95f, 0.97f, 1f), 18);
            RectTransform handleRect = handle.rectTransform;
            handle.raycastTarget = false;
            handleRect.sizeDelta = new Vector2(18f, 12f);

            TMPro.TextMeshProUGUI value = NewText("Value", rect, "", 15, TextAnchor.MiddleRight);
            value.color = TextValue;
            SetLayout(value.gameObject, 72f);

            Slider slider = hit.gameObject.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = whole;
            slider.value = Mathf.Clamp(get(), min, max);
            Tint(slider);

            value.text = Format(slider.value, whole);
            slider.onValueChanged.AddListener(delegate(float v)
            {
                set(v);
                value.text = Format(v, whole);
            });

            _refreshers.Add(delegate
            {
                slider.value = Mathf.Clamp(get(), min, max);
                value.text = Format(slider.value, whole);
            });

            return row;
        }

        private static GameObject AddStepperRow(string label, string tip, Func<string> get, Action<int> step)
        {
            GameObject row = NewRow(RowHeight, true);
            RectTransform rect = row.GetComponent<RectTransform>();

            AddLabel(rect, label, tip);

            // Built before the buttons that change it, so both of their handlers can close over
            // it - the left one would otherwise capture a variable that is still null.
            TMPro.TextMeshProUGUI value = NewText("Value", rect, get(), 15, TextAnchor.MiddleCenter);
            value.color = TextValue;

            Button back = NewStepButton("Back", rect, "‹", delegate { step(-1); value.text = get(); });

            // Ordered so the arrows end up either side of the value.
            value.transform.SetSiblingIndex(back.transform.GetSiblingIndex() + 1);
            SetLayout(value.gameObject, 150f);

            NewStepButton("Forward", rect, "›", delegate { step(1); value.text = get(); });

            _refreshers.Add(delegate { value.text = get(); });
            return row;
        }

        /// <summary>
        /// One of the arrows either side of an enum. Same idea as the switch: a full height cell
        /// to click, with a smaller rounded shape inside it to look at.
        /// </summary>
        private static Button NewStepButton(string name, RectTransform row, string glyph,
            UnityEngine.Events.UnityAction click)
        {
            Image hit = NewHitArea(name, row);
            SetLayout(hit.gameObject, 34f);

            Image box = NewRounded("Box", hit.rectTransform, Surface, 8);
            box.raycastTarget = false;
            RectTransform boxRect = box.rectTransform;
            boxRect.anchorMin = new Vector2(0.5f, 0.5f);
            boxRect.anchorMax = new Vector2(0.5f, 0.5f);
            boxRect.pivot = new Vector2(0.5f, 0.5f);
            boxRect.sizeDelta = new Vector2(30f, 28f);
            boxRect.anchoredPosition = Vector2.zero;

            TMPro.TextMeshProUGUI text = NewText("Text", boxRect, glyph, 18, TextAnchor.MiddleCenter);
            text.color = TextPrimary;
            Stretch(text.rectTransform);

            Button button = hit.gameObject.AddComponent<Button>();
            button.targetGraphic = box;
            button.onClick.AddListener(click);
            Tint(button);

            return button;
        }

        private static void AddLabel(RectTransform row, string label, string tip)
        {
            TMPro.TextMeshProUGUI text = NewText("Label", row, label, 16, TextAnchor.MiddleLeft);
            text.color = TextPrimary;

            LayoutElement layout = text.gameObject.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            layout.minWidth = 200f;
        }

        /// <summary>
        /// One line of the list. Control rows get a faint rounded plate behind them: it separates
        /// them from each other, and it keeps the labels legible when the panel is sitting over a
        /// bright sky rather than over a menu.
        /// </summary>
        private static GameObject NewRow(float height, bool surface)
        {
            GameObject row = NewUi("Row", _content);

            if (surface)
            {
                Image background = row.AddComponent<Image>();
                background.color = RowBack;
                background.sprite = Rounded(8);
                background.type = Image.Type.Sliced;
                background.raycastTarget = true;
            }

            HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(16, 14, 0, 0);
            layout.spacing = 10f;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.MiddleLeft;

            LayoutElement element = row.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;

            return row;
        }

        private static void SetLayout(GameObject part, float width)
        {
            LayoutElement layout = part.AddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.flexibleWidth = 0f;
        }

        private static string Format(float value, bool whole)
        {
            if (whole) return Mathf.RoundToInt(value).ToString();
            return value.ToString(Mathf.Abs(value) < 10f ? "0.00" : "0.#");
        }

        // ------------------------------------------------------------------
        // Colours and shapes
        // ------------------------------------------------------------------

        // One place for the palette, so nothing drifts.
        //
        // Every one of these is fully opaque, and that is not a style choice. The panel is drawn
        // into UUVR's capture texture before it is shown on the plane, and Unity's UI blend does
        // not accumulate alpha correctly into a texture: each translucent layer leaves the pixel
        // *less* opaque than the one under it. So a panel at 97% with rows at 3.5% over it came
        // out as a grey wash with a bright hangar showing through - the rows lighter than the
        // panel they sat on, which is the giveaway. At alpha 1 the arithmetic has nothing to get
        // wrong.
        private static readonly Color PanelBack = new Color(0.078f, 0.086f, 0.102f, 1f);
        private static readonly Color PanelEdge = new Color(0.20f, 0.22f, 0.26f, 1f);
        private static readonly Color Divider = new Color(0.16f, 0.18f, 0.21f, 1f);
        private static readonly Color RowBack = new Color(0.118f, 0.129f, 0.153f, 1f);
        private static readonly Color Surface = new Color(0.157f, 0.173f, 0.204f, 1f);
        private static readonly Color TrackBack = new Color(0.196f, 0.212f, 0.247f, 1f);
        private static readonly Color Accent = new Color(0.35f, 0.60f, 0.85f, 1f);
        private static readonly Color TextPrimary = new Color(0.90f, 0.92f, 0.95f, 1f);
        private static readonly Color TextMuted = new Color(0.55f, 0.59f, 0.66f, 1f);
        private static readonly Color TextValue = new Color(0.72f, 0.76f, 0.82f, 1f);
        private static readonly Color KnobOff = new Color(0.60f, 0.64f, 0.70f, 1f);
        private static readonly Color KnobOn = new Color(1f, 1f, 1f, 1f);

        private const float RowHeight = 40f;

        private static readonly Dictionary<int, Sprite> _roundedSprites = new Dictionary<int, Sprite>();
        private static readonly Dictionary<int, Sprite> _circleSprites = new Dictionary<int, Sprite>();
        private static Sprite _outlineSprite;

        /// <summary>
        /// How much of a pixel a rounded rectangle covers. Half a pixel of falloff at the edge is
        /// all it takes to stop the corners looking like stairs.
        /// </summary>
        private static float Coverage(int x, int y, int size, float radius)
        {
            float px = x + 0.5f;
            float py = y + 0.5f;
            float cx = Mathf.Clamp(px, radius, size - radius);
            float cy = Mathf.Clamp(py, radius, size - radius);
            float dx = px - cx;
            float dy = py - cy;
            return Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
        }

        private static Texture2D NewShapeTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        /// <summary>
        /// A rounded rectangle, drawn once per radius and stretched by nine slice scaling.
        ///
        /// uGUI without a sprite draws nothing but hard cornered boxes, which is most of why the
        /// old panel looked like a debug overlay. The whole shape is white; the colour comes from
        /// the Image, so one texture serves every surface at that radius.
        /// </summary>
        private static Sprite Rounded(int radius)
        {
            Sprite cached;
            if (_roundedSprites.TryGetValue(radius, out cached) && cached != null) return cached;

            int size = radius * 2 + 2;
            Texture2D texture = NewShapeTexture(size);
            Color32[] pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    byte alpha = (byte)Mathf.RoundToInt(255f * Coverage(x, y, size, radius));
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            // FullRect, not Tight: a tight mesh would trim the transparent corners away and the
            // nine slice borders would then describe a rectangle that is no longer there.
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            sprite.hideFlags = HideFlags.HideAndDontSave;

            _roundedSprites[radius] = sprite;
            return sprite;
        }

        /// <summary>A plain disc, for switch knobs and slider handles.</summary>
        private static Sprite Circle(int diameter)
        {
            Sprite cached;
            if (_circleSprites.TryGetValue(diameter, out cached) && cached != null) return cached;

            Texture2D texture = NewShapeTexture(diameter);
            Color32[] pixels = new Color32[diameter * diameter];

            for (int y = 0; y < diameter; y++)
            {
                for (int x = 0; x < diameter; x++)
                {
                    byte alpha = (byte)Mathf.RoundToInt(255f * Coverage(x, y, diameter, diameter * 0.5f));
                    pixels[y * diameter + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, diameter, diameter),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;

            _circleSprites[diameter] = sprite;
            return sprite;
        }

        /// <summary>
        /// The panel's hairline, as a ring rather than a filled shape. Drawn sliced with the
        /// centre left out, so the one pixel edge stays one pixel however far it is stretched.
        /// </summary>
        private static Sprite Outline()
        {
            if (_outlineSprite != null) return _outlineSprite;

            const int radius = 16;
            const float thickness = 1.4f;
            int size = radius * 2 + 2;

            Texture2D texture = NewShapeTexture(size);
            Color32[] pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float outer = Coverage(x, y, size, radius);
                    float inner = Coverage(x, y, size, radius - thickness);
                    byte alpha = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(outer - inner));
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            _outlineSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            _outlineSprite.hideFlags = HideFlags.HideAndDontSave;
            return _outlineSprite;
        }

        // ------------------------------------------------------------------
        // Small uGUI helpers
        // ------------------------------------------------------------------

        private static int _uiLayer = -2;

        private static GameObject NewUi(string name, Transform parent)
        {
            // A project without a layer called UI hands back -1, and assigning that is an error
            // rather than a default. Layer 5 is where Unity puts UI in every standard setup.
            if (_uiLayer == -2)
            {
                _uiLayer = LayerMask.NameToLayer("UI");
                if (_uiLayer < 0) _uiLayer = 5;
            }

            GameObject part = new GameObject(name);
            part.layer = _uiLayer;
            RectTransform rect = part.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            return part;
        }

        private static Image NewImage(string name, Transform parent, Color colour)
        {
            GameObject part = NewUi(name, parent);
            Image image = part.AddComponent<Image>();
            image.color = colour;
            return image;
        }

        private static Image NewRounded(string name, Transform parent, Color colour, int radius)
        {
            Image image = NewImage(name, parent, colour);
            image.sprite = Rounded(radius);
            image.type = Image.Type.Sliced;
            return image;
        }

        private static Image NewCircle(string name, Transform parent, Color colour, int diameter)
        {
            Image image = NewImage(name, parent, colour);
            image.sprite = Circle(diameter);
            image.type = Image.Type.Simple;
            image.preserveAspect = true;
            return image;
        }

        /// <summary>
        /// An invisible box that still catches clicks. Every control here is a small shape inside
        /// a full height cell: the shape is what you look at, this is what you hit.
        /// </summary>
        private static Image NewHitArea(string name, Transform parent)
        {
            Image image = NewImage(name, parent, new Color(0f, 0f, 0f, 0f));
            image.raycastTarget = true;
            return image;
        }

        /// <summary>
        /// TextMeshPro rather than the old uGUI Text, and not for looks. Legacy Text needs a
        /// Font, Unity's built-in one is not in a shipped player, and Liftoff draws practically
        /// everything with TextMeshPro - so a TMP font asset is certain to be loaded while a
        /// legacy one might not be. A menu whose labels are all blank is a bad way to find out.
        /// </summary>
        private static TMPro.TextMeshProUGUI NewText(string name, Transform parent, string content,
            int size, TextAnchor anchor)
        {
            GameObject part = NewUi(name, parent);
            TMPro.TextMeshProUGUI text = part.AddComponent<TMPro.TextMeshProUGUI>();

            if (_font != null) text.font = _font;
            text.fontSize = size;
            text.text = content;
            text.alignment = Align(anchor);
            text.color = Color.white;
            text.enableWordWrapping = false;
            text.overflowMode = TMPro.TextOverflowModes.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static TMPro.TextAlignmentOptions Align(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.MiddleCenter: return TMPro.TextAlignmentOptions.Center;
                case TextAnchor.MiddleRight: return TMPro.TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft: return TMPro.TextAlignmentOptions.BottomLeft;
                default: return TMPro.TextAlignmentOptions.Left;
            }
        }

        private static Button NewButton(string name, Transform parent, string label, UnityEngine.Events.UnityAction click)
        {
            Image background = NewRounded(name, parent, Surface, 8);

            TMPro.TextMeshProUGUI text = NewText("Text", background.rectTransform, label, 15, TextAnchor.MiddleCenter);
            text.color = TextPrimary;
            Stretch(text.rectTransform);

            Button button = background.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(click);
            Tint(button);

            return button;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // ------------------------------------------------------------------
        // The two things the panel cannot build itself
        // ------------------------------------------------------------------

        /// <summary>
        /// uGUI needs one of these somewhere in the scene or nothing is clickable. The game has
        /// one for its own menus, but there is no guarantee it is awake during a flight, so an
        /// absent one is replaced rather than assumed.
        /// </summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null && EventSystem.current.isActiveAndEnabled) return;

            EventSystem[] existing = UnityEngine.Object.FindObjectsOfType<EventSystem>();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] == null) continue;
                existing[i].enabled = true;
                if (existing[i].GetComponent<StandaloneInputModule>() == null)
                {
                    existing[i].gameObject.AddComponent<StandaloneInputModule>();
                }
                return;
            }

            GameObject events = new GameObject("FpvSettingsEventSystem");
            events.AddComponent<EventSystem>();
            events.AddComponent<StandaloneInputModule>();
            UnityEngine.Object.DontDestroyOnLoad(events);

            FpvGogglesPlugin.Log.LogInfo("No active EventSystem found; added one for the settings menu.");
        }

        /// <summary>
        /// The font TextMeshPro is already using, whatever that is. Asking for a specific one
        /// would mean shipping it; borrowing the game's means the menu looks like it belongs.
        /// </summary>
        private static void EnsureFont()
        {
            if (_font != null) return;

            try { _font = TMPro.TMP_Settings.defaultFontAsset; }
            catch (Exception) { }

            if (_font == null)
            {
                TMPro.TMP_FontAsset[] loaded = Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
                if (loaded.Length > 0) _font = loaded[0];
            }

            if (_font == null)
            {
                FpvGogglesPlugin.Log.LogWarning(
                    "Found no TextMeshPro font; the settings menu will open with blank labels.");
            }
            else
            {
                FpvGogglesPlugin.Log.LogInfo("Settings menu using font '" + _font.name + "'.");
            }
        }
    }
}
