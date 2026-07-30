using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// Fixes the bug in UUVR that makes the game's HUD blink off the VR plane.
    ///
    /// UUVR decides whether to redirect a screen space camera canvas by asking whether that
    /// canvas renders into a texture - a sensible question, because a canvas drawn onto a screen
    /// inside the game world has no business being pulled in front of your eyes. But it asks the
    /// canvas as it stands right now, and the moment UUVR has redirected it, its camera *is* the
    /// capture camera, which renders into a texture. So the answer flips to "leave it alone" and
    /// the HUD is dropped off the plane. The change after that, it has been restored, the answer
    /// flips back, and the HUD returns.
    ///
    /// One flicker per setting change is easy to live with, and easy to miss. A slider that
    /// writes a setting every frame turns it into a strobe - which is how this was found.
    ///
    /// The fix is to correct the question rather than the answer: while UUVR is deciding, the
    /// canvas is handed back the camera it had before redirection, so the check sees what it was
    /// meant to see. Every other branch of UUVR's decision - the patch mode, which kinds of
    /// canvas the user asked for - runs exactly as written and is not second guessed here. Switch
    /// UUVR's UI patch mode off and canvases still come back, as they should.
    /// </summary>
    internal static class UuvrCanvasFix
    {
        private static FieldInfo _isPatchedField;
        private static FieldInfo _canvasField;
        private static FieldInfo _originalCameraField;

        internal static void Apply()
        {
            Type redirectType = AccessTools.TypeByName("Uuvr.VrUi.PatchModes.CanvasRedirect");
            if (redirectType == null)
            {
                FpvGogglesPlugin.Log.LogWarning(
                    "Could not find Uuvr.VrUi.PatchModes.CanvasRedirect. The HUD will blink off the VR plane whenever a setting changes.");
                return;
            }

            MethodInfo target = AccessTools.Method(redirectType, "ShouldPatchCanvas");
            _isPatchedField = AccessTools.Field(redirectType, "_isPatched");
            _canvasField = AccessTools.Field(redirectType, "_canvas");
            _originalCameraField = AccessTools.Field(redirectType, "_originalWorldCamera");

            // All four are private members of somebody else's mod, so none of them are promises.
            // Naming what is missing beats a patch that silently does nothing.
            if (target == null || _isPatchedField == null || _canvasField == null || _originalCameraField == null)
            {
                FpvGogglesPlugin.Log.LogWarning("UUVR's CanvasRedirect looks different than expected (missing " +
                    (target == null ? "ShouldPatchCanvas " : "") +
                    (_isPatchedField == null ? "_isPatched " : "") +
                    (_canvasField == null ? "_canvas " : "") +
                    (_originalCameraField == null ? "_originalWorldCamera " : "") +
                    "). The HUD will blink off the VR plane whenever a setting changes.");
                return;
            }

            try
            {
                new Harmony(FpvGogglesPlugin.Guid).Patch(
                    target,
                    new HarmonyMethod(AccessTools.Method(typeof(UuvrCanvasFix), "ShouldPatchPrefix")),
                    new HarmonyMethod(AccessTools.Method(typeof(UuvrCanvasFix), "ShouldPatchPostfix")));

                FpvGogglesPlugin.Log.LogInfo(
                    "Patched Uuvr CanvasRedirect.ShouldPatchCanvas - the HUD stays on the VR plane across setting changes.");
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Could not patch UUVR's canvas check: " + e.Message);
            }
        }

        /// <summary>What was swapped out, so the postfix can put it back.</summary>
        private sealed class Swap
        {
            internal Canvas Canvas;
            internal Camera Capture;
        }

        /// <summary>
        /// Hands the canvas back its own camera for the duration of the check. Nothing renders
        /// between here and the postfix - this is one synchronous call - so the swap is never on
        /// screen.
        /// </summary>
        private static void ShouldPatchPrefix(object __instance, out object __state)
        {
            __state = null;

            try
            {
                object patched = _isPatchedField.GetValue(__instance);
                if (!(patched is bool) || !(bool)patched) return;

                Canvas canvas = _canvasField.GetValue(__instance) as Canvas;
                if (canvas == null) return;

                Camera capture = canvas.worldCamera;
                Camera original = _originalCameraField.GetValue(__instance) as Camera;
                if (capture == original) return;

                canvas.worldCamera = original;

                // Carried through the call rather than parked in a static field: the swapped out
                // camera can legitimately be null, so "is there something to put back" needs an
                // answer of its own.
                Swap swap = new Swap();
                swap.Canvas = canvas;
                swap.Capture = capture;
                __state = swap;
            }
            catch (Exception)
            {
                __state = null;
            }
        }

        private static void ShouldPatchPostfix(object __state)
        {
            Swap swap = __state as Swap;
            if (swap == null || swap.Canvas == null) return;

            try { swap.Canvas.worldCamera = swap.Capture; }
            catch (Exception) { }
        }
    }
}
