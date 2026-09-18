using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
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

        /// <summary>
        /// The frame's height in world units at the near plane -- the depth the haze hangs at, and
        /// the depth the character is treated as standing at when the drift moves him.
        ///
        /// This is the number that turns a camera move into a distance on screen: something at
        /// that depth shifts by <c>move / CharacterFrameHeight</c> of the frame. Zero when no art
        /// is up, which is how SceneDepth knows there is nothing to be parallaxed against.
        /// </summary>
        internal static float CharacterFrameHeight;

        private readonly MapGrade _grade = new MapGrade();

        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<float> _planeDistance = new List<float>();
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
        private float _builtFov, _builtAspect;
        private readonly List<object> _prisms = new List<object>();
        private readonly List<object> _shadowOwners = new List<object>();
        private readonly List<Behaviour> _switchedOff = new List<Behaviour>();
        private readonly List<object> _shadowWas = new List<object>();
        private readonly List<object> _prismWas = new List<object>();
        private bool _fitReported;
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

                _builtFov = camera.orthographic ? 60f : camera.fieldOfView;
                _builtAspect = aspect;

                // A camera that renders to less than the whole screen would leave a border no
                // plane can fill, so say what it is rather than assuming it is the default.
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] backdrop camera: fov=" + _builtFov.ToString("0.0")
                    + " aspect=" + aspect.ToString("0.000")
                    + " viewport=" + camera.rect
                    + " pixels=" + camera.pixelRect
                    + " screen=" + Screen.width + "x" + Screen.height
                    + " ortho=" + camera.orthographic);

                TakeCameraVignette(camera);
                TakeBackdropOcclusion(camera);


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

                // The game lights the character for a menu, not for this picture. Same grade,
                // same day -- and its shadows off, since the only thing they fall on is the
                // preview's own catcher, which over a photograph is a halo behind the PMC.
                TakeCastShadow(screen);

                _grade.TakeCharacterRig(screen, grade,
                    DeployScreenPlugin.StagingGradeStrength.Value);
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

            // What a camera move is worth on screen at the near plane. Measured from the camera
            // that is actually rendering this, not assumed, for the same reason the planes are.
            CharacterFrameHeight = 2f * near * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

            // Kept for the probe: what draws the backdrop, and how far away the art is.
            _backdropCamera = camera;
            _artDistance = far;

            // Far: the map itself, washed toward the destination's light.
            var wash = Color.Lerp(Color.white, grade.Wash,
                Mathf.Clamp01(DeployScreenPlugin.StagingGradeStrength.Value));

            BuildPlane(root, camera, aspect, "DeployScreen Map", far, farOverscan, sprite, wash, -100);

            // What the picture is doing in the corners the writing goes in. Measured here
            // because this is the one place that knows both the picture and the wash that will
            // be laid over it, and the screen layout -- which builds the scrims -- runs after.
            ArtTone.Measure(sprite, wash);

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
            _planeDistance.Add(distance);

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
                    // Starts at 0.70 rather than 0.55 so the darkening is a corner falloff
                    // and not a frame. The old number was chosen when the overscan floor was
                    // 1.12 and hid a tenth of this sprite off-screen; at 1.02 that margin is
                    // gone and the same ramp puts 24% haze along every edge, which reads as a
                    // black border around the picture. At 0.70 the edge midpoints sit at zero
                    // and only the corners take any.
                    var a = Mathf.Clamp01((d - 0.70f) / 0.45f);
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

            KeepFit();

            if (state == _artState) return;

            var was = _artState;
            _artState = state;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] art " + state + " at " + now.ToString("0.00") + "s"
                + (was == null ? " (first look)" : " (was " + was + ")"));
        }

        /// <summary>
        /// One-shot inventory of the deploy screen's own hierarchy: what is on it, where it sits,
        /// how it is anchored and what it says. Written because the layout cannot be rearranged
        /// without knowing which object holds the location name, which the timer, and whether
        /// they are anchored to a corner or stretched -- and none of that is knowable from
        /// outside the running game.
        ///
        /// Once per session, four levels deep: enough to find everything that matters on this
        /// screen without filling the log with a menu's worth of nesting.
        /// </summary>
        private static bool _dumped;
        private static bool _countdownDumped;
        private static double _countdownNext;

        internal static void DumpScreen(Component screen)
        {
            if (_dumped || screen == null) return;
            if (!DeployScreenPlugin.ReportLayout.Value) return;

            _dumped = true;

            try
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] --- deploy screen layout, from " + Describe(screen.gameObject) + " ---");

                DumpInto(screen.transform, 0);

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] --- end of layout ---");

                // The deploy screen is not the only screen in this flow. The countdown that
                // replaces it is a different object with a different name, and nothing here
                // touches it, which is why it stays stock. List the siblings so it can be
                // named rather than hunted for.
                var parent = screen.transform.parent;

                if (parent != null)
                {
                    var siblings = new StringBuilder();

                    foreach (Transform child in parent)
                    {
                        if (siblings.Length > 0) siblings.Append(", ");
                        siblings.Append(child.name);
                        if (!child.gameObject.activeSelf) siblings.Append(" [off]");
                    }

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] screens beside this one, under " + Describe(parent.gameObject)
                        + ": " + siblings);
                }

                DumpPreview(screen.transform);
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning("[DeployScreen] could not read the layout: " + error.Message);
            }
        }
        /// <summary>
        /// Keeps the planes covering the frame they were built for.
        ///
        /// They are sized once, from the camera's field of view and aspect at the moment the screen
        /// opens, with only the overscan the drift needs -- about 1.5% -- as margin. If the camera
        /// then changes its field of view or its aspect, the frustum grows past the plane and what
        /// shows around the edge is whatever is behind it: a dark border on all four sides. That is
        /// not a vignette, and softening one will not touch it.
        ///
        /// So the fit is checked rather than assumed, and corrected in place. Size only -- the
        /// planes are deliberately left where they were put, because the camera drifting across
        /// them at a fixed distance is the parallax.
        /// </summary>
        private void KeepFit()
        {
            if (_camera == null || _created.Count == 0) return;

            var fov = _camera.orthographic ? 60f : _camera.fieldOfView;
            var aspect = _camera.aspect > 0.01f ? _camera.aspect : _builtAspect;

            var fovDrift = Mathf.Abs(fov - _builtFov) / Mathf.Max(1f, _builtFov);
            var aspectDrift = Mathf.Abs(aspect - _builtAspect) / Mathf.Max(0.01f, _builtAspect);

            if (fovDrift < 0.002f && aspectDrift < 0.002f) return;

            if (!_fitReported)
            {
                _fitReported = true;

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] the frame moved under the art: fov " + _builtFov.ToString("0.0")
                    + " -> " + fov.ToString("0.0") + ", aspect " + _builtAspect.ToString("0.000")
                    + " -> " + aspect.ToString("0.000") + ", viewport " + _camera.rect
                    + " -- re-sizing the planes to match");
            }

            var drift = DeployScreenPlugin.DepthEnabled.Value ? DeployScreenPlugin.DepthDrift.Value : 0f;
            var sway = DeployScreenPlugin.DepthEnabled.Value ? DeployScreenPlugin.DepthSway.Value : 0f;
            var floor = Mathf.Max(1f, DeployScreenPlugin.StagingOverscan.Value);

            for (var i = 0; i < _created.Count && i < _planeDistance.Count; i++)
            {
                var plane = _created[i];
                if (plane == null) continue;

                var rect = plane.transform as RectTransform;
                if (rect == null) continue;

                var distance = _planeDistance[i];

                var height = _camera.orthographic
                    ? _camera.orthographicSize * 2f
                    : 2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

                var overscan = Mathf.Max(floor, RequiredOverscan(distance, fov, aspect, drift, sway));

                rect.sizeDelta = new Vector2(height * aspect * overscan, height * overscan);
            }

            _builtFov = fov;
            _builtAspect = aspect;
        }

        /// <summary>
        /// Stops the character preview casting a shadow onto nothing.
        ///
        /// The preview camera runs MaskAndShadow: a mask that cuts the character out of its own
        /// render, and a cast shadow that, in the stock menu, lands on the wall of the room the
        /// character is standing in. The staging area hides that room and hangs a photograph a
        /// long way behind instead, so the shadow has nothing to fall on and reads as a dark blob
        /// floating beside the PMC.
        ///
        /// Only the shadow half is touched, and only through fields whose names say they are the
        /// shadow -- switching the whole component off would take the mask with it, and the mask
        /// is what keeps the character from arriving in a grey box. Everything changed is recorded
        /// and put back. What was found is logged either way, because these are somebody else's
        /// fields and the next game version may rename them.
        /// </summary>
        private void TakeCastShadow(Component screen)
        {
            if (screen == null || !DeployScreenPlugin.StagingCastShadowOff.Value) return;
            if (GameTypes.Loading_PlayerModel == null) return;

            try
            {
                var view = GameTypes.Loading_PlayerModel.GetValue(screen) as Component;
                if (view == null) return;

                var seen = false;

                foreach (var camera in view.GetComponentsInChildren<Camera>(true))
                {
                    // The preview renders through its own PrismEffects, with its own vignette, and
                    // the image it produces covers most of the screen. Darkened edges on that are a
                    // frame over the art just as surely as the backdrop camera's were -- and it is
                    // the one that survived turning the other off.
                    TakeCameraVignette(camera);
                    TakePreviewKey(camera);
                    SimplifyPreview(camera);

                    foreach (var component in camera.GetComponents<Component>())
                    {
                        if (component == null || component.GetType().Name != "MaskAndShadow") continue;

                        var type = component.GetType();
                        var found = new StringBuilder();
                        var changed = 0;

                        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (!Plain(field.FieldType)) continue;

                            object was;
                            try { was = field.GetValue(component); } catch { continue; }

                            Pair(found, field.Name, was);

                            if (!field.Name.ToLowerInvariant().Contains("shadow")) continue;

                            // Vector2 and int are in the list now, and the shift is why. The
                            // player drew the outline of what is on screen: a blurred silhouette
                            // of him *and his rifle*, offset up and to the right. That is a drop
                            // shadow, and ShadowShift=(-0.03, -0.01) is the offset. Zeroing the
                            // strengths and leaving the shift alone was leaving the one field
                            // that decides whether the thing is visible beside him or hidden
                            // behind him.
                            object now = null;
                            if (field.FieldType == typeof(bool)) now = false;
                            else if (field.FieldType == typeof(float)) now = 0f;
                            else if (field.FieldType == typeof(int)) now = 0;
                            else if (field.FieldType == typeof(Vector2)) now = Vector2.zero;
                            else if (field.FieldType == typeof(Vector3)) now = Vector3.zero;
                            if (now == null) continue;

                            _shadowOwners.Add(component);
                            _shadowWas.Add(was);
                            _shadowNames.Add(field);

                            field.SetValue(component, now);
                            changed++;
                        }

                        // Worth saying, and this is the part that matters: SimplifyPreview runs a
                        // few lines above and logs when it switches this component off. It did
                        // not, which means the game already had it disabled -- so the dark shape
                        // behind the PMC is not this, and zeroing its fields was never going to
                        // remove it.
                        var effect = component as Behaviour;
                        var state = effect == null ? "not a behaviour"
                            : effect.enabled ? "enabled" : "already off";

                        DeployScreenPlugin.Log.LogInfo(
                            "[DeployScreen] MaskAndShadow on '" + camera.name + "': " + found
                            + " -- cleared " + changed + " shadow field(s), component " + state);

                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] no MaskAndShadow on the character preview; the blob is something else");
                }
            }
            catch (Exception error) { WarnOnce(error); }
        }

        private readonly List<FieldInfo> _shadowNames = new List<FieldInfo>();

        private void GiveBackCastShadow()
        {
            for (var i = _shadowNames.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_shadowNames[i] != null && _shadowOwners[i] != null)
                        _shadowNames[i].SetValue(_shadowOwners[i], _shadowWas[i]);
                }
                catch { }
            }

            _shadowOwners.Clear();
            _shadowWas.Clear();
            _shadowNames.Clear();
        }


        /// <summary>
        /// Switches off the menu camera's own vignette for as long as the art is up.
        ///
        /// This is the black frame. Not the near-haze plane, not the overscan, not the camera
        /// changing under the planes -- all three were chased and none of them were it. The menu
        /// camera runs PrismEffects with useVignette on and vignetteStrength at 1, darkening its
        /// own edges after everything else has been drawn. Over the stock backdrop, which is a dim
        /// scene, nobody has ever noticed. Over a photograph it is a black border on all four
        /// sides, and no amount of sizing a plane can reach it, because it is applied to the
        /// finished image.
        ///
        /// One field, put back on restore. Bloom, colour correction and the rest of the stack are
        /// left alone: they are what the menu is supposed to look like.
        /// </summary>
        private void TakeCameraVignette(Camera camera)
        {
            if (camera == null || !DeployScreenPlugin.StagingVignetteOff.Value) return;

            try
            {
                foreach (var component in camera.GetComponents<Component>())
                {
                    if (component == null || component.GetType().Name != "PrismEffects") continue;

                    var field = component.GetType().GetField("useVignette",
                        BindingFlags.Public | BindingFlags.Instance);

                    if (field == null || field.FieldType != typeof(bool)) return;

                    var was = field.GetValue(component);

                    _prisms.Add(component);
                    _prismWas.Add(was);

                    field.SetValue(component, false);

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] vignette off on '" + camera.name + "' (was " + was + ")");
                    return;
                }
            }
            catch (Exception error) { WarnOnce(error); }
        }

        private readonly List<Camera> _keyedCameras = new List<Camera>();
        private readonly List<Color> _keyedWas = new List<Color>();

        /// <summary>
        /// Stops the character being composited off an actual greenscreen.
        ///
        /// This is the halo, and the probe is what named it. The preview camera clears to
        /// <c>RGBA(1.000, 0.000, 1.000, 0.000)</c> -- **magenta**, at zero alpha -- into a
        /// 2734x2464 render texture, and the RawImage lays that over the art afterwards. Zero
        /// alpha means the background contributes nothing where it is left alone. The trouble is
        /// that nothing leaves it alone: every post-processing pass on that camera reads
        /// neighbouring pixels and writes colour without regard for alpha, so each one drags
        /// magenta inwards across the silhouette and drags the character outwards into the
        /// magenta. What comes back is a band of part-transparent, magenta-contaminated pixels
        /// following the character's outline -- a halo the shape of him, bright against a dark
        /// map and washed-out against a light one, which is exactly what was reported twice.
        ///
        /// It is a chroma key, so the player's own words for it were right.
        ///
        /// The fix is not to fight the passes, it is to key against nothing: clear to the same
        /// transparent, but black. Bleed from a black background is a faint dark edge instead of
        /// a coloured glow, and an edge a pixel or two wide is what a cut-out is supposed to
        /// have. One property, recorded and put back.
        ///
        /// Ruled out on the way here, each by something in the log rather than by argument: the
        /// cast shadow (MaskAndShadow is disabled by the game before this mod sees it), ambient
        /// occlusion (we switch it off and say so), every light that can see the preview layer
        /// (all report shadows=None), bloom (useBloom=False already), and any surface inside the
        /// preview to catch a shadow -- all 174 renderers the probe found are the character and
        /// his kit.
        /// </summary>
        private void TakePreviewKey(Camera camera)
        {
            if (camera == null || !DeployScreenPlugin.StagingClearPreview.Value) return;

            try
            {
                if (camera.clearFlags != CameraClearFlags.SolidColor) return;

                var was = camera.backgroundColor;

                // Only a coloured key, and only a transparent one. A camera clearing to something
                // opaque is drawing a background on purpose and is none of our business, and one
                // already clearing to black has nothing to contaminate anything with.
                if (was.a > 0.001f) return;
                if (was.r < 0.02f && was.g < 0.02f && was.b < 0.02f) return;

                _keyedCameras.Add(camera);
                _keyedWas.Add(was);

                camera.backgroundColor = new Color(0f, 0f, 0f, 0f);

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] preview key on '" + camera.name + "': was " + was
                    + ", cleared to transparent black");
            }
            catch (Exception error) { WarnOnce(error); }
        }

        private void GiveBackPreviewKey()
        {
            for (var i = _keyedCameras.Count - 1; i >= 0; i--)
            {
                try { if (_keyedCameras[i] != null) _keyedCameras[i].backgroundColor = _keyedWas[i]; }
                catch { }
            }

            _keyedCameras.Clear();
            _keyedWas.Clear();
        }

        /// <summary>
        /// Switches off ambient occlusion on the camera that draws the backdrop.
        ///
        /// This file has known for a long time why AO is wrong here, and says so about the
        /// preview camera a few lines down: it darkens where it believes geometry meets geometry,
        /// and against a silhouette with nothing behind it what it finds to darken is the air
        /// beside the silhouette. That reasoning was applied to the preview camera and never to
        /// this one, which is the camera the photograph is drawn by -- and this one has it on.
        ///
        /// It is a screen-space effect, so what it darkens is whatever is in the frame when it
        /// runs, and it follows that thing when the thing moves. The player turned the character
        /// with the mouse and the dark shape turned with him, which is the observation that put
        /// this camera in the frame at all: a shape that tracks his rotation is a shape derived
        /// from his silhouette, and by then the preview render had been ruled out as the place it
        /// could be coming from.
        ///
        /// Recorded and restored like everything else, and held down by KeepPreviewQuiet, so a
        /// menu that switches it back on partway through the load does not win.
        /// </summary>
        private void TakeBackdropOcclusion(Camera camera)
        {
            if (camera == null || !DeployScreenPlugin.StagingBackdropAo.Value) return;

            try
            {
                foreach (var component in camera.GetComponents<Component>())
                {
                    if (component == null || component.GetType().Name != "AmbientOcclusion") continue;

                    var behaviour = component as Behaviour;
                    if (behaviour == null) continue;

                    _switchedOff.Add(behaviour);
                    _switchedOffWas.Add(behaviour.enabled);

                    if (!behaviour.enabled) continue;

                    behaviour.enabled = false;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] ambient occlusion off on the backdrop camera " + camera.name);
                }
            }
            catch (Exception error) { WarnOnce(error); }
        }

        /// <summary>
        /// Switches off the two passes on the preview camera that draw darkness around the
        /// character rather than on it.
        ///
        /// Ambient occlusion darkens where it believes geometry meets geometry. With the character
        /// alone against a transparent background, what it finds to darken is the air beside his
        /// silhouette. The shadow catcher draws his cast shadow onto a surface that, once the menu
        /// room is hidden, is not there. Both are right for the stock menu and wrong here, and
        /// zeroing the shadow's own strength fields did not remove what is on screen -- which is
        /// what says it is not only the shadow.
        ///
        /// Components rather than fields this time, because the fields did not do it. Their
        /// enabled state is recorded and restored, and nothing else on the camera is touched.
        /// </summary>
        private void SimplifyPreview(Camera camera)
        {
            if (camera == null || !DeployScreenPlugin.StagingSimplePreview.Value) return;

            try
            {
                foreach (var component in camera.GetComponents<Component>())
                {
                    if (component == null) continue;

                    // The preview's image is a RawImage covering most of the screen, and the
                    // whole of it -- including the transparent area around the character -- goes
                    // through this stack. Post-processing a transparent background tints it, and
                    // the edge of that texture is a rectangle inset from the screen: the border.
                    // The art itself was measured covering every edge by 30px, so what is left is
                    // what is drawn on top of it.
                    var name = component.GetType().Name;
                    if (!Simplified(name)) continue;

                    var behaviour = component as Behaviour;
                    if (behaviour == null) continue;

                    // Recorded even when it is already off, and that is the point. Skipping those
                    // meant a component the game switches back on once the character finishes
                    // loading was never in the list and so was never held down. MaskAndShadow is
                    // exactly that case: off at screen-show, and nothing here has ever looked at
                    // it again.
                    _switchedOff.Add(behaviour);
                    _switchedOffWas.Add(behaviour.enabled);

                    if (!behaviour.enabled) continue;

                    behaviour.enabled = false;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] " + name + " off on " + camera.name);
                }
            }
            catch (Exception error) { WarnOnce(error); }
        }

        /// <summary>
        /// The effects on the preview camera that are switched off while the art is up.
        ///
        /// Two of them always: ambient occlusion darkens the air beside a silhouette when there
        /// is no geometry for it to find, and MaskAndShadow draws a cast shadow onto a surface
        /// that is not there once the menu room is hidden.
        ///
        /// The rest only when asked. The preview is a render with a transparent surround, and a
        /// post stack does not know that: bloom bleeds a lit character outwards into the
        /// transparency as a soft light halo, and grading and aberration tint what should be
        /// nothing at all. Over a dim room nobody sees it. Over a photograph it is the halo that
        /// makes the PMC look cut out and pasted on.
        ///
        /// Off by default because it also changes how the character himself looks -- these are
        /// the effects BSG lights him for -- and that is a trade only the person looking at it
        /// can make. Everything is restored either way.
        /// </summary>
        private static bool Simplified(string name)
        {
            if (name == "AmbientOcclusion" || name == "MaskAndShadow") return true;
            if (!DeployScreenPlugin.StagingPlainPreview.Value) return false;

            // Antialiasing is in the list because a post-process AA pass is a neighbour-sampling
            // blur by another name, and neighbour-sampling is what drags the key colour along the
            // character's outline in the first place.
            return name == "PrismEffects" || name == "Bloom" || name == "DesaturateEffect"
                || name == "ChromaticAberration" || name == "CameraMotionBlur"
                || name == "Antialiasing";
        }

        private readonly List<bool> _switchedOffWas = new List<bool>();

        private void GiveBackPreview()
        {
            for (var i = _switchedOff.Count - 1; i >= 0; i--)
            {
                try { if (_switchedOff[i] != null) _switchedOff[i].enabled = _switchedOffWas[i]; }
                catch { }
            }

            _switchedOff.Clear();
            _switchedOffWas.Clear();
        }

        private readonly HashSet<string> _cameBack = new HashSet<string>();

        /// <summary>
        /// Holds the preview quiet, rather than switching it off once and walking away.
        ///
        /// This file already knows better and says so in ScreenLayout: "the game sets these again
        /// when it changes state, and whatever writes last wins -- re-assert, do not set". That
        /// lesson was learned for the screen furniture and never applied to the camera, and the
        /// camera is the one with an asynchronous character arriving in the middle of it.
        ///
        /// Cheap enough to do every frame: an enabled check per component and a float compare per
        /// field, over the handful of each that were recorded. It says so once per thing that
        /// comes back, because a log line per frame would be worse than the bug.
        /// </summary>
        internal void KeepPreviewQuiet()
        {
            for (var i = 0; i < _switchedOff.Count; i++)
            {
                var one = _switchedOff[i];
                if (one == null || !one.enabled) continue;

                one.enabled = false;
                CameBack(one.GetType().Name);
            }

            for (var i = 0; i < _shadowNames.Count; i++)
            {
                var field = _shadowNames[i];
                var owner = _shadowOwners[i];
                if (field == null || owner == null) continue;

                try
                {
                    var now = field.GetValue(owner);
                    if (now == null || IsZero(now)) continue;

                    field.SetValue(owner, Zero(field.FieldType));
                    CameBack(field.Name);
                }
                catch { }
            }
        }

        private static bool IsZero(object value)
        {
            if (value is bool) return !(bool)value;
            if (value is float) return Mathf.Abs((float)value) < 0.0001f;
            if (value is int) return (int)value == 0;
            if (value is Vector2) return ((Vector2)value).sqrMagnitude < 0.000001f;
            if (value is Vector3) return ((Vector3)value).sqrMagnitude < 0.000001f;

            return true;
        }

        private static object Zero(Type type)
        {
            if (type == typeof(bool)) return false;
            if (type == typeof(float)) return 0f;
            if (type == typeof(int)) return 0;
            if (type == typeof(Vector2)) return Vector2.zero;
            if (type == typeof(Vector3)) return Vector3.zero;

            return null;
        }

        private void CameBack(string what)
        {
            if (!_cameBack.Add(what)) return;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] the game switched " + what + " back on partway through the load, "
                + "holding it off");
        }

        private void GiveBackCameraVignette()
        {
            for (var i = _prisms.Count - 1; i >= 0; i--)
            {
                try
                {
                    var field = _prisms[i].GetType().GetField("useVignette",
                        BindingFlags.Public | BindingFlags.Instance);

                    if (field != null) field.SetValue(_prisms[i], _prismWas[i]);
                }
                catch { }
            }

            _prisms.Clear();
            _prismWas.Clear();
        }
        private static bool Plain(Type type)
        {
            return type == typeof(bool) || type == typeof(int) || type == typeof(float)
                || type == typeof(Vector2) || type == typeof(Rect) || type.IsEnum;
        }

        private static void Pair(StringBuilder line, string name, object value)
        {
            if (line.Length > 0) line.Append(", ");
            line.Append(name).Append('=').Append(value);
        }
        private static void DumpPreview(Transform root)
        {
            var view = root.Find("PlayerModelView");
            if (view == null) return;

            DeployScreenPlugin.Log.LogInfo("[DeployScreen] --- character preview ---");

            foreach (var camera in view.GetComponentsInChildren<Camera>(true))
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] preview camera '" + camera.name + "' clear=" + camera.clearFlags
                    + " bg=" + camera.backgroundColor + " mask=0x" + camera.cullingMask.ToString("X8")
                    + " depth=" + camera.depth
                    + " target=" + (camera.targetTexture == null
                        ? "screen"
                        : camera.targetTexture.width + "x" + camera.targetTexture.height));
            }

            DumpInto(view, 0);

            DeployScreenPlugin.Log.LogInfo("[DeployScreen] --- end of character preview ---");
        }

        private static Camera _backdropCamera;
        private static float _artDistance;

        private static int _darknessReports;
        private static double _darknessAt = -1;

        /// <summary>
        /// When the darkness probe samples, in seconds after the screen opens.
        ///
        /// Twice, because one sample cannot tell a thing that is off from a thing that is off
        /// *yet*. The character arrives asynchronously and the game may turn its own effects back
        /// on when it does -- which is exactly the mistake the shadow fix made in the other
        /// direction -- so the second sample is late enough to have missed nothing.
        /// </summary>
        private static readonly double[] DarknessAt = { 4.0, 12.0 };

        /// <summary>
        /// Runs the darkness probe once, a few seconds in.
        ///
        /// Not from DumpScreen with the rest of the dump: ShowPlayerModel is async, so at
        /// screen-show the preview is an empty rig -- no character, no renderers, and a probe
        /// that lists what is inside it would list nothing and look like an answer.
        /// </summary>
        internal static void WatchForPreview(Component screen, double now)
        {
            if (screen == null || _darknessReports >= DarknessAt.Length) return;
            if (!DeployScreenPlugin.ReportLayout.Value) return;

            if (_darknessAt < 0) _darknessAt = now;
            if (now - _darknessAt < DarknessAt[_darknessReports]) return;

            var at = DarknessAt[_darknessReports];
            _darknessReports++;

            var view = screen.transform.Find("PlayerModelView");
            if (view == null) return;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] --- what is darkening the preview, at " + at.ToString("0") + "s ---");
            ReportPreviewDarkness(view);
            ReportBackdrop();

            // Once, not at every sample: the press listener would otherwise be added twice and
            // report one click as two.
            if (_darknessReports == 1) ReportBackButton(screen);

            DeployScreenPlugin.Log.LogInfo("[DeployScreen] --- end of darkness ---");
        }

        /// <summary>
        /// What could be drawing the dark shape behind the PMC.
        ///
        /// It is not the cast shadow. MaskAndShadow is already disabled by the game before this
        /// mod touches it, its strength fields were zeroed anyway, every light under the preview
        /// has its shadows set to None, and the two lights this mod adds are created with None.
        /// The shape is still there, so it is something none of those explain, and three
        /// candidates are left: a surface inside the preview that the character darkens, a light
        /// from outside the preview that can nonetheless see its layer, or an image effect on the
        /// preview camera that nobody has named yet.
        ///
        /// So this lists all three rather than reasoning about them further. Once per session,
        /// behind Report the screen layout, and it is meant to be deleted the day it answers --
        /// see the probes in CLAUDE.md that already have.
        ///
        /// FindObjectsOfType is a scene-wide sweep and has no business on a timer. Once, behind a
        /// flag, on a screen that is already stalling, is the exception.
        /// </summary>
        private static void ReportPreviewDarkness(Transform view)
        {
            try
            {
                // 1. Anything with a surface inside the preview that is not the character. A
                // shadow catcher, a backdrop quad, a blob sprite: all of them are renderers,
                // whatever they are called.
                //
                // The character's own meshes are skipped, and they are recognised by their
                // shader rather than by their path. The first version of this filtered on
                // "under MenuPlayer", which was wrong in the one way that mattered: a shadow blob
                // hung off the player's rig is under MenuPlayer too, so the filter would have
                // hidden the very thing the probe exists to find. A skin, a rig or a rifle is
                // drawn with the game's p0/ shader family; a blob, a catcher or a decal is not.
                //
                // Anything that names itself a shadow is printed whatever it is drawn with.
                var surfaces = 0;
                var skipped = 0;

                foreach (var renderer in view.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null) continue;

                    if (Character(renderer)) { skipped++; continue; }

                    surfaces++;

                    var shaders = new StringBuilder();

                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (shaders.Length > 0) shaders.Append(" + ");
                        shaders.Append(material == null ? "none"
                            : material.name + " (" + (material.shader == null ? "?" : material.shader.name) + ")");
                    }

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] preview surface " + Describe(renderer.gameObject)
                        + " on=" + renderer.enabled + "/" + renderer.gameObject.activeInHierarchy
                        + " layer=" + LayerMask.LayerToName(renderer.gameObject.layer)
                        + " casts=" + renderer.shadowCastingMode + " receives=" + renderer.receiveShadows
                        + " size=" + renderer.bounds.size
                        + " material=" + shaders);
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] preview surfaces: " + surfaces + " that are not the character, "
                    + skipped + " that are");

                ReportPreviewLayer(view);
                ReportShadowComponents(view);

                // A projector is how a blob shadow is usually done, it is not a Renderer, and so
                // nothing above would have found one.
                foreach (var component in view.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component.GetType().Name != "Projector") continue;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] preview projector " + Describe(component.gameObject));
                }

                // Lights inside the preview, with what they are set to now -- the shadows on these
                // are cleared by TakeCharacterRig, so anything here still casting is one it missed.
                foreach (var light in view.GetComponentsInChildren<Light>(true))
                {
                    if (light == null) continue;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] preview light " + Describe(light.gameObject)
                        + " type=" + light.type + " shadows=" + light.shadows
                        + " mask=0x" + light.cullingMask.ToString("X8")
                        + " on=" + light.enabled + "/" + light.gameObject.activeInHierarchy);
                }

                // 2. The preview's own components, which DumpInto does not print for the root
                // object, and every behaviour on each of its cameras: one of them is an image
                // effect and image effects are where unexplained darkness usually lives.
                var mine = new StringBuilder();

                foreach (var component in view.GetComponents<Component>())
                {
                    if (component == null) continue;

                    var behaviour = component as Behaviour;
                    Pair(mine, component.GetType().Name, behaviour == null ? "-" : behaviour.enabled.ToString());
                }

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] preview root components: " + mine);

                // The compositing path, which nothing has ever looked inside. The RawImage on
                // this object is what draws the preview's render texture onto the screen, and
                // CameraImage is the game's own component sitting beside it. If a drop shadow is
                // drawn at composite time rather than inside the render, this is where it lives --
                // and that would explain why disabling MaskAndShadow, a component that holds
                // shadow settings and nothing else, changes nothing on screen.
                foreach (var component in view.GetComponents<Component>())
                {
                    if (component == null) continue;

                    var name = component.GetType().Name;
                    if (name == "RectTransform" || name == "CanvasRenderer") continue;

                    ReportFieldsOn(component);
                    ReportMaterialsOn(component);
                }

                foreach (var camera in view.GetComponentsInChildren<Camera>(true))
                {
                    if (camera == null) continue;

                    var effects = new StringBuilder();

                    foreach (var component in camera.GetComponents<Component>())
                    {
                        if (component == null || component is Camera) continue;

                        var behaviour = component as Behaviour;
                        Pair(effects, component.GetType().Name, behaviour == null ? "-" : behaviour.enabled.ToString());
                    }

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] preview camera '" + camera.name + "' components: " + effects);

                    // The line that answered it. A camera clearing to a colour with no alpha is a
                    // chroma key, and every effect above it smears that colour along whatever it
                    // is keying out.
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] preview camera '" + camera.name + "' clears to "
                        + camera.clearFlags + " " + camera.backgroundColor
                        + " into " + (camera.targetTexture == null
                            ? "the screen"
                            : camera.targetTexture.width + "x" + camera.targetTexture.height
                              + " " + camera.targetTexture.format));

                    ReportFields(camera, "PrismEffects");

                    // The last two components on this camera nobody has opened. A name in a log
                    // is what every round of this has been short of, and these are the only two
                    // left unread.
                    ReportFields(camera, "Undithering");
                    ReportFields(camera, "LightSwitcherOverkill");

                    ReportPreviewEmptiness(camera);
                    ReportRenderTextureAuthors(camera);
                    ReportPreviewBisect(camera);
                    ReportRendererBisect(camera, view);
                    ReportAlphaProfile(camera);
                }

                // 3. Lights from anywhere that can see the preview's layer. The ones under the
                // preview are already accounted for; a light in the menu scene whose culling mask
                // happens to include WeaponPreview is not, and it would cast the character onto
                // whatever surface #1 turns up.
                if (GameTypes.Layers_WeaponPreview == null) return;

                var layer = Convert.ToInt32(GameTypes.Layers_WeaponPreview.GetValue(null));
                if (layer < 0 || layer > 31) return;

                var mask = 1 << layer;

                foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    if (light == null || (light.cullingMask & mask) == 0) continue;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] light on the preview layer: " + Describe(light.gameObject)
                        + " type=" + light.type + " shadows=" + light.shadows
                        + " intensity=" + light.intensity.ToString("0.00")
                        + " on=" + light.enabled + "/" + light.gameObject.activeInHierarchy);
                }
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not read what is darkening the preview: " + error.Message);
            }
        }

        /// <summary>
        /// Dumps the final countdown screen the first time it appears.
        ///
        /// The countdown is a different screen object -- 'Matchmaker Final Countdown', a sibling of
        /// the deploy screen -- which is why every layout change made so far leaves it stock:
        /// nothing here has ever looked at it. It cannot be dumped when the deploy screen opens,
        /// because it is inactive then and its layout has not been run, so its rects are empty.
        ///
        /// Polled rather than patched: a Harmony hook would need a name for a method on a type
        /// that is only known by its GameObject, and four times a second costs nothing next to
        /// what the game is doing on these frames.
        /// </summary>
        internal static void WatchForCountdown(Component screen, double now)
        {
            if (_countdownDumped || screen == null || now < _countdownNext) return;
            if (!DeployScreenPlugin.ReportLayout.Value) return;

            _countdownNext = now + 0.25;

            var parent = screen.transform.parent;
            if (parent == null) return;

            var countdown = parent.Find("Matchmaker Final Countdown");
            if (countdown == null || !countdown.gameObject.activeInHierarchy) return;

            _countdownDumped = true;

            try
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] --- final countdown layout, from " + Describe(countdown.gameObject) + " ---");

                DumpInto(countdown, 0);

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] --- end of final countdown ---");
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not read the countdown layout: " + error.Message);
            }
        }

        private static void DumpInto(Transform parent, int depth)
        {
            if (depth > 4) return;

            foreach (Transform child in parent)
            {
                var line = new StringBuilder();

                line.Append(' ', depth * 2).Append(child.name);
                if (!child.gameObject.activeSelf) line.Append(" [off]");

                var rect = child as RectTransform;
                if (rect != null)
                {
                    line.Append(" @").Append(rect.anchoredPosition.x.ToString("0"))
                        .Append(",").Append(rect.anchoredPosition.y.ToString("0"))
                        .Append(" ").Append(rect.rect.width.ToString("0"))
                        .Append("x").Append(rect.rect.height.ToString("0"))
                        .Append(" anc ").Append(rect.anchorMin.x.ToString("0.##"))
                        .Append(",").Append(rect.anchorMin.y.ToString("0.##"))
                        .Append("-").Append(rect.anchorMax.x.ToString("0.##"))
                        .Append(",").Append(rect.anchorMax.y.ToString("0.##"));
                }

                foreach (var component in child.GetComponents<Component>())
                {
                    if (component == null) continue;

                    var type = component.GetType();
                    if (type.Name == "RectTransform" || type.Name == "Transform"
                        || type.Name == "CanvasRenderer") continue;

                    line.Append(" | ").Append(type.Name);

                    // A near-full-screen image with alpha is a veil over everything behind
                    // it, and nothing but its colour tells it apart from a decoration.
                    var colour = type.GetProperty("color");
                    if (colour != null && colour.PropertyType == typeof(Color))
                    {
                        var drawn = (Color)colour.GetValue(component, null);
                        line.Append(" rgba ").Append(drawn.r.ToString("0.00"))
                            .Append(",").Append(drawn.g.ToString("0.00"))
                            .Append(",").Append(drawn.b.ToString("0.00"))
                            .Append(",").Append(drawn.a.ToString("0.00"));
                    }

                    // Reflection rather than a TextMeshPro reference: this plugin deliberately
                    // holds no reference to the game's UI assemblies.
                    var text = type.GetProperty("text");
                    if (text == null || text.PropertyType != typeof(string)) continue;

                    var value = text.GetValue(component, null) as string;
                    if (string.IsNullOrEmpty(value)) continue;

                    if (value.Length > 40) value = value.Substring(0, 40) + "...";
                    line.Append(" '").Append(value.Replace((char)10, ' ')).Append("'");

                    var size = type.GetProperty("fontSize");
                    if (size != null) line.Append(" ").Append(size.GetValue(component, null)).Append("pt");
                }

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] " + line);

                DumpInto(child, depth + 1);
            }
        }

        /// <summary>
        /// Everything the backdrop camera can see that is nearer than the art, from anywhere.
        ///
        /// This is the gap. HideWhatOccludes only ever looks at the **direct children of the
        /// environment root** -- one level, one subtree -- so anything drawing on the backdrop
        /// camera's layers from somewhere else in the scene has never been considered, never been
        /// logged, and never been hidden. On the run that prompted this, that loop printed exactly
        /// one object, which should have been a warning on its own: a menu room is not one object.
        ///
        /// The preview has now been eliminated as the source -- every surface in it accounted for,
        /// its effects off, and its render reading alpha 0 wherever the character is not -- so
        /// what is left is on this side, and this is the sweep that was never done.
        ///
        /// FindObjectsOfType across every loaded scene is exactly the thing never to put on a
        /// timer. Once, behind Report the screen layout, on a screen that is already stalling.
        /// </summary>
        private static void ReportBackdrop()
        {
            var camera = _backdropCamera;
            if (camera == null) return;

            try
            {
                var effects = new StringBuilder();

                foreach (var component in camera.GetComponents<Component>())
                {
                    if (component == null || component is Camera) continue;

                    var behaviour = component as Behaviour;
                    Pair(effects, component.GetType().Name, behaviour == null ? "-" : behaviour.enabled.ToString());
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] backdrop camera " + camera.name + " components: " + effects);

                var eye = camera.transform.position;
                var forward = camera.transform.forward;
                var mask = camera.cullingMask;

                var found = new List<KeyValuePair<float, string>>();

                foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (renderer == null || !renderer.enabled) continue;
                    if (!renderer.gameObject.activeInHierarchy) continue;
                    if ((mask & (1 << renderer.gameObject.layer)) == 0) continue;

                    var point = renderer.bounds.ClosestPoint(eye);
                    var depth = Vector3.Dot(point - eye, forward);

                    // Behind the art cannot occlude it, and behind the camera is not on screen.
                    if (depth > _artDistance || depth < -50f) continue;

                    var size = renderer.bounds.size;
                    if (size.x < 0.05f && size.y < 0.05f && size.z < 0.05f) continue;

                    found.Add(new KeyValuePair<float, string>(depth,
                        depth.ToString("0.00") + "u " + Describe(renderer.gameObject)
                        + " layer=" + LayerMask.LayerToName(renderer.gameObject.layer)
                        + " size=" + size
                        + " shader=" + (renderer.sharedMaterial == null || renderer.sharedMaterial.shader == null
                            ? "none" : renderer.sharedMaterial.shader.name)));
                }

                found.Sort((a, b) => a.Key.CompareTo(b.Key));

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] in front of the art at " + _artDistance.ToString("0.00")
                    + "u: " + found.Count + " renderer(s) the backdrop camera can see");

                for (var i = 0; i < found.Count && i < 30; i++)
                {
                    DeployScreenPlugin.Log.LogInfo("[DeployScreen]   " + found[i].Value);
                }
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not sweep the backdrop: " + error.Message);
            }
        }

        /// <summary>
        /// Everything the preview camera renders that is not under the preview.
        ///
        /// The third time this hunt has been lost to a search scoped too narrowly, and the same
        /// mistake each time: looking inside one subtree and concluding the thing is not there.
        /// A camera does not render a subtree. It renders **a layer**, from anywhere in any loaded
        /// scene -- and the preview camera's culling mask is a single layer, WeaponPreview. An
        /// object sitting on that layer somewhere else entirely is drawn into the preview exactly
        /// as if it were part of the character, and every sweep so far walked PlayerModelView and
        /// stopped.
        ///
        /// The alpha map is what forced this: a soft band four or five cells wide hugging the
        /// character's right side, all the way down, inside the render texture. Nothing in the
        /// preview's own subtree draws it, and it is still there with every post-processing pass
        /// on that camera switched off, so what draws it is geometry the sweep never looked at.
        ///
        /// The backdrop got this sweep two rounds ago and it was clean. The preview never did.
        /// </summary>
        private static void ReportPreviewLayer(Transform view)
        {
            try
            {
                Camera camera = null;

                foreach (var one in view.GetComponentsInChildren<Camera>(true))
                {
                    if (one != null) { camera = one; break; }
                }

                if (camera == null) return;

                var mask = camera.cullingMask;

                // Describe wraps a path in quotes, so the prefix to test against is the path
                // with its closing quote turned back into a separator. Comparing against the
                // quoted form is the bug this had: 'A/B' is not a prefix of 'A/B/C' -- the
                // quote sits where the slash goes -- so every one of the character's own 174
                // renderers would have been counted as coming from outside the preview, and the
                // one number this probe exists to print would have been nonsense.
                var self = Describe(view.gameObject);
                var mine = self.Substring(0, self.Length - 1) + "/";
                var found = 0;

                foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (renderer == null || !renderer.enabled) continue;
                    if (!renderer.gameObject.activeInHierarchy) continue;
                    if ((mask & (1 << renderer.gameObject.layer)) == 0) continue;

                    var path = Describe(renderer.gameObject);
                    if (path == self || path.StartsWith(mine, StringComparison.Ordinal)) continue;

                    found++;

                    if (found > 25) continue;

                    var shaders = new StringBuilder();

                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (shaders.Length > 0) shaders.Append(" + ");
                        shaders.Append(material == null || material.shader == null
                            ? "none" : material.name + " (" + material.shader.name + ")");
                    }

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] on the preview layer, outside the preview: " + path
                        + " layer=" + LayerMask.LayerToName(renderer.gameObject.layer)
                        + " size=" + renderer.bounds.size
                        + " material=" + shaders);
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] the preview camera renders " + found
                    + " renderer(s) from outside PlayerModelView on mask 0x" + mask.ToString("X8"));
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not sweep the preview layer: " + error.Message);
            }
        }

        /// <summary>
        /// Every camera in the game that writes into the preview's render texture.
        ///
        /// The fourth scope this hunt has been lost to, and the widest one yet. Every sweep in
        /// this file -- components, renderers, effects, fields -- starts at PlayerModelView and
        /// works down, and the one sweep that broadened, ReportPreviewLayer, broadened to the
        /// layer. None of them ever asked the question a render texture actually invites: a
        /// texture is a destination, and more than one camera can write to it.
        ///
        /// That would fit everything the alpha map shows and everything that has been ruled out.
        /// A second camera drawing the same character into the same target with a dark material
        /// and an offset produces exactly the band in the picture, and it is invisible to every
        /// probe run so far: the renderers it draws are the character's own renderers, so a
        /// renderer sweep sees nothing unusual; it is not post-processing, so switching the post
        /// stack off changes nothing; and it is not under PlayerModelView, so no component sweep
        /// ever reached it.
        ///
        /// FindObjectsOfTypeAll rather than FindObjectsOfType on purpose. A camera that renders
        /// on demand, by calling Render() itself rather than by being ticked, is disabled the
        /// rest of the time, and that is the ordinary way to drive exactly this kind of effect.
        /// FindObjectsOfType would skip it. The scene check drops prefabs and assets, which
        /// FindObjectsOfTypeAll also returns and which draw nothing.
        /// </summary>
        private static void ReportRenderTextureAuthors(Camera preview)
        {
            try
            {
                var target = preview.targetTexture;
                if (target == null) return;

                var found = 0;

                foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (camera == null || camera == preview) continue;
                    if (!camera.gameObject.scene.IsValid()) continue;
                    if (camera.targetTexture != target) continue;

                    found++;

                    var effects = new StringBuilder();

                    foreach (var component in camera.GetComponents<Component>())
                    {
                        if (component == null || component is Camera) continue;

                        var behaviour = component as Behaviour;
                        Pair(effects, component.GetType().Name, behaviour == null ? "-" : behaviour.enabled.ToString());
                    }

                    // depth and clearFlags are the two that say which way round the pass goes: a
                    // lower depth draws first and is therefore behind, and a camera that does not
                    // clear is adding to what is already in the target rather than replacing it.
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] also writes the preview render: " + Describe(camera.gameObject)
                        + " depth=" + camera.depth + " clears=" + camera.clearFlags + " " + camera.backgroundColor
                        + " mask=0x" + camera.cullingMask.ToString("X8")
                        + " on=" + camera.enabled + "/" + camera.gameObject.activeInHierarchy
                        + " components: " + (effects.Length == 0 ? "none" : effects.ToString()));
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] " + found + " other camera(s) write into the preview's "
                    + target.width + "x" + target.height + " " + target.format + " target");
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not sweep the preview's render texture: " + error.Message);
            }
        }

        /// <summary>
        /// Everything scene-wide that calls itself a shadow, and where it sits.
        ///
        /// The MaskAndShadow under the preview camera is not the one drawing the band. That is
        /// not a guess: TakeCastShadow finds it, logs its fields, and zeroes every field with
        /// "shadow" in the name -- ShadowShift, ShadowStrength, ShadowBlurIterations, all of
        /// them -- and the band survives. Yet the band in the picture is a blurred silhouette of
        /// the man and his rifle offset up and to the right, which is what that component's own
        /// numbers describe: a shift sampling left and down, four blur iterations, half strength.
        ///
        /// A mechanism that matches on an instance that provably is not it means there is another
        /// instance. TakeCastShadow only ever looks at cameras under PlayerModelView, so a second
        /// one anywhere else has never been in scope.
        ///
        /// Matched on the type name rather than a fixed list, because the point is to find the
        /// one that has not been named yet, and printed with the side of the preview boundary it
        /// falls on, because that is what is worth knowing at a glance.
        /// </summary>
        private static void ReportShadowComponents(Transform view)
        {
            try
            {
                var self = Describe(view.gameObject);
                var mine = self.Substring(0, self.Length - 1) + "/";
                var found = 0;

                foreach (var behaviour in Resources.FindObjectsOfTypeAll<Behaviour>())
                {
                    if (behaviour == null) continue;
                    if (!behaviour.gameObject.scene.IsValid()) continue;

                    var name = behaviour.GetType().Name;
                    if (name.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    found++;

                    if (found > 40) continue;

                    var path = Describe(behaviour.gameObject);
                    var side = path == self || path.StartsWith(mine, StringComparison.Ordinal)
                        ? " (inside the preview)" : " (outside)";

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] shadow component " + name + " on " + path + side
                        + " on=" + behaviour.enabled + "/" + behaviour.gameObject.activeInHierarchy);

                    // The fields as well for the type that matches the picture, so a second
                    // instance can be compared against the first without another raid.
                    if (name == "MaskAndShadow") ReportFieldsOn(behaviour);
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] " + found + " component(s) scene-wide with " + "'shadow'"
                    + " in the type name");
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not sweep for shadow components: " + error.Message);
            }
        }

        /// <summary>
        /// A picture of what the preview render contains, printed as text.
        ///
        /// The version of this that read five corners was worthless and worse than worthless: it
        /// answered alpha 0 at all five, which was true and meant nothing, because a shadow sits
        /// **beside** the character and the corners of a 5420x2464 texture are nowhere near him.
        /// Reading the empty parts of an image to find out whether it is empty is circular, and it
        /// cost a round trip. What broke the deadlock was the player noticing the shape turns when
        /// the character turns -- which says it is a silhouette of him, and a silhouette of him
        /// can only be in this texture.
        ///
        /// So this reads all of it. The render is downscaled on the GPU to something the size of a
        /// paragraph and printed as a map, alpha per cell. The character is whatever comes out
        /// solid; anything part-transparent spreading out from him is the thing being hunted, and
        /// its shape and its offset will be visible in the map at a glance.
        /// </summary>
        private static void ReportPreviewEmptiness(Camera camera)
        {
            var target = camera.targetTexture;
            if (target == null) return;

            var pixels = SamplePreview(target);
            if (pixels == null) return;

            try
            {

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] preview render, alpha map ("
                    + target.width + "x" + target.height
                    + "; # solid, * mostly, : half, . faint, space empty):");

                var partial = 0;
                var solid = 0;

                // Top row first: texture rows run bottom-up and a map printed that way is upside
                // down, which is exactly the kind of small confusion this is meant to remove.
                for (var y = MapTall - 1; y >= 0; y--)
                {
                    var row = new StringBuilder(MapWide);

                    for (var x = 0; x < MapWide; x++)
                    {
                        var a = pixels[y * MapWide + x].a / 255f;

                        if (a >= 0.90f) { row.Append('#'); solid++; }
                        else if (a >= 0.50f) { row.Append('*'); partial++; }
                        else if (a >= 0.15f) { row.Append(':'); partial++; }
                        else if (a >= 0.02f) { row.Append('.'); partial++; }
                        else row.Append(' ');
                    }

                    DeployScreenPlugin.Log.LogInfo("[DeployScreen] |" + row + "|");
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] preview render: " + solid + " solid cell(s), " + partial
                    + " part-transparent. Anything part-transparent spreading out from the solid "
                    + "shape is drawn into this texture, and is what is being hunted.");
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] could not print the preview render: " + error.Message);
            }
        }

        private const int MapWide = 78;
        private const int MapTall = 30;

        /// <summary>
        /// The preview render downscaled on the GPU to the size of a paragraph and read back.
        /// A kilobyte off the card rather than the fifty megabytes the full target would be, and
        /// the downscale is what makes a soft band show up as a run of cells rather than noise.
        /// </summary>
        private static Color32[] SamplePreview(RenderTexture target)
        {
            if (target == null) return null;

            var was = RenderTexture.active;
            RenderTexture small = null;
            Texture2D read = null;

            try
            {
                small = RenderTexture.GetTemporary(MapWide, MapTall, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(target, small);

                RenderTexture.active = small;
                read = new Texture2D(MapWide, MapTall, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0f, 0f, MapWide, MapTall), 0, 0, false);
                read.Apply(false, false);

                return read.GetPixels32();
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] could not read the preview render: " + error.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = was;
                if (small != null) RenderTexture.ReleaseTemporary(small);
                if (read != null) UnityEngine.Object.Destroy(read);
            }
        }

        /// <summary>The same sample as the map, counted instead of drawn.</summary>
        private static bool CountPreview(RenderTexture target, out int solid, out int partial)
        {
            solid = 0;
            partial = 0;

            var pixels = SamplePreview(target);
            if (pixels == null) return false;

            foreach (var pixel in pixels)
            {
                var a = pixel.a / 255f;

                if (a >= 0.90f) solid++;
                else if (a >= 0.02f) partial++;
            }

            return true;
        }

        /// <summary>
        /// Which effect on the preview camera draws the band, settled by removing them one at a
        /// time and measuring rather than by looking.
        ///
        /// Two things forced this. The first is that "it is not post-processing" was never true.
        /// The run that established it switched off Antialiasing, PrismEffects and
        /// AmbientOcclusion -- and Simplified(), the list that decides what goes off, has never
        /// contained Undithering or LightSwitcherOverkill, both of which sit on this camera and
        /// are both enabled. Two image effects were ruled out without ever being switched off.
        ///
        /// The second is that every verdict in this hunt has been a person looking at a
        /// screenshot and saying the shape was unchanged. The alpha map gives a number instead --
        /// how many cells are part-transparent -- and a number can be compared across variants
        /// within one raid without anyone having to decide what "unchanged" means.
        ///
        /// So: measure, then for each effect disable it, render the camera by hand, measure
        /// again, put it back. The effect whose removal collapses the part-transparent count is
        /// the one drawing the band. The last line disables all of them at once; if the count
        /// survives that, no effect is involved and the band is the character's own materials,
        /// which is the other half of the answer and costs nothing extra to get.
        ///
        /// camera.Render() is a full render into the camera's own target, image effects included,
        /// so each variant is measured the way the screen would have shown it. Every state goes
        /// back in a finally, and the camera is rendered once more afterwards, because this runs
        /// on a live screen with the player looking at it.
        /// </summary>
        private static void ReportPreviewBisect(Camera camera)
        {
            var target = camera.targetTexture;
            if (target == null) return;

            var effects = new List<Behaviour>();

            foreach (var component in camera.GetComponents<Component>())
            {
                if (component == null || component is Camera) continue;

                var behaviour = component as Behaviour;
                if (behaviour != null && behaviour.enabled) effects.Add(behaviour);
            }

            if (effects.Count == 0) return;

            var state = new bool[effects.Count];
            for (var i = 0; i < effects.Count; i++) state[i] = effects[i].enabled;

            try
            {
                int solid, partial;

                camera.Render();
                if (!CountPreview(target, out solid, out partial)) return;

                var baseline = partial;

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] bisect, everything on: " + solid + " solid, "
                    + partial + " part-transparent");

                foreach (var one in effects)
                {
                    one.enabled = false;
                    camera.Render();
                    var got = CountPreview(target, out solid, out partial);
                    one.enabled = true;

                    if (!got) continue;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] bisect, without " + one.GetType().Name + ": "
                        + solid + " solid, " + partial + " part-transparent ("
                        + (partial - baseline).ToString("+0;-0;0") + ")");
                }

                foreach (var one in effects) one.enabled = false;

                camera.Render();

                if (CountPreview(target, out solid, out partial))
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] bisect, without any effect: " + solid + " solid, "
                        + partial + " part-transparent ("
                        + (partial - baseline).ToString("+0;-0;0")
                        + "). Still near the baseline means the band is not an effect at all and"
                        + " the character's own materials are what is left.");
                }
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not bisect the preview effects: " + error.Message);
            }
            finally
            {
                for (var i = 0; i < effects.Count; i++)
                {
                    try { if (effects[i] != null) effects[i].enabled = state[i]; }
                    catch { }
                }

                try { camera.Render(); } catch { }
            }
        }

        /// <summary>
        /// Which of the character's own renderers put the band in the alpha, grouped by the shader
        /// they draw with.
        ///
        /// ReportPreviewBisect answered the question it was built for, and the answer was none of
        /// them: every effect on the preview camera off, one at a time and then all at once, moved
        /// the part-transparent count by at most one cell out of 135. So no image effect draws the
        /// band. Nothing on the layer draws into the target from outside, no second camera writes
        /// to it, and MaskAndShadow is dead. What is left is the geometry pass, which is the 69
        /// renderers that are the character.
        ///
        /// Grouped by shader rather than taken one at a time, and that is the whole point. If one
        /// renderer were responsible, hiding it would show as a large drop -- but a band that
        /// follows the entire silhouette is far more likely to be how a whole family of materials
        /// writes alpha into an ARGB32 target, and in that case each renderer on its own moves the
        /// count by a cell or two and nothing stands out of the noise. A shader group moves it by
        /// everything that family is responsible for at once.
        ///
        /// The last line is the one to read first. With every character renderer off the target
        /// should be empty; anything still part-transparent there is drawn by something that is
        /// not the character and was not found by any sweep so far.
        /// </summary>
        private static void ReportRendererBisect(Camera camera, Transform view)
        {
            var target = camera.targetTexture;
            if (target == null) return;

            var groups = new Dictionary<string, List<Renderer>>();
            var all = new List<Renderer>();

            foreach (var renderer in view.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!renderer.gameObject.activeInHierarchy) continue;

                var shader = "none";

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null) continue;
                    shader = material.shader.name;
                    break;
                }

                List<Renderer> group;
                if (!groups.TryGetValue(shader, out group)) groups[shader] = group = new List<Renderer>();

                group.Add(renderer);
                all.Add(renderer);
            }

            if (all.Count == 0) return;

            try
            {
                int solid, partial;

                camera.Render();
                if (!CountPreview(target, out solid, out partial)) return;

                var baseline = partial;

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] renderers, all " + all.Count + " on: " + solid + " solid, "
                    + partial + " part-transparent");

                foreach (var pair in groups)
                {
                    foreach (var renderer in pair.Value) renderer.enabled = false;

                    camera.Render();
                    var got = CountPreview(target, out solid, out partial);

                    foreach (var renderer in pair.Value) renderer.enabled = true;

                    if (!got) continue;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] renderers, without " + pair.Value.Count + "x " + pair.Key
                        + ": " + solid + " solid, " + partial + " part-transparent ("
                        + (partial - baseline).ToString("+0;-0;0") + ")");
                }

                foreach (var renderer in all) renderer.enabled = false;

                camera.Render();

                if (CountPreview(target, out solid, out partial))
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] renderers, without the character at all: " + solid
                        + " solid, " + partial + " part-transparent. Anything left here is drawn"
                        + " into the target by something that is not the character.");
                }
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not bisect the preview renderers: " + error.Message);
            }
            finally
            {
                foreach (var renderer in all)
                {
                    try { if (renderer != null) renderer.enabled = true; }
                    catch { }
                }

                try { camera.Render(); } catch { }
            }
        }

        /// <summary>
        /// Why Back does nothing, asked of the button rather than of the screen around it.
        ///
        /// Two raids, two different failures. With Rearrange the screen on, the click reached the
        /// game: the report ended cancel-requested, and what failed after that was the screen
        /// never closing. With it off, nothing fired at all -- no abort, no report, and the raid
        /// went ahead. Rearranging is therefore not what breaks the abort, and the question splits
        /// in two: does the click reach the button, and does the button's handler do anything.
        ///
        /// A listener on onClick answers the first half the moment it is pressed. The rest is
        /// everything that can eat a UI click without leaving a trace: the rect the button
        /// occupies in screen pixels, whether it is active and interactable, and every CanvasGroup
        /// above it -- one with blocksRaycasts off, or alpha at zero, anywhere up the chain, takes
        /// the click silently and leaves the button looking perfectly normal on screen. That is
        /// the classic cause of exactly this symptom and nothing has looked for it yet.
        ///
        /// Through reflection because this project references the engine's UIModule and not the
        /// game's UI library, which is the same rule the rest of the file keeps.
        /// </summary>
        private static void ReportBackButton(Component screen)
        {
            try
            {
                Transform button = null;

                foreach (var candidate in screen.GetComponentsInChildren<Transform>(true))
                {
                    if (candidate == null || candidate.name != "BackButton") continue;

                    button = candidate;
                    break;
                }

                if (button == null)
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] back: nothing called BackButton under the screen");
                    return;
                }

                var rect = button as RectTransform;

                if (rect != null)
                {
                    var corners = new Vector3[4];
                    rect.GetWorldCorners(corners);

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] back: " + Describe(button.gameObject)
                        + " active=" + button.gameObject.activeInHierarchy
                        + " corners " + corners[0] + " to " + corners[2]
                        + " on a " + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height
                        + " screen");
                }

                // Every component on the button, printed whatever it is. The first version of
                // this skipped any component without an "interactable" property, on the
                // assumption that a button is a Unity Button -- and this one is not. It is
                // DefaultUIButton, DefaultUIButtonAnimation and TweenAnimatedButton, none of
                // which matched, so the loop attached nothing and the raid it was built for
                // answered nothing. Print first, filter never.
                foreach (var component in button.GetComponents<Component>())
                {
                    if (component == null) continue;

                    var type = component.GetType();
                    var behaviour = component as Behaviour;
                    var extra = new StringBuilder();

                    var interactable = type.GetProperty("interactable");

                    if (interactable != null)
                    {
                        try { Pair(extra, "interactable", interactable.GetValue(component, null)); }
                        catch { }
                    }

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] back: component " + type.Name
                        + " enabled=" + (behaviour == null ? "-" : behaviour.enabled.ToString())
                        + (extra.Length == 0 ? "" : " " + extra));

                    ListenForPress(component, type);
                }

                for (var t = button; t != null; t = t.parent)
                {
                    var group = t.GetComponent<CanvasGroup>();
                    if (group == null) continue;

                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] back: CanvasGroup on " + Describe(t.gameObject)
                        + " alpha=" + group.alpha.ToString("0.00")
                        + " interactable=" + group.interactable
                        + " blocksRaycasts=" + group.blocksRaycasts);
                }
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not read the back button: " + error.Message);
            }
        }

        /// <summary>
        /// A line in the log the moment the button is actually pressed.
        ///
        /// Matched on the base type rather than on the name "onClick", which is what the first
        /// version did and why it found nothing. UnityEventBase lives in the engine's own
        /// CoreModule, which this project references, and every click event in every UI library
        /// derives from it -- Unity's Button, the game's DefaultUIButton, whatever a mod adds --
        /// whether it is called onClick, OnClick or something else entirely. Private fields are
        /// included because a game button usually keeps its event in one.
        /// </summary>
        private static void ListenForPress(Component button, Type type)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var property in type.GetProperties(Any))
            {
                if (!ClickEvent(property.PropertyType) || !property.CanRead) continue;
                if (property.GetIndexParameters().Length > 0) continue;

                object held = null;
                try { held = property.GetValue(button, null); } catch { }

                Listen(held, type.Name + "." + property.Name);
            }

            foreach (var field in type.GetFields(Any))
            {
                if (!ClickEvent(field.FieldType)) continue;

                object held = null;
                try { held = field.GetValue(button); } catch { }

                Listen(held, type.Name + "." + field.Name);
            }
        }

        private static bool ClickEvent(Type type)
        {
            return type != null && typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(type);
        }

        /// <summary>
        /// AddListener is looked up with the no-argument UnityAction overload specifically. A
        /// UnityEvent that carries a value has a different one, this finds nothing for it, and
        /// skipping it is right: a click is the event with no argument.
        /// </summary>
        private static void Listen(object raised, string what)
        {
            if (raised == null) return;

            try
            {
                var add = raised.GetType().GetMethod(
                    "AddListener", new[] { typeof(UnityEngine.Events.UnityAction) });

                if (add == null) return;

                add.Invoke(raised, new object[]
                {
                    new UnityEngine.Events.UnityAction(() => DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] back: " + what + " fired"))
                });

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] back: listening on " + what);
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] back: could not listen on " + what + ": " + error.Message);
            }
        }

        /// <summary>
        /// How the alpha actually falls off beside the character, read at full resolution.
        ///
        /// Everything in this hunt rests on the alpha map, and the alpha map is a 78x30 downscale
        /// of a 5420x2464 render made with Graphics.Blit. That is a 69x minification, and Blit
        /// samples with the source texture's own filterMode -- so if this target carries mipmaps,
        /// or is filtered trilinear, the "soft band four or five cells wide" could be the
        /// downscale spreading the silhouette rather than anything in the render at all. Four
        /// sessions have been spent on a shape that has only ever been seen through that lens,
        /// and no probe has checked whether the lens is flat.
        ///
        /// So: one row of the target, at native width, straight off the card. Find the row with
        /// the most solid cells in the coarse map, find the outermost solid pixel on it, and print
        /// the alpha at fixed distances outward from there. A silhouette with nothing beside it
        /// drops from 255 to 0 within a pixel or two. A real halo holds a middling value for
        /// hundreds of pixels, and the band is about 280 pixels wide in this target, so the two
        /// cases are not close.
        ///
        /// Both sides are printed because the band is asymmetric in the map -- four cells right,
        /// one cell left -- and if that asymmetry is real it is a strong clue on its own, while if
        /// both sides come back identical the asymmetry was the measurement.
        /// </summary>
        private static void ReportAlphaProfile(Camera camera)
        {
            var target = camera.targetTexture;
            if (target == null) return;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] preview target: " + target.width + "x" + target.height
                + " " + target.format + " mips=" + target.useMipMap
                + " filter=" + target.filterMode + " aa=" + target.antiAliasing);

            var coarse = SamplePreview(target);
            if (coarse == null) return;

            var best = -1;
            var most = 0;

            for (var y = 0; y < MapTall; y++)
            {
                var count = 0;

                for (var x = 0; x < MapWide; x++)
                {
                    if (coarse[y * MapWide + x].a >= 230) count++;
                }

                if (count <= most) continue;

                most = count;
                best = y;
            }

            if (best < 0)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] alpha profile: nothing solid in the render to measure from");
                return;
            }

            var row = (int)((best + 0.5f) * target.height / MapTall);
            if (row < 0) row = 0;
            if (row > target.height - 1) row = target.height - 1;

            var was = RenderTexture.active;
            Texture2D line = null;

            try
            {
                RenderTexture.active = target;
                line = new Texture2D(target.width, 1, TextureFormat.RGBA32, false);
                line.ReadPixels(new Rect(0f, row, target.width, 1f), 0, 0, false);
                line.Apply(false, false);

                var pixels = line.GetPixels32();

                var left = -1;
                var right = -1;

                for (var x = 0; x < pixels.Length; x++)
                {
                    if (pixels[x].a < 230) continue;

                    if (left < 0) left = x;
                    right = x;
                }

                if (left < 0)
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] alpha profile: row " + row + " has no solid pixel");
                    return;
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] alpha profile, row " + row + " of " + target.height
                    + ": solid from x=" + left + " to x=" + right
                    + " (the map said this row was the widest)");

                var steps = new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512 };
                var out_ = new StringBuilder();
                var back = new StringBuilder();

                foreach (var step in steps)
                {
                    var rx = right + step;
                    var lx = left - step;

                    Pair(out_, "+" + step, rx < pixels.Length ? pixels[rx].a.ToString() : "-");
                    Pair(back, "-" + step, lx >= 0 ? pixels[lx].a.ToString() : "-");
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] alpha right of the silhouette, 0-255: " + out_);
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] alpha left of the silhouette, 0-255: " + back);
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] could not read the alpha profile: " + error.Message);
            }
            finally
            {
                RenderTexture.active = was;
                if (line != null) UnityEngine.Object.Destroy(line);
            }
        }

        /// <summary>
        /// Whether a renderer is a piece of the character rather than something drawn around him.
        ///
        /// By shader family. Every skin, rig and weapon part the probe has ever printed is drawn
        /// with p0/ -- the game's own lit shader -- and a blob, a catcher, a decal or a projector
        /// quad is drawn with something else. Anything naming itself a shadow is never skipped,
        /// whatever it is drawn with, because that is the thing being looked for.
        /// </summary>
        private static bool Character(Renderer renderer)
        {
            if (renderer.name.ToLowerInvariant().Contains("shadow")) return false;

            var any = false;

            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || material.shader == null) continue;
                if (!material.shader.name.StartsWith("p0/", StringComparison.OrdinalIgnoreCase)) return false;

                any = true;
            }

            return any;
        }

        /// <summary>
        /// Every plain public field on one named component of a camera.
        /// </summary>
        private static void ReportFields(Camera camera, string typeName)
        {
            foreach (var component in camera.GetComponents<Component>())
            {
                if (component == null || component.GetType().Name != typeName) continue;

                ReportFieldsOn(component);
                return;
            }
        }

        /// <summary>The same, for a component already in hand, and its properties as well.</summary>
        private static void ReportFieldsOn(Component component)
        {
            var line = new StringBuilder();

            foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!Plain(field.FieldType)) continue;

                try { Pair(line, field.Name, field.GetValue(component)); }
                catch { }
            }

            // Properties too, and this is where it matters: a UI Graphic keeps almost everything
            // it is asked about behind a property, so a fields-only dump of a RawImage says
            // nothing at all about it.
            foreach (var property in component.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!Plain(property.PropertyType) || !property.CanRead) continue;
                if (property.GetIndexParameters().Length > 0) continue;

                try { Pair(line, property.Name, property.GetValue(component, null)); }
                catch { }
            }

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] " + component.GetType().Name + " on "
                + Describe(component.gameObject) + ": "
                + (line.Length == 0 ? "nothing plain" : line.ToString()));
        }

        /// <summary>
        /// Whatever material a component draws with, and every property on its shader.
        ///
        /// A drop shadow drawn at composite time is a shader drawing it, and a shader that draws
        /// one has properties that say so. Reading the whole list rather than guessing at names is
        /// the point: a name is what has been missing every time this has been chased.
        /// </summary>
        private static void ReportMaterialsOn(Component component)
        {
            var names = new[] { "material", "materialForRendering", "defaultMaterial" };

            foreach (var name in names)
            {
                var property = component.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanRead) continue;

                Material material;
                try { material = property.GetValue(component, null) as Material; }
                catch { continue; }

                if (material == null || material.shader == null) continue;

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen]   " + component.GetType().Name + "." + name + " = "
                    + material.name + ", shader " + material.shader.name
                    + ", keywords [" + string.Join(", ", material.shaderKeywords) + "]");

                ReportShaderProperties(material);
            }
        }

        private static void ReportShaderProperties(Material material)
        {
            try
            {
                var shader = material.shader;
                var type = typeof(Shader);

                var count = type.GetMethod("GetPropertyCount", new[] { typeof(Shader) });
                var named = type.GetMethod("GetPropertyName", new[] { typeof(Shader), typeof(int) });

                if (count == null || named == null)
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen]   this Unity cannot list shader properties");
                    return;
                }

                var total = (int)count.Invoke(null, new object[] { shader });
                var line = new StringBuilder();

                for (var i = 0; i < total; i++)
                {
                    var one = named.Invoke(null, new object[] { shader, i }) as string;
                    if (one == null || !material.HasProperty(one)) continue;

                    object value = null;

                    try
                    {
                        // Colour first: a shadow is far more likely to be one than a number, and
                        // reading a colour property as a float gives nothing useful.
                        value = one.ToLowerInvariant().Contains("color")
                            ? (object)material.GetColor(one)
                            : material.GetFloat(one);
                    }
                    catch { }

                    Pair(line, one, value);
                }

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen]   properties: " + (line.Length == 0 ? "none" : line.ToString()));
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen]   shader properties unreadable: " + error.Message);
            }
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
            KeepPreviewQuiet();

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
                // The header in the mod's own brass, the body in the text's own colour, so the
                // line reads as a label and a value rather than as a sentence. Rich text is
                // switched on for this field when the layout takes it; TMP prints the tags
                // literally otherwise, which is why it is not assumed here.
                var line = string.IsNullOrEmpty(card.Header)
                    ? card.Body
                    : "<color=#C8A45C>" + card.Header + "</color>   " + card.Body;

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

            // The reading belongs to the picture that is going, not to the next one.
            ArtTone.Forget();
            _cameBack.Clear();
            _backdropCamera = null;
            CharacterFrameHeight = 0f;

            // First: it belongs to the camera rather than to anything built here, and the menu
            // is entitled to its own look the moment this screen is done with it.
            GiveBackCameraVignette();
            GiveBackPreviewKey();
            GiveBackCastShadow();
            GiveBackPreview();

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
