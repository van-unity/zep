using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zep.Editor.UI.AssetsArea.MaterialThumbnails
{
    // Owns the offscreen scene that renders a single material into a fixed
    // 256x256 RenderTexture: a PreviewRenderUtility (camera + preview scene),
    // a key/fill/rim light rig, a procedural studio environment cubemap for
    // reflections, and procedural sphere/plane meshes. One rig instance is
    // shared by every render; the provider creates it lazily and disposes it
    // the moment the render queue drains (PreviewRenderUtility holds a camera,
    // lights, and an RT — its finalizer only LOGS a leak, it cannot clean up,
    // so disposal must be deterministic).
    //
    // The render path is deliberately identical to Unity's own material
    // thumbnail: DrawMesh + PreviewRenderUtility.Render(allowScriptableRenderPipeline: true)
    // — the exact call MaterialEditor makes — so Built-in and URP shaders
    // render correctly instead of magenta. HDRP is rejected upstream by the
    // provider (needs warm-up frames; falls back to the stock sphere).
    //
    // All colors in this file are rendered texture CONTENT (lights, ambient,
    // environment), not element styling — the styling-standards no-literal
    // rule governs USS/UI writes and does not apply here.
    internal sealed class MaterialThumbnailRenderRig : IDisposable
    {
        private PreviewRenderUtility _preview;
        private Mesh _sphereMesh;
        private Mesh _planeMesh;
        private Cubemap _environment;
        private RenderTexture _output;
        private bool _disposed;

        private readonly int _size;

        public MaterialThumbnailRenderRig(int size)
        {
            _size = size;
        }

        // The blit target the provider reads back from. Deliberately
        // LINEAR-typed: reading back an sRGB texture is ambiguous (the
        // conversion the readback applies differs by backend and has shipped
        // dark thumbnails), while a linear target reads back raw bytes with
        // no conversion in play. The provider applies the linear→sRGB encode
        // on the CPU where it is deterministic; under Gamma color space the
        // pixels are already display-ready and no encode happens anywhere.
        public RenderTexture Output => _output;

        // Renders the material into Output. False means this material cannot
        // be rendered by the rig (skybox preview type, dead shader) and the
        // caller should fall back to the stock preview pipeline.
        public bool Render(Material material)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterialThumbnailRenderRig));

            var mesh = SelectMesh(material);
            if (mesh == null)
            {
                return false;
            }

            EnsureScene();

            _preview.BeginStaticPreview(new Rect(0f, 0f, _size, _size));

            // AFTER Begin, before Render: BeginStaticPreview resets the
            // preview scene's customReflectionTexture to Unity's default
            // cubemap on every call, so the studio environment must be
            // re-assigned inside the Begin/End window. While the preview
            // lighting override is active, RenderSettings writes target the
            // preview scene, not the user's scene.
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = _environment;
            RenderSettings.reflectionIntensity = 1.15f;

            var camera = _preview.camera;
            PlaceCamera(camera, mesh);

            _preview.DrawMesh(mesh, Matrix4x4.identity, material, 0);
            _preview.Render(allowScriptableRenderPipeline: true);

            // EndPreview() is the public handle to the preview RT (the field
            // itself is internal). It also restores the lighting override and
            // saved render state, so the blit must come after it.
            var previewRt = _preview.EndPreview();
            Graphics.Blit(previewRt, _output);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Cleanup() is the only release path: it destroys the preview
            // scene, camera, and RT. The finalizer just logs an error.
            _preview?.Cleanup();
            _preview = null;
            DestroyOwned(ref _sphereMesh);
            DestroyOwned(ref _planeMesh);
            DestroyOwned(ref _environment);
            if (_output != null)
            {
                _output.Release();
                UnityEngine.Object.DestroyImmediate(_output);
                _output = null;
            }
        }

        private Mesh SelectMesh(Material material)
        {
            // Same shader-tag contract MaterialEditor honors: UI/particle
            // shaders declare PreviewType=Plane and look wrong on a sphere.
            // Skybox materials need RenderSettings-based rendering the rig
            // doesn't do — stock pipeline handles those.
            var previewType = material.GetTag("PreviewType", searchFallbacks: false, defaultValue: string.Empty);
            if (string.Equals(previewType, "Skybox", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(previewType, "Plane", StringComparison.OrdinalIgnoreCase))
            {
                _planeMesh ??= BuildPlaneMesh();
                return _planeMesh;
            }

            _sphereMesh ??= BuildSphereMesh(stacks: 64, slices: 96, radius: 0.5f);
            return _sphereMesh;
        }

        private void EnsureScene()
        {
            if (_preview != null)
            {
                return;
            }

            _preview = new PreviewRenderUtility();

            var camera = _preview.camera;
            // The preview camera ships with clearFlags=Depth, FOV 15, far 10.
            // SolidColor is required: URP substitutes its own preference
            // background color for preview cameras whose clearFlags is not
            // SolidColor. Alpha 0 so the provider can composite the gradient
            // backdrop behind the readback pixels on the CPU (no backdrop
            // geometry means no cross-pipeline background-shader risk).
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.fieldOfView = 30f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;

            // Key / fill on the two built-in preview lights, rim via an extra
            // GameObject parked in the preview scene (AddSingleGO transfers
            // ownership to the PreviewRenderUtility, which destroys it in
            // Cleanup). Angles and intensities approximate UE's thumbnail
            // studio rig; shadows stay off — a floating primitive has nothing
            // to catch them and they cost a shadow pass per thumb.
            var key = _preview.lights[0];
            key.intensity = 1.35f;
            key.transform.rotation = Quaternion.Euler(38f, -32f, 0f);
            key.color = new Color(1f, 0.965f, 0.92f);
            key.shadows = LightShadows.None;

            var fill = _preview.lights[1];
            fill.intensity = 0.5f;
            fill.transform.rotation = Quaternion.Euler(-15f, 140f, 0f);
            fill.color = new Color(0.8f, 0.86f, 1f);
            fill.shadows = LightShadows.None;

            // Strong rim: the bright top-back edge is what separates a
            // "product shot" from a "lit ball" — it draws the silhouette
            // against the backdrop at every roughness level.
            var rimGo = new GameObject("MaterialThumbRimLight");
            var rim = rimGo.AddComponent<Light>();
            rim.type = LightType.Directional;
            rim.intensity = 1.3f;
            rim.transform.rotation = Quaternion.Euler(18f, 215f, 0f);
            rim.color = new Color(0.88f, 0.94f, 1f);
            rim.shadows = LightShadows.None;
            _preview.AddSingleGO(rimGo);

            // Upward bounce so the lower hemisphere reads as lit-from-a-floor
            // instead of falling to black — the single biggest "dim and
            // muddy" factor on the first in-editor pass.
            var bounceGo = new GameObject("MaterialThumbBounceLight");
            var bounce = bounceGo.AddComponent<Light>();
            bounce.type = LightType.Directional;
            bounce.intensity = 0.35f;
            bounce.transform.rotation = Quaternion.Euler(-60f, -15f, 0f);
            bounce.color = new Color(0.9f, 0.9f, 0.95f);
            bounce.shadows = LightShadows.None;
            _preview.AddSingleGO(bounceGo);

            // Flat ambient is a hard constraint: PreviewRenderUtility.Render()
            // force-sets AmbientMode.Flat + this color on every call while the
            // lighting override is active, so a Trilight gradient is
            // unreachable through Render(). The custom reflection cubemap
            // carries the environment interest instead.
            _preview.ambientColor = new Color(0.28f, 0.30f, 0.35f);

            _environment ??= BuildStudioEnvironment(64);

            // Note on sizing: InitPreview multiplies the requested rect by
            // EditorGUIUtility.pixelsPerPoint, so the preview RT may be 2x
            // on retina displays. The fixed-size blit target below makes the
            // readback deterministic either way (a 512→256 blit is a free
            // supersample, not a bug).
            _output = new RenderTexture(
                _size, _size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "MaterialThumbOutput",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _output.Create();
        }

        private static void PlaceCamera(Camera camera, Mesh mesh)
        {
            // Half-HEIGHT, not extents.magnitude: the magnitude is the
            // bounding-box half-diagonal (0.866 for a unit sphere), which
            // silently framed the sphere at ~58% of the intended size — the
            // "sphere is too small" bug that margin tuning couldn't fix.
            var radius = mesh.bounds.extents.y;
            if (radius <= 0f)
            {
                radius = 0.5f;
            }

            // Sphere-exact framing: a sphere of radius r at distance d has
            // angular radius asin(r/d), so d = margin·r/sin(halfFov) makes
            // the silhouette span 1/margin of the frame height. 1.04 → the
            // sphere fills ~96% of the tile, nearly touching the edges
            // (UE-style dominance); the contact shadow hugs the thin bottom
            // gap, and the rounded-corner mask can't clip the silhouette.
            var distance = radius * 1.04f / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var pitch = 8f * Mathf.Deg2Rad;
            var position = new Vector3(
                0f,
                distance * Mathf.Sin(pitch),
                -distance * Mathf.Cos(pitch));
            camera.transform.position = position;
            camera.transform.rotation = Quaternion.LookRotation(-position.normalized, Vector3.up);
            camera.farClipPlane = distance + radius * 4f;
        }

        // UV sphere with analytic normals and computed tangents (normal maps
        // are the single most common material feature; a mesh without tangents
        // renders them flat). Built in code so the package ships no mesh asset
        // and no hand-written .meta.
        private static Mesh BuildSphereMesh(int stacks, int slices, float radius)
        {
            var vertexCount = (stacks + 1) * (slices + 1);
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uv = new Vector2[vertexCount];

            var index = 0;
            for (var stack = 0; stack <= stacks; stack++)
            {
                var v = (float)stack / stacks;
                var phi = v * Mathf.PI;
                var sinPhi = Mathf.Sin(phi);
                var cosPhi = Mathf.Cos(phi);
                for (var slice = 0; slice <= slices; slice++)
                {
                    var u = (float)slice / slices;
                    var theta = u * Mathf.PI * 2f;
                    var normal = new Vector3(
                        sinPhi * Mathf.Cos(theta),
                        cosPhi,
                        sinPhi * Mathf.Sin(theta));
                    normals[index] = normal;
                    vertices[index] = normal * radius;
                    uv[index] = new Vector2(u, 1f - v);
                    index++;
                }
            }

            var triangles = new int[stacks * slices * 6];
            var t = 0;
            for (var stack = 0; stack < stacks; stack++)
            {
                for (var slice = 0; slice < slices; slice++)
                {
                    var a = stack * (slices + 1) + slice;
                    var b = a + slices + 1;
                    triangles[t++] = a;
                    triangles[t++] = a + 1;
                    triangles[t++] = b;
                    triangles[t++] = a + 1;
                    triangles[t++] = b + 1;
                    triangles[t++] = b;
                }
            }

            var mesh = new Mesh
            {
                name = "MaterialThumbSphere",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = IndexFormat.UInt32,
                vertices = vertices,
                normals = normals,
                uv = uv,
                triangles = triangles,
            };
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildPlaneMesh()
        {
            var mesh = new Mesh
            {
                name = "MaterialThumbPlane",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f),
                    new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(-0.5f, 0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f),
                },
                normals = new[]
                {
                    Vector3.back, Vector3.back, Vector3.back, Vector3.back,
                },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                },
                triangles = new[] { 0, 2, 1, 2, 3, 1 },
            };
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Procedural studio environment: a vertical gray gradient (bright
        // zenith, dark floor) with two soft "light card" hotspots that give
        // smooth and metallic materials the specular interest Unity's flat
        // default environment lacks. Mip chain enabled so rough materials
        // sample sensible blurred lobes.
        private static Cubemap BuildStudioEnvironment(int faceSize)
        {
            var cube = new Cubemap(faceSize, TextureFormat.RGBA32, mipChain: true)
            {
                name = "MaterialThumbEnvironment",
                hideFlags = HideFlags.HideAndDontSave,
            };

            var keyCardDir = new Vector3(-0.45f, 0.65f, -0.6f).normalized;
            var rimCardDir = new Vector3(0.7f, 0.25f, 0.65f).normalized;
            var pixels = new Color[faceSize * faceSize];
            for (var face = 0; face < 6; face++)
            {
                for (var y = 0; y < faceSize; y++)
                {
                    for (var x = 0; x < faceSize; x++)
                    {
                        var dir = CubemapDirection((CubemapFace)face, x, y, faceSize);
                        var altitude = dir.y * 0.5f + 0.5f;
                        var baseTone = Mathf.Lerp(0.10f, 0.42f, altitude * altitude);

                        var key = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(dir, keyCardDir)), 24f) * 1.6f;
                        var rim = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(dir, rimCardDir)), 32f) * 0.9f;

                        var tone = baseTone + key + rim;
                        pixels[y * faceSize + x] = new Color(
                            tone,
                            tone * 1.01f,
                            tone * 1.06f,
                            1f);
                    }
                }
                cube.SetPixels(pixels, (CubemapFace)face);
            }

            cube.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return cube;
        }

        // Standard cubemap texel → direction mapping (+X, -X, +Y, -Y, +Z, -Z
        // face order; texel rows run top-down within a face).
        private static Vector3 CubemapDirection(CubemapFace face, int x, int y, int faceSize)
        {
            var u = (x + 0.5f) / faceSize * 2f - 1f;
            var v = (y + 0.5f) / faceSize * 2f - 1f;
            switch (face)
            {
                case CubemapFace.PositiveX: return new Vector3(1f, -v, -u).normalized;
                case CubemapFace.NegativeX: return new Vector3(-1f, -v, u).normalized;
                case CubemapFace.PositiveY: return new Vector3(u, 1f, v).normalized;
                case CubemapFace.NegativeY: return new Vector3(u, -1f, -v).normalized;
                case CubemapFace.PositiveZ: return new Vector3(u, -v, 1f).normalized;
                default: return new Vector3(-u, -v, -1f).normalized;
            }
        }

        private static void DestroyOwned<T>(ref T obj) where T : UnityEngine.Object
        {
            if (obj != null)
            {
                UnityEngine.Object.DestroyImmediate(obj);
                obj = null;
            }
        }
    }
}
