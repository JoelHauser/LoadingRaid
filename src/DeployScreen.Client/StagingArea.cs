using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Puts the PMC in the place they are about to deploy to.
    ///
    /// What the screen actually is
    /// ---------------------------
    /// Three things drawn by three cameras and composited:
    ///
    ///     backdrop   a real 3D scene (EnvironmentUIRoot), camera under CameraContainer
    ///     PMC        a real skinned model on layer LayersMaskController.WeaponPreview,
    ///                parented to PlayerModelView's own UI transform, drawn by a preview camera
    ///     UI         the canvas -- banners, map name, timer, cancel
    ///
    /// The character is therefore **not** in the backdrop's 3D space, and moving it there would
    /// mean re-parenting it out of the canvas and changing its layer, which breaks the screen
    /// anchors, the drag-to-rotate and the UI ordering. So this does not try.
    ///
    /// What it does instead is treat the screen as what it already is -- a composite -- and use
    /// the three things that sell one:
    ///
    ///   1. **Parallax.** The backdrop and the PMC are on different cameras, so drifting the
    ///      backdrop camera moves the world *and not the character*. Two art planes at different
    ///      depths shear against each other as well, giving a third relation.
    ///   2. **Light agreement.** MapGrade lights the character with a key and a rim masked to the
    ///      WeaponPreview layer, coloured for the destination. This is the single biggest lever:
    ///      a cut-out is a cut-out because its light disagrees with its surroundings.
    ///   3. **The map as the world.** The player's own art for the map is hung deep in the
    ///      backdrop's 3D space and the menu scene's furniture is switched off, so the picture is
    ///      the place rather than a panel inside it.
    ///
    /// The art plane is a world-space Canvas rather than a quad with a material, and that is a
    /// deliberate choice: a quad needs a shader, Shader.Find only finds shaders the build
    /// actually included, and there is no way to confirm from outside the game which those are.
    /// A world-space Canvas draws through the same UI path the mod already uses for the minimal
    /// background, so nothing has to be guessed.
    /// </summary>
    internal sealed class StagingArea
    {
        /// <summary>How far apart the two art planes sit, as a multiple of the near distance.</summary>
        private const float DepthRatio = 2.6f;

        private readonly MapGrade _grade = new MapGrade();

        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<GameObject> _hidden = new List<GameObject>();

        private Component _screen;
        private Camera _camera;
        private Transform _planeRoot;
        private Transform _playerModel;
        private Component _subCaption;
        private PropertyInfo _subCaptionText;
        private string _subCaptionWas;

        private List<IntelCard> _cards;
        private int _card = -1;
        private double _nextCard;

        private Texture2D _vignette;
        private string _conditions = "conditions unknown";
        private bool _built;
        private string _artState;
        private double _watchNext;
        private bool _warnedOnce;

        internal bool Built { get { return _built; } }

        /// <summary>
        /// What staging actually applied, for the report. compare-loading.ps1 groups on these, so
        /// a capture where the art was missing must not pool with one where it was not -- the same
        /// reasoning as the minimal-mode fields, and the same mistake 1.3.1 had to fix there.
        /// </summary>
        internal string Metadata
        {
            get { return MetadataFor(_built, _hidden.Count, _grade.CharacterLit, _subCaptionText != null, _conditions); }
        }

        internal static string MetadataFor(bool built, int hidden, bool characterLit, bool intel, string conditions)
        {
            return ",\"stagingArtShown\":" + (built ? "true" : "false")
                + ",\"stagingSceneObjectsHidden\":" + hidden
                + ",\"stagingCharacterLit\":" + (characterLit ? "true" : "false")
                + ",\"stagingIntelShown\":" + (intel ? "true" : "false")
                + ",\"raidConditions\":" + LoadTrace.Quote(conditions ?? "unknown");
        }

        internal string Description
        {
            get
            {
                return "conditions=" + _conditions
                    + "; art-planes=" + _created.Count
                    + "; scene-objects-hidden=" + _hidden.Count
                    + "; intel-in-subcaption=" + (_subCaptionText != null)
                    + "; " + _grade.Description;
            }
        }

        // ------------------------------------------------------------------ begin

        internal void Begin(Component screen, string locationId, object location, object session,
            MapGrade.Weather weather)
        {
            _screen = screen;

            try
            {
                var root = CurrentRoot();
                if (root == null) return;

                var camera = BackdropCamera(root);
                if (camera == null) return;

                // Kept so Hide can refuse to switch off anything the staging area itself
                // needs. MainScreenObjects is the game's own list and what is in it cannot be
                // known from outside the game.
                _camera = camera;
                _planeRoot = root.transform;
                _playerModel = PlayerModelTransform(screen);

                // The map says what the place looks like; the raid says what it looks like today.
                if (!DeployScreenPlugin.StagingFollowWeather.Value) weather = default(MapGrade.Weather);

                var grade = MapGrade.ForRaid(locationId, weather);
                _conditions = MapGrade.Describe(weather);

                // Everything about shape comes from the camera, never from Screen: the planes fill
                // its frustum and the art is cropped to the same number, so the two cannot disagree
                // whatever the monitor is.
                var aspect = camera.aspect > 0.01f ? camera.aspect : SafeAspect();

                // The art is the whole point. With none for this map, the menu scene is left
                // exactly as it is -- no hidden furniture, no empty void -- and only the light
                // follows the destination.
                var sprite = MapSprite(locationId, aspect);

                if (sprite != null)
                {
                    BuildPlanes(root, camera, aspect, sprite, grade);
                    HideSceneFurniture(root);
                    HideBanners();
                    _built = true;
                }

                _grade.Begin(root, grade);
                ShowIntel(location, session);

                if (_built || _grade.CharacterLit)
                {
                    DeployScreenPlugin.Log.LogInfo("[DeployScreen] staging area: " + Description);
                }
            }
            catch (Exception error)
            {
                WarnOnce(error);
                Restore();
            }
        }

        private static Component CurrentRoot()
        {
            if (GameTypes.Environment_Current == null || GameTypes.EnvironmentUI_Instance == null) return null;

            try
            {
                if (GameTypes.EnvironmentUI_Instantiated != null)
                {
                    var live = GameTypes.EnvironmentUI_Instantiated.GetValue(null, null) as bool?;
                    if (live != true) return null;
                }

                var environmentUI = GameTypes.EnvironmentUI_Instance.GetValue(null, null);
                if (environmentUI == null) return null;

                return GameTypes.Environment_Current.GetValue(environmentUI) as Component;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The backdrop's camera, found the way the game finds it -- EnvironmentUIRoot's own
        /// SetCameraActive does GetComponentInChildren&lt;Camera&gt;() on the same transform.
        /// </summary>
        private static Camera BackdropCamera(Component root)
        {
            if (GameTypes.EnvRoot_CameraContainer == null) return null;

            var container = GameTypes.EnvRoot_CameraContainer.GetValue(root) as Transform;
            return container == null ? null : container.GetComponentInChildren<Camera>();
        }

        /// <summary>
        /// Which of a map's pictures the staging area uses. Stable for a map so the place does not
        /// change between raids, and shared with the pre-warm so both agree on what to decode.
        /// </summary>
        internal static int PictureIndex(string locationId, int count)
        {
            if (count <= 0) return 0;
            return Mathf.Abs((locationId ?? string.Empty).GetHashCode()) % count;
        }

        /// <summary>
        /// The screen's own shape, for the rare case where the camera will not say. Measured, not
        /// assumed: a hard-coded 16:9 would be wrong on every other monitor, which is the whole
        /// thing this is here to avoid.
        /// </summary>
        private static float SafeAspect()
        {
            if (Screen.width > 0 && Screen.height > 0) return (float)Screen.width / Screen.height;
            return ScreenFit.StockAspect;
        }

        /// <summary>
        /// The map's own picture, cover-cropped to the shape of the **camera's** frustum.
        ///
        /// Not the screen's shape, which is the subtle one: the plane is built to fill the
        /// backdrop camera's frustum, and a UI Image stretches its sprite to the RectTransform it
        /// is on. So a sprite cropped to the screen and shown on a plane shaped by the camera is
        /// stretched by exactly the ratio between them. They are usually the same -- but not when
        /// the camera has a viewport rect or renders to a RenderTexture, and the whole point of
        /// taking the aspect from the camera is that we do not have to know which.
        ///
        /// Everything else -- which size of the picture, the crop, the caching, the freeing -- is
        /// the art pipeline that already exists, and it is measured rather than calculated, so it
        /// is right on any shape of screen.
        /// </summary>
        private static Sprite MapSprite(string locationId, float aspect)
        {
            var images = BannerArt.For(locationId);
            if (images == null || images.Count == 0) return null;
            if (aspect <= 0.01f) return null;

            // A whole screen wants the sharp one, so ask as though the banner filled it. Height is
            // the stable side: on an ultrawide the extra pixels are width, and asking for a
            // 5120-wide source would reject every sensible screenshot.
            var height = Screen.height > 0 ? Screen.height : 1080;
            var fit = new BannerFit { Width = Mathf.RoundToInt(height * aspect), Height = height };
            if (fit.Width <= 0 || fit.Height <= 0) return null;

            // Filling a whole screen is a much harder ask than filling a banner, and hardest of
            // all on a wide one: a 16:9 screenshot cover-cropped to 32:9 keeps its width and
            // throws away most of its height, so it arrives with far fewer pixels than the frame
            // wants. Say so once per file per screen, rather than letting it look soft silently.
            BannerArt.WarnIfSoft(images, fit);

            // One picture per raid, chosen by the map, so the place is stable while you look at it.
            return images[PictureIndex(locationId, images.Count)].Sprite(fit, true);
        }

        /// <summary>
        /// How much bigger than the frame the planes have to be built so that the drift can never
        /// reveal an edge, whatever the shape of the screen.
        ///
        /// A fixed number cannot do this. The plane fills the frustum at rest, and then the camera
        /// moves: sideways by up to <c>drift</c>, vertically by <c>0.55 x drift</c>, and backwards
        /// by up to <c>0.8 x drift</c> -- and moving backwards is the dangerous one, because the
        /// frustum at the plane's depth grows while the plane does not.
        ///
        /// The shape matters because the excursion is in world units and the frame is not square.
        /// The plane's height is <c>2 D tan(fov/2)</c> and its width is that times the aspect, so a
        /// sideways drift is a smaller fraction of an ultrawide frame and a much larger fraction of
        /// a 4:3 or portrait one. Both axes are worked out and the larger requirement wins.
        ///
        /// Pure arithmetic, no Unity: this is the part that can be checked without the game.
        /// </summary>
        internal static float RequiredOverscan(float distance, float fov, float aspect, float drift, float sway)
        {
            if (distance <= 0.01f || fov <= 1f || fov >= 179f || aspect <= 0.01f) return 1f;

            drift = Mathf.Max(0f, drift);
            sway = Mathf.Max(0f, sway);

            // Amplitudes straight out of SceneDepth.DriveCamera.
            var dx = drift;              // (0.7 + 0.3) x drift
            var dy = drift * 0.55f;      // (0.6 + 0.4) x drift x 0.55
            var dz = drift * 0.8f;

            var halfTan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            if (halfTan <= 0.0001f) return 1f;

            // Worst case is the camera pulled back: the frustum at the plane's depth is widest
            // there, and the plane does not grow with it.
            var pulled = distance + dz;
            var growth = pulled / distance;

            // Half the frame at the plane, in world units. The horizontal half also carries the
            // aspect, which is the whole reason a fixed number cannot be right: the same sideways
            // excursion is a small fraction of an ultrawide frame and a large one of a portrait.
            var halfHeight = distance * halfTan;
            var halfWidth = halfHeight * aspect;

            // Rotation about the horizontal axis shifts the frame vertically, about the vertical
            // axis horizontally. Both scale with the distance, and the horizontal one is divided
            // by the *horizontal* half-extent -- getting that wrong is what under-sized the plane
            // on a portrait screen.
            var shiftY = dy + pulled * Mathf.Tan(sway * 0.6f * Mathf.Deg2Rad);
            var shiftX = dx + pulled * Mathf.Tan(sway * Mathf.Deg2Rad);

            var vertical = growth + shiftY / halfHeight;
            var horizontal = growth + shiftX / halfWidth;

            return Mathf.Max(vertical, horizontal);
        }

        // ------------------------------------------------------------ the two planes

        private void BuildPlanes(Component root, Camera camera, float aspect, Sprite sprite, Grade grade)
        {
            var near = Mathf.Max(0.05f, DeployScreenPlugin.StagingDistance.Value);
            var far = near * DepthRatio;

            var fov = camera.orthographic ? 60f : camera.fieldOfView;
            var drift = DeployScreenPlugin.DepthEnabled.Value ? DeployScreenPlugin.DepthDrift.Value : 0f;
            var sway = DeployScreenPlugin.DepthEnabled.Value ? DeployScreenPlugin.DepthSway.Value : 0f;

            // The configured value is a floor, not the answer. What the drift actually needs
            // depends on the distance, the field of view and the shape of the screen, and a number
            // that is right on 16:9 at the default drift is not right on 4:3 at twice the drift.
            // Each plane is worked out separately -- the near one needs more, because the same
            // excursion is a larger fraction of a smaller frustum.
            var floor = Mathf.Max(1f, DeployScreenPlugin.StagingOverscan.Value);
            var nearOverscan = Mathf.Max(floor, RequiredOverscan(near, fov, aspect, drift, sway));
            var farOverscan = Mathf.Max(floor, RequiredOverscan(far, fov, aspect, drift, sway));

            if (nearOverscan > floor + 0.001f || farOverscan > floor + 0.001f)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] overscan raised for this screen ("
                    + aspect.ToString("0.00") + ":1, drift " + drift.ToString("0.000")
                    + "): near x" + nearOverscan.ToString("0.00")
                    + ", map x" + farOverscan.ToString("0.00"));
            }

            // Overscan is not free: the picture is stretched across the whole oversized plane, so
            // at rest you see only the middle 1/overscan of it. Past about half again, that is a
            // visible crop and a visible loss of sharpness, and the cause is always the same --
            // a lot of drift over a short distance.
            if (farOverscan > 1.5f)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] the drift needs the map built x" + farOverscan.ToString("0.00")
                    + " oversize, so only the middle " + Mathf.RoundToInt(100f / farOverscan)
                    + "% of your picture is on screen at rest. Lower 'Camera drift' or raise "
                    + "'Near plane distance' to see more of it.");
            }

            // Far: the map itself, washed toward the destination's light.
            var wash = Color.Lerp(Color.white, grade.Wash,
                Mathf.Clamp01(DeployScreenPlugin.StagingGradeStrength.Value));

            BuildPlane(root, camera, aspect, "DeployScreen Map", far, farOverscan, sprite, wash, -100);

            // Near: a soft dark frame. Its only job is to be at a different depth from the map, so
            // that drifting the camera shears the two against each other -- which is the parallax
            // a single plane cannot give you.
            if (DeployScreenPlugin.StagingVignette.Value > 0f)
            {
                var vignette = VignetteSprite();
                if (vignette != null)
                {
                    var strength = Mathf.Clamp01(DeployScreenPlugin.StagingVignette.Value);
                    BuildPlane(root, camera, aspect, "DeployScreen Haze", near, nearOverscan, vignette,
                        new Color(0.02f, 0.025f, 0.03f, strength), -90);
                }
            }
        }

        /// <summary>
        /// A world-space canvas sized to fill the camera's frustum at a given distance, placed in
        /// the scene rather than under the camera -- if it were parented to the camera it would
        /// travel with the drift and never parallax at all.
        /// </summary>
        private void BuildPlane(Component root, Camera camera, float aspect, string name,
            float distance, float overscan, Sprite sprite, Color colour, int order)
        {
            float height;
            if (camera.orthographic)
            {
                height = camera.orthographicSize * 2f;
            }
            else
            {
                height = 2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            }

            var width = height * aspect;

            height *= overscan;
            width *= overscan;

            var go = new GameObject(name, typeof(RectTransform));
            _created.Add(go);

            var rect = (RectTransform)go.transform;
            rect.SetParent(root.transform, false);
            rect.position = camera.transform.position + camera.transform.forward * distance;
            rect.rotation = camera.transform.rotation;
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one;

            go.layer = VisibleLayer(camera, root);
            SetLayerRecursively(go, go.layer);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.sortingOrder = order;

            var image = go.AddComponent(GameTypes.BackgroundImage);

            if (GameTypes.Background_Sprite != null) GameTypes.Background_Sprite.SetValue(image, sprite, null);
            if (GameTypes.Background_Color != null) GameTypes.Background_Color.SetValue(image, colour, null);
            if (GameTypes.Background_Raycast != null) GameTypes.Background_Raycast.SetValue(image, false, null);

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] plane " + name + ": " + width.ToString("0.00") + "x"
                + height.ToString("0.00") + "u at " + distance.ToString("0.00") + "u on layer "
                + go.layer + " (" + LayerMask.LayerToName(go.layer) + "), camera '" + camera.name
                + "' mask 0x" + camera.cullingMask.ToString("X8") + ", clip "
                + camera.nearClipPlane.ToString("0.00") + "-" + camera.farClipPlane.ToString("0.0")
                + ", sprite " + (sprite == null || sprite.texture == null ? "none"
                    : sprite.texture.width + "x" + sprite.texture.height)
                + ", parent " + Describe(root.gameObject));

            // The layer has to be set again: AddComponent does not change it, but a Canvas added
            // to a fresh GameObject can re-parent nothing, so this is belt and braces for the
            // children an Image may create.
            SetLayerRecursively(go, go.layer);
        }

        /// <summary>
        /// A layer this camera actually draws. The container's own layer if the camera renders it,
        /// otherwise the lowest layer in its culling mask -- a plane on a layer the camera culls
        /// would simply never appear, with nothing in the log to say why.
        /// </summary>
        private static int VisibleLayer(Camera camera, Component root)
        {
            var mask = camera.cullingMask;

            if (root != null && (mask & (1 << root.gameObject.layer)) != 0) return root.gameObject.layer;
            if ((mask & (1 << camera.gameObject.layer)) != 0) return camera.gameObject.layer;

            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0) return i;
            }

            return root != null ? root.gameObject.layer : 0;
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursively(child.gameObject, layer);
        }

        /// <summary>
        /// A soft dark frame, generated rather than shipped. Transparent through the middle so the
        /// map reads clean, darkening toward the corners.
        /// </summary>
        private Sprite VignetteSprite()
        {
            const int Size = 128;

            _vignette = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            _vignette.wrapMode = TextureWrapMode.Clamp;
            _vignette.filterMode = FilterMode.Bilinear;

            var pixels = new Color32[Size * Size];

            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    // Distance from centre, 0 in the middle and 1 at the edge midpoints.
                    var dx = (x / (float)(Size - 1)) * 2f - 1f;
                    var dy = (y / (float)(Size - 1)) * 2f - 1f;

                    // Squared falloff biased outward: nothing for most of the frame, then a roll.
                    var d = Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f;
                    var a = Mathf.Clamp01((d - 0.55f) / 0.45f);
                    a = a * a * (3f - 2f * a);

                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }

            _vignette.SetPixels32(pixels);

            // No CPU copy kept; FullRect sprites do not need the pixels back.
            _vignette.Apply(false, true);

            return Sprite.Create(
                _vignette,
                new Rect(0, 0, Size, Size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
        }

        // ------------------------------------------------------- what gets out of the way

        /// <summary>
        /// The menu scene's own furniture. With the map hung behind it, the mall shutters or the
        /// factory crates are someone else's place sitting in front of the one you are going to.
        /// </summary>
        private void HideSceneFurniture(Component root)
        {
            if (!DeployScreenPlugin.StagingHideMenuScene.Value) return;

            var before = _hidden.Count;

            if (GameTypes.EnvRoot_MainScreenObjects == null)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] menu scene: this build has no MainScreenObjects field");
            }
            else
            {
                var array = GameTypes.EnvRoot_MainScreenObjects.GetValue(root) as Array;

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] menu scene: MainScreenObjects "
                    + (array == null ? "is null" : "has " + array.Length + " entries"));

                if (array != null)
                {
                    foreach (var entry in array) Hide(entry as GameObject);
                }
            }

            if (_hidden.Count > before) return;

            // The game's own list came to nothing on this build -- it is empty by the time the
            // deploy screen runs -- and the art hangs deep in the backdrop, so anything of the
            // menu room still standing nearer than the map plane is simply in front of it. That
            // is not a missing feature, it is the whole picture: the art renders perfectly and
            // is never seen. So fall back to the scene's own children.
            HideWhatOccludes(root);
        }

        /// <summary>
        /// Switches off the parts of the live backdrop scene that stand between the camera and the
        /// art, and only those. A child with nothing to draw cannot occlude anything, and neither
        /// can one further away than the map plane -- the lights in particular have to stay, since
        /// the grade is applied through them.
        /// </summary>
        private void HideWhatOccludes(Component root)
        {
            if (_camera == null) return;

            var far = Mathf.Max(0.05f, DeployScreenPlugin.StagingDistance.Value) * DepthRatio;
            var eye = _camera.transform.position;
            var forward = _camera.transform.forward;

            foreach (Transform child in root.transform)
            {
                var go = child.gameObject;
                if (!go.activeSelf || _created.Contains(go)) continue;

                var renderers = go.GetComponentsInChildren<Renderer>(false);
                if (renderers.Length == 0) continue;

                // Depth along the camera's own axis, measured to the nearest point of what the
                // object actually draws rather than to its transform, which can sit anywhere.
                var nearest = float.MaxValue;
                foreach (var renderer in renderers)
                {
                    var point = renderer.bounds.ClosestPoint(eye);
                    nearest = Mathf.Min(nearest, Vector3.Dot(point - eye, forward));
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] menu scene: " + Describe(go) + " draws " + renderers.Length
                    + " renderer(s), nearest " + nearest.ToString("0.00") + "u"
                    + (nearest < far ? " -- in front of the art at " + far.ToString("0.00") + "u" : " -- behind the art"));

                if (nearest < far) Hide(go);
            }
        }

        /// <summary>
        /// The banner panel. Its pictures are now the world, so the panel showing them again in a
        /// small frame is the thing this design exists to remove.
        /// </summary>
        private void HideBanners()
        {
            if (_screen == null || GameTypes.Loading_Banners == null) return;

            var panel = GameTypes.Loading_Banners.GetValue(_screen) as Component;
            if (panel != null) Hide(panel.gameObject);
        }

        private void Hide(GameObject target)
        {
            if (target == null || !target.activeSelf) return;

            // Never switch off the screen itself or anything it hangs from.
            if (_screen != null)
            {
                if (target == _screen.gameObject) return;
                if (_screen.transform.IsChildOf(target.transform)) return;
            }

            // Nor anything the staging area is standing on. An entry in MainScreenObjects can
            // be an ancestor of the backdrop camera, of the planes, or of the character, and
            // switching one of those off takes the whole composite down with it -- silently,
            // because the character's animator then throws from inside the game's own Dispose
            // on teardown, where nothing here can catch it.
            if (Keeps(target, _camera == null ? null : _camera.transform, "the backdrop camera")) return;
            if (Keeps(target, _planeRoot, "the art planes")) return;
            if (Keeps(target, _playerModel, "the character")) return;

            DeployScreenPlugin.Log.LogInfo("[DeployScreen] menu scene: hiding " + Describe(target));

            target.SetActive(false);
            _hidden.Add(target);
        }

        /// <summary>
        /// Whether <paramref name="target"/> is an ancestor of something staging needs. Said
        /// once per object so a list of what the game put in MainScreenObjects ends up in the
        /// log, where the next person can read it without a decompiler.
        /// </summary>
        /// <summary>
        /// The planes are built once and never touched again, so if the art stops being on screen
        /// mid-load, something outside this mod took it: a backdrop scene reload, a parent being
        /// switched off, a destroy. Reports the first time each state changes, with the time and
        /// the object responsible, so that "it flashed" becomes something that can be read back.
        /// </summary>
        private void WatchArt(double now)
        {
            if (_created.Count == 0 || now < _watchNext) return;

            _watchNext = now + 0.25;

            var plane = _created[0];
            string state;

            if (plane == null)
            {
                state = "destroyed";
            }
            else if (plane.transform.parent == null)
            {
                state = "orphaned";
            }
            else if (!plane.activeInHierarchy)
            {
                // Which ancestor was switched off is the whole answer, so walk up to the first
                // one that is not active rather than reporting the plane and stopping there.
                var culprit = plane.transform;
                while (culprit != null && culprit.gameObject.activeSelf) culprit = culprit.parent;
                state = culprit == null ? "inactive" : "inactive -- " + Describe(culprit.gameObject) + " was switched off";
            }
            else
            {
                state = "up";
            }

            if (_camera == null) state += "; camera destroyed";
            else if (!_camera.isActiveAndEnabled) state += "; camera '" + _camera.name + "' disabled";

            if (state == _artState) return;

            var was = _artState;
            _artState = state;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] art " + state + " at " + now.ToString("0.00") + "s"
                + (was == null ? " (first look)" : " (was " + was + ")"));
        }

        private static bool Keeps(GameObject target, Transform needed, string what)
        {
            if (needed == null || target == null) return false;
            if (!needed.IsChildOf(target.transform)) return false;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] menu scene: left " + Describe(target) + " alone -- it holds "
                + what + ".");
            return true;
        }

        /// <summary>A path, not a name: two objects called 'Root' are not the same object.</summary>
        private static string Describe(GameObject target)
        {
            if (target == null) return "'(null)'";

            var path = target.name;
            for (var t = target.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return "'" + path + "'";
        }

        /// <summary>The character on the deploy screen, so it is never hidden out from under the game.</summary>
        private static Transform PlayerModelTransform(Component screen)
        {
            if (screen == null || GameTypes.Loading_PlayerModel == null) return null;

            try
            {
                var view = GameTypes.Loading_PlayerModel.GetValue(screen) as Component;
                return view == null ? null : view.transform;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ intel

        /// <summary>
        /// Intel goes in the line under the map name rather than on a banner caption.
        ///
        /// _subCaption is borrowed rather than replaced: building a TextMeshPro object would mean
        /// referencing TMPro and guessing a font, a size and a position, and the field is already
        /// the right one in the right place. Nothing in MatchmakerTimeHasCome writes it -- both
        /// ChangeStatus and UpdateStatusText write _deployingText -- so it can be set and put back.
        /// </summary>
        private void ShowIntel(object location, object session)
        {
            if (!DeployScreenPlugin.StagingIntelLine.Value) return;
            if (!GameTypes.IntelReady || location == null) return;
            if (_screen == null || GameTypes.Loading_SubCaption == null) return;

            var cards = Intel.Build(location, session);
            if (cards == null || cards.Count == 0) return;

            _subCaption = GameTypes.Loading_SubCaption.GetValue(_screen) as Component;
            if (_subCaption == null) return;

            // The concrete type is a TextMeshProUGUI; "text" is inherited and public. Resolved off
            // the live object so this assembly still needs no TMPro reference.
            _subCaptionText = _subCaption.GetType().GetProperty("text");
            if (_subCaptionText == null || !_subCaptionText.CanWrite)
            {
                _subCaptionText = null;
                return;
            }

            _subCaptionWas = _subCaptionText.GetValue(_subCaption, null) as string;
            _cards = cards;
            _card = -1;
            _nextCard = 0;
        }

        internal void Tick(double now)
        {
            WatchArt(now);

            if (_subCaptionText == null || _cards == null || _cards.Count == 0) return;
            if (now < _nextCard) return;

            _nextCard = now + Mathf.Max(3f, DeployScreenPlugin.StagingIntelSeconds.Value);
            _card = (_card + 1) % _cards.Count;

            try
            {
                var card = _cards[_card];

                // Raw text is correct here, unlike on a banner caption. The caption trap is
                // SelectBanner's -- it hides a description that localizes to itself -- and this
                // writes the TextMeshPro text directly, so there is no lookup to fall foul of.
                var line = string.IsNullOrEmpty(card.Header)
                    ? card.Body
                    : card.Header + "   " + card.Body;

                _subCaptionText.SetValue(_subCaption, line ?? string.Empty, null);
            }
            catch (Exception error)
            {
                WarnOnce(error);
                _subCaptionText = null;
            }
        }

        // ---------------------------------------------------------------- restore

        /// <summary>
        /// Everything built is destroyed and everything hidden comes back. Safe to call twice and
        /// safe to call when Begin never got anywhere.
        /// </summary>
        internal void Restore()
        {
            _built = false;

            try { _grade.Restore(); }
            catch (Exception error) { WarnOnce(error); }

            foreach (var go in _created)
            {
                try { if (go != null) UnityEngine.Object.Destroy(go); }
                catch (Exception error) { WarnOnce(error); }
            }
            _created.Clear();

            foreach (var go in _hidden)
            {
                try { if (go != null) go.SetActive(true); }
                catch (Exception error) { WarnOnce(error); }
            }
            _hidden.Clear();

            try
            {
                if (_subCaptionText != null && _subCaption != null)
                {
                    _subCaptionText.SetValue(_subCaption, _subCaptionWas ?? string.Empty, null);
                }
            }
            catch (Exception error) { WarnOnce(error); }

            _subCaption = null;
            _subCaptionText = null;
            _subCaptionWas = null;
            _cards = null;

            try { if (_vignette != null) UnityEngine.Object.Destroy(_vignette); }
            catch (Exception error) { WarnOnce(error); }

            _vignette = null;
            _screen = null;
        }

        private void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] staging area failed: " + error);
        }
    }
}
