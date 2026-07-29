using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// Artificial horizon in the style of a Betaflight OSD: a bar with a gap in the middle
    /// that rolls and slides with the drone's attitude, plus a fixed centre mark as reference.
    /// That is what an analog goggle actually shows, so it is what the sim should show.
    ///
    /// The bar angle is not derived from a roll value. Roll sign conventions are easy to get
    /// backwards, so instead the real world horizon direction is projected into the view
    /// plane and its angle read off directly - that comes out right by construction.
    /// </summary>
    internal static class HorizonIndicator
    {
        private static GameObject _root;
        private static Transform _bar;
        private static Material _material;
        private static Camera _camera;

        private static float _builtHalfWidth, _builtHalfHeight, _builtDistance;
        private static float _builtWidth, _builtGap, _builtThickness, _builtScale;
        private static bool _built;

        internal static void Update(Camera viewCamera, float halfWidth, float halfHeight, float distance)
        {
            if (viewCamera == null || !FpvGogglesPlugin.HorizonEnabled.Value)
            {
                Teardown();
                return;
            }

            if (_root == null || _camera != viewCamera || _root.transform.parent != viewCamera.transform)
            {
                Build(viewCamera);
                if (_root == null) return;
            }

            if (GeometryChanged(halfWidth, halfHeight, distance))
            {
                Rebuild(halfWidth, halfHeight, distance);
            }

            Transform view = viewCamera.transform;
            Vector3 forward = view.forward;

            // Direction of the world horizon, expressed in the view's own frame.
            Vector3 worldHorizon = Vector3.Cross(Vector3.up, forward);
            if (worldHorizon.sqrMagnitude < 1e-6f)
            {
                // Looking straight up or down: there is no meaningful horizon direction.
                _bar.gameObject.SetActive(false);
                return;
            }

            Vector3 local = view.InverseTransformDirection(worldHorizon.normalized);
            float angle = Mathf.Atan2(local.y, local.x) * Mathf.Rad2Deg;

            // Subtracting the camera tilt turns "where the camera points" into "how the drone
            // sits", which is what a Betaflight OSD draws.
            float pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg
                          - FpvGogglesPlugin.HorizonCameraTilt.Value;

            // The travel scales with the indicator. Shrinking the bar without shrinking how
            // far it moves leaves a tiny line racing across the whole view - which is exactly
            // how it looked before.
            float travel = halfHeight * Mathf.Max(0.05f, FpvGogglesPlugin.HorizonScale.Value);
            float offset = -pitch / Mathf.Max(1f, FpvGogglesPlugin.HorizonRange.Value) * travel;

            // Beyond the configured pitch range the horizon is off the display, as on real
            // hardware.
            bool visible = Mathf.Abs(offset) <= travel;
            if (_bar.gameObject.activeSelf != visible) _bar.gameObject.SetActive(visible);
            if (!visible) return;

            _bar.localRotation = Quaternion.Euler(0f, 0f, angle);
            _bar.localPosition = new Vector3(0f, offset, 0f);
        }

        private static bool GeometryChanged(float halfWidth, float halfHeight, float distance)
        {
            return !_built
                || _builtHalfWidth != halfWidth
                || _builtHalfHeight != halfHeight
                || _builtDistance != distance
                || _builtWidth != FpvGogglesPlugin.HorizonWidth.Value
                || _builtGap != FpvGogglesPlugin.HorizonGap.Value
                || _builtThickness != FpvGogglesPlugin.HorizonThickness.Value
                || _builtScale != FpvGogglesPlugin.HorizonScale.Value;
        }

        private static void Build(Camera viewCamera)
        {
            Teardown();

            _camera = viewCamera;

            _root = new GameObject("FpvHorizon");
            _root.transform.SetParent(viewCamera.transform, false);
            _root.transform.localPosition = Vector3.zero;
            _root.transform.localRotation = Quaternion.identity;
            _root.transform.localScale = Vector3.one;
            _root.layer = viewCamera.gameObject.layer;

            if (_material == null) _material = CreateMaterial();

            _bar = NewPart("Bar", _root.transform).transform;
            NewPart("CentreMark", _root.transform);

            _built = false;
        }

        private static GameObject NewPart(string name, Transform parent)
        {
            GameObject part = new GameObject(name);
            part.transform.SetParent(parent, false);
            part.layer = parent.gameObject.layer;

            part.AddComponent<MeshFilter>();
            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            return part;
        }

        private static void Rebuild(float halfWidth, float halfHeight, float distance)
        {
            float scale = Mathf.Max(0.05f, FpvGogglesPlugin.HorizonScale.Value);
            float outer = halfWidth * Mathf.Clamp01(FpvGogglesPlugin.HorizonWidth.Value) * scale;
            float gap = halfWidth * Mathf.Clamp(FpvGogglesPlugin.HorizonGap.Value, 0f, 0.9f) * scale;
            float thickness = halfHeight * Mathf.Max(0.0005f, FpvGogglesPlugin.HorizonThickness.Value) * scale;
            if (gap >= outer) gap = outer * 0.5f;

            // The bar sits at the origin of its own object so it rolls about the centre of the
            // window; the pitch offset is applied afterwards, in screen space.
            Mesh bar = new Mesh();
            bar.name = "FpvHorizonBar";
            bar.hideFlags = HideFlags.HideAndDontSave;
            FillTwoBars(bar, -outer, -gap, gap, outer, thickness);
            _bar.GetComponent<MeshFilter>().sharedMesh = bar;

            // Fixed reference: a short dash in the gap, plus two small ticks flanking it.
            Transform centre = _root.transform.Find("CentreMark");
            Mesh mark = new Mesh();
            mark.name = "FpvHorizonCentre";
            mark.hideFlags = HideFlags.HideAndDontSave;
            float dash = gap * 0.35f;
            FillTwoBars(mark, -dash, -dash * 0.25f, dash * 0.25f, dash, thickness);
            centre.GetComponent<MeshFilter>().sharedMesh = mark;

            _root.transform.localPosition = new Vector3(0f, 0f, distance);

            _builtHalfWidth = halfWidth;
            _builtHalfHeight = halfHeight;
            _builtDistance = distance;
            _builtWidth = FpvGogglesPlugin.HorizonWidth.Value;
            _builtGap = FpvGogglesPlugin.HorizonGap.Value;
            _builtThickness = FpvGogglesPlugin.HorizonThickness.Value;
            _builtScale = FpvGogglesPlugin.HorizonScale.Value;
            _built = true;
        }

        /// <summary>Two rectangles in the XY plane, wound both ways so culling cannot hide them.</summary>
        private static void FillTwoBars(Mesh mesh, float leftOuter, float leftInner,
            float rightInner, float rightOuter, float thickness)
        {
            Vector3[] vertices =
            {
                new Vector3(leftOuter, -thickness, 0f), new Vector3(leftOuter, thickness, 0f),
                new Vector3(leftInner, thickness, 0f), new Vector3(leftInner, -thickness, 0f),
                new Vector3(rightInner, -thickness, 0f), new Vector3(rightInner, thickness, 0f),
                new Vector3(rightOuter, thickness, 0f), new Vector3(rightOuter, -thickness, 0f),
            };

            int[] triangles =
            {
                0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0,
                4, 5, 6, 4, 6, 7, 6, 5, 4, 7, 6, 4,
            };

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        }

        private static Material CreateMaterial()
        {
            string[] candidates = { "Unlit/Color", "Hidden/Internal-Colored", "Sprites/Default", "UI/Default" };

            Shader shader = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                shader = Shader.Find(candidates[i]);
                if (shader != null) break;
            }

            // Logged because the choice decides whether the depth test can be overridden at all.
            // Unlit/Color has no handle for it, Hidden/Internal-Colored does - and an indicator
            // that vanishes behind a close gate would be blamed on anything but the shader.
            if (shader != null) FpvGogglesPlugin.Log.LogInfo("Horizon using shader '" + shader.name + "'.");
            if (shader == null)
            {
                Material canvasMaterial = Canvas.GetDefaultCanvasMaterial();
                if (canvasMaterial != null) shader = canvasMaterial.shader;
            }
            if (shader == null)
            {
                FpvGogglesPlugin.Log.LogError("Found no shader for the horizon indicator.");
                return null;
            }

            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.name = "FpvHorizon";

            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);

            // Above the goggle mask, so it stays readable over the black border too.
            material.renderQueue = 5001;
            if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)CompareFunction.Always);
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Off);

            return material;
        }

        internal static void Teardown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _bar = null;
            _camera = null;
            _built = false;
        }
    }
}
