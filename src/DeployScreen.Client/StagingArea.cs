using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

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

        /// <summary>
        /// The deploy screen's parent, taken at Begin and kept.
        ///
        /// It cannot be read at BeginFade: by then the screen has gone and the Component compares
        /// null, so asking for its parent there returned nothing and the sibling watch armed with
        /// zero screens to watch -- `watching=0` in the trace, which is also why the queue
        /// indicator was never found.
        /// </summary>
        private Transform _screensParent;

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

            try { _screensParent = screen != null ? screen.transform.parent : null; }
            catch { _screensParent = null; }

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
        /// <summary>
        /// Which of a map's pictures this raid gets, advancing one each time that map is loaded.
        ///
        /// It used to be <c>abs(locationId.GetHashCode()) % count</c>, which is stable in exactly
        /// the wrong sense: stable within a raid, which was the point, and stable across every
        /// raid ever loaded, which was not. Ten pictures a map shipped and nine of them were
        /// unreachable, because a map's name does not change between loads.
        ///
        /// So it rotates instead, and rotates rather than randomising on purpose: random repeats,
        /// and a picture shown twice in three raids reads as the bug that was just fixed. Going
        /// round in order means every picture is seen once before any is seen twice.
        ///
        /// The cursor is kept in the config next to the measured sizes, so it survives a restart
        /// and the first raid of a session is not always picture one. Losing it is harmless: every
        /// map simply starts from the beginning again.
        /// </summary>
        internal static int PictureIndex(string locationId, int count)
        {
            if (count <= 0) return 0;

            var key = (locationId ?? string.Empty).ToLowerInvariant();

            // Chosen once per raid. Begin runs on a fresh StagingArea each time, but MapSprite is
            // not the only thing that may ask, and the answer has to be the same all the way
            // through one screen or the art would change under the player.
            int already;
            if (_pictureThisRaid.TryGetValue(key, out already)) return already % count;

            var seen = SeenPictures();
            var next = NextPicture(seen, key, count);

            seen[key] = next;
            _pictureThisRaid[key] = next;
            RememberSeenPictures(seen);

            return next;
        }

        /// <summary>Cleared when a raid ends, so the next one moves on.</summary>
        internal static void ForgetPictureChoices()
        {
            _pictureThisRaid.Clear();
        }

        private static readonly Dictionary<string, int> _pictureThisRaid = new Dictionary<string, int>();

        /// <summary>
        /// The cursor per map, read back from the config. Malformed entries are dropped rather
        /// than thrown on: this is written by us and read by us, but a player may still open it.
        /// </summary>
        private static Dictionary<string, int> SeenPictures()
        {
            try
            {
                return ReadSeen(DeployScreenPlugin.SeenPictures == null
                    ? string.Empty
                    : DeployScreenPlugin.SeenPictures.Value);
            }
            catch { return new Dictionary<string, int>(); }
        }

        private static void RememberSeenPictures(Dictionary<string, int> seen)
        {
            try
            {
                if (DeployScreenPlugin.SeenPictures == null) return;
                DeployScreenPlugin.SeenPictures.Value = WriteSeen(seen);
            }
            catch { }
        }

        /// <summary>
        /// The cursor per map, parsed from the setting. Malformed entries are dropped rather than
        /// thrown on: this is written by us and read by us, but a player may still open the file
        /// and a half-edited line should cost one map its place, not the whole rotation.
        ///
        /// Kept free of the config so it can be checked without the game, the same way the
        /// remembered banner sizes are.
        /// </summary>
        internal static Dictionary<string, int> ReadSeen(string raw)
        {
            var seen = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(raw)) return seen;

            // char[] on purpose -- Split(';') binds to Split(char, StringSplitOptions), which the
            // reference assemblies have and this runtime does not. Same trap as ScreenFit.Remember.
            foreach (var entry in raw.Split(new[] { ';' }))
            {
                var trimmed = entry.Trim();
                if (trimmed.Length == 0) continue;

                var split = trimmed.IndexOf('=');
                if (split <= 0 || split >= trimmed.Length - 1) continue;

                int value;
                if (!int.TryParse(trimmed.Substring(split + 1).Trim(), out value)) continue;
                if (value < 0) continue;

                seen[trimmed.Substring(0, split).Trim().ToLowerInvariant()] = value;
            }

            return seen;
        }

        /// <summary>The same table written back out. Round-trips through ReadSeen unchanged.</summary>
        internal static string WriteSeen(Dictionary<string, int> seen)
        {
            if (seen == null) return string.Empty;

            var builder = new System.Text.StringBuilder();

            foreach (var pair in seen)
            {
                if (pair.Key == null || pair.Key.Length == 0) continue;
                if (builder.Length > 0) builder.Append(';');
                builder.Append(pair.Key).Append('=').Append(pair.Value);
            }

            return builder.ToString();
        }

        /// <summary>
        /// The next place in a map's rotation, given where it got to last time. A map not seen
        /// before starts at its first picture; everything else moves on one and wraps.
        /// </summary>
        internal static int NextPicture(Dictionary<string, int> seen, string key, int count)
        {
            if (count <= 0) return 0;
            if (seen == null) return 0;

            int last;
            if (!seen.TryGetValue((key ?? string.Empty).ToLowerInvariant(), out last)) return 0;

            // A shrunken folder must not park the cursor past the end for ever.
            return ((last % count) + 1) % count;
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

            // The handle the fade pulls on. A CanvasGroup is free while its alpha is 1 and is the
            // only thing here that can take the art down without touching the image, the sprite or
            // the shader -- which matters, because the art plane is a world-space Canvas precisely
            // so that no shader has to be guessed at.
            _planeFades.Add(go.AddComponent<CanvasGroup>());

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.sortingOrder = order;

            var image = go.AddComponent(GameTypes.BackgroundImage);

            _planeImages.Add(image);
            _planeColours.Add(colour);

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
        ///
        /// True while the screen is up, and only while it is up. Once the abort hands the camera
        /// back to the main menu it is re-posed wholesale, and art left where it was put is art
        /// True throughout, as it turns out: two aborts measured the camera moving 0.06m and 0.2
        /// degrees across a whole hold, so nothing here is what takes the art off the screen. The
        /// abort is handled by RaiseArtToOverlay instead, which leaves these planes alone.
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
                    TakeCommandBuffers(camera);

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

            GiveBackCommandBuffers();
        }

        private readonly List<Camera> _bufferCameras = new List<Camera>();
        private readonly List<CameraEvent> _bufferEvents = new List<CameraEvent>();
        private readonly List<CommandBuffer> _buffers = new List<CommandBuffer>();

        /// <summary>
        /// Takes the command buffers off the preview camera, which is where the dark band has been
        /// hiding for five sessions.
        ///
        /// A command buffer is not a component. `camera.AddCommandBuffer` attaches work to a
        /// camera and that work keeps running when the component that attached it is disabled,
        /// when its fields are zeroed, and when every Behaviour on the camera is switched off --
        /// which is exactly the set of things that has been tried, and exactly why none of them
        /// changed anything. Nothing in this file had ever asked the camera what it was carrying.
        ///
        /// Everything measured says this is it:
        ///
        /// - the plateau is `rgba 1,1,1,127` -- black at an alpha of exactly half, and
        ///   `MaskAndShadow.ShadowStrength` is 0.5 on every stock instance
        /// - it runs to about 250 pixels and is gone by 512, and `ShadowShift.x` is -0.05, which on
        ///   a 5420-wide target is 271 pixels
        /// - it is on one side only, which is what a shift does and a blur does not
        /// - no single renderer owns it: hiding any one of the 42 takes at most 31 pixels of 203,
        ///   because the buffer draws all of them into a mask and then offsets and blurs the whole
        ///   mask, so each renderer only owns its own share
        /// - with every renderer hidden the target is empty, so it is drawn from the character and
        ///   not from anything else
        ///
        /// Zeroing `MaskAndShadow`'s fields never had a chance: the buffer was built while the
        /// values were still stock, and a built buffer does not re-read them.
        ///
        /// Every buffer is recorded with the camera and the event it was attached to, so
        /// `GiveBackCommandBuffers` can put it back exactly where it was. Behind the same
        /// **Remove the cast shadow** option that has always owned this, and the names are logged
        /// either way -- if taking all of them costs something else on screen, the log says which
        /// one to spare.
        /// </summary>
        private void TakeCommandBuffers(Camera camera)
        {
            if (camera == null || !DeployScreenPlugin.StagingCastShadowOff.Value) return;

            try
            {
                if (camera.commandBufferCount == 0)
                {
                    DeployScreenPlugin.Log.LogInfo(
                        "[DeployScreen] preview camera '" + camera.name
                        + "' carries no command buffers; the band is something else again");
                    return;
                }

                foreach (CameraEvent when in Enum.GetValues(typeof(CameraEvent)))
                {
                    CommandBuffer[] buffers;

                    try { buffers = camera.GetCommandBuffers(when); }
                    catch { continue; }

                    if (buffers == null || buffers.Length == 0) continue;

                    foreach (var buffer in buffers)
                    {
                        if (buffer == null) continue;

                        var mine = TheShadow(buffer.name);

                        DeployScreenPlugin.Log.LogInfo(
                            "[DeployScreen] command buffer on '" + camera.name + "' at " + when
                            + ": '" + buffer.name + "', " + buffer.sizeInBytes + " bytes -- "
                            + (mine ? "removed" : "left alone"));

                        if (!mine) continue;

                        camera.RemoveCommandBuffer(when, buffer);

                        _bufferCameras.Add(camera);
                        _bufferEvents.Add(when);
                        _buffers.Add(buffer);
                    }
                }
            }
            catch (Exception error) { WarnOnce(error); }
        }

        /// <summary>
        /// Whether a command buffer is the cast shadow, by the name it gave itself.
        ///
        /// The first version took every buffer off the camera, which found the answer and was the
        /// wrong thing to ship, for two reasons the very first log showed.
        ///
        /// The camera carried two: **'grab background'** at `BeforeGBuffer`, and **'grab alpha and
        /// blur'** at `BeforeImageEffectsOpaque`. The second one is the shadow and says so -- grab
        /// the silhouette out of the alpha, blur it, and that is the mask. The first is not, and
        /// taking it is the likeliest reason the player's next words were that the PMC looked a
        /// bit dark.
        ///
        /// The other reason is worse. That same log shows
        /// `'[WeaponCamoAndStickers] Deferred Decals'` still attached at `BeforeLighting` -- another
        /// mod's work, which survived only because it was added after this ran. On a load where the
        /// order came out the other way, taking everything would have silently broken somebody
        /// else's mod, and the weapon camo would have quietly stopped drawing with nothing to say
        /// why.
        ///
        /// So: matched on the name, and anything unrecognised is logged and left where it is. A
        /// buffer this does not remove is a buffer whose owner still gets to run.
        /// </summary>
        private static bool TheShadow(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            var lower = name.ToLowerInvariant();

            return lower.Contains("alpha") && lower.Contains("blur");
        }

        private void GiveBackCommandBuffers()
        {
            for (var i = _buffers.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_bufferCameras[i] != null && _buffers[i] != null)
                        _bufferCameras[i].AddCommandBuffer(_bufferEvents[i], _buffers[i]);
                }
                catch { }
            }

            _bufferCameras.Clear();
            _bufferEvents.Clear();
            _buffers.Clear();
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

        private static bool _watched;
        private static double _watchFrom = -1;

        /// <summary>
        /// Attaches the back-button probe a few seconds into the screen.
        ///
        /// Not from DumpScreen with the rest of the dump. The game only makes the cancel button
        /// available partway through -- ChangeCancelButtonVisibility, which the traces show firing
        /// around four seconds -- and a listener added before that attaches to an inactive object
        /// and hears nothing.
        ///
        /// This drove the dark-shape probes too, sampling twice because one sample cannot tell a
        /// thing that is off from a thing that is off *yet*. They answered -- it was a command
        /// buffer named 'grab alpha and blur' -- and went with the answer. What they found is in
        /// CLAUDE.md; what they were is in the history.
        /// </summary>
        internal static void WatchForPreview(Component screen, double now)
        {
            if (screen == null || _watched) return;
            if (!DeployScreenPlugin.ReportLayout.Value) return;

            if (_watchFrom < 0) _watchFrom = now;
            if (now - _watchFrom < 4.0) return;

            _watched = true;
            ReportBackButton(screen);
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
                    new UnityEngine.Events.UnityAction(() => Fired(what))
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
        /// One button event, on both records: the log for reading by eye, and the trace so it sits
        /// on the same timeline as the screen closing and the raid starting.
        /// </summary>
        private static void Fired(string what)
        {
            DeployScreenPlugin.Log.LogInfo("[DeployScreen] back: " + what + " fired");
            LoadingPerformance.Note("back: " + what);
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

            if (_subCaptionText == null) return;

            // A notice outranks the intel while it is up. Intel is a slow cycle nobody is waiting
            // on; this is the one line on the screen with a deadline behind it.
            if (_notice != null)
            {
                if (_noticeUntil < 0)
                {
                    _noticeUntil = now + 6.0;
                    DeployScreenPlugin.Log.LogInfo("[DeployScreen] notice: " + _notice);
                }

                // Every frame, not once. This row belongs to the game and the game writes to it;
                // the intel only looks stable because it is rewritten every few seconds, which
                // would hide an overwrite completely. A line that is meant to be read has to hold
                // its own against that, and a TMP set on a loading screen costs nothing.
                if (now < _noticeUntil)
                {
                    Write(_notice);
                    return;
                }

                _notice = null;
                _noticeUntil = -1;

                // Straight back to the cycle rather than after another full interval, so the
                // line does not sit empty on whatever the notice interrupted.
                _nextCard = 0;
            }

            if (_cards == null || _cards.Count == 0) return;
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
                Write(string.IsNullOrEmpty(card.Header)
                    ? card.Body
                    : "<color=#C8A45C>" + card.Header + "</color>   " + card.Body);
            }
            catch (Exception error)
            {
                WarnOnce(error);
                _subCaptionText = null;
            }
        }

        private string _notice;
        private double _noticeUntil = -1;

        /// <summary>
        /// One line, in the row the intel cycles through, for something the player needs to know
        /// now rather than eventually.
        ///
        /// Built for the end of the abort window. The game decides how long backing out is offered
        /// -- `MatchmakerPlayersController.MatchingAbortAvailability`, bound straight to
        /// `ChangeCancelButtonVisibility` -- and when it stops it simply removes the button, on one
        /// run 49 seconds before the raid actually started. Twice now that has been reported as
        /// Back not working, because from the player's side an empty corner and a dead button look
        /// identical.
        ///
        /// It borrows the row the same way the intel does rather than building anything: a
        /// TextMeshPro object of its own would be a new thing to place, size and put back on a
        /// screen this mod is already rearranging.
        /// </summary>
        internal void Notice(string text)
        {
            if (string.IsNullOrEmpty(text) || _subCaptionText == null) return;

            _notice = text;
            _noticeUntil = -1;
        }

        /// <summary>The one place the borrowed row is written, so the notice and the intel agree.</summary>
        private void Write(string line)
        {
            try { _subCaptionText.SetValue(_subCaption, line ?? string.Empty, null); }
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
        private readonly List<CanvasGroup> _planeFades = new List<CanvasGroup>();
        private readonly List<Component> _planeImages = new List<Component>();
        private readonly List<Color> _planeColours = new List<Color>();
        private float _fade = 1f;
        private float _fadeWaited;

        /// <summary>When the menu came up, as a point on _fadeWaited, or -1 before it has.</summary>
        private float _menuUpAt = -1f;

        /// <summary>The next _fadeWaited at which the hold reports what it is still waiting on.</summary>
        private float _nextHoldSample;

        /// <summary>
        /// Where the backdrop camera stood when the abort came, so the hold can say how far it has
        /// moved since -- which turned out to be barely at all, and is kept as the invariant that
        /// says so.
        /// </summary>
        private Vector3 _holdCameraFrom;
        private Quaternion _holdCameraFacing = Quaternion.identity;
        private bool _holdCameraKnown;

        /// <summary>The screen-space canvas the art is re-drawn on for the hold, if it got one.</summary>
        private GameObject _holdOverlay;

        /// <summary>Whether the client has been seen working at all, so that stopping means something.</summary>
        private bool _sawBusy;

        /// <summary>The dots that say the hold is a wait and not a hang, and where they are in their cycle.</summary>
        private readonly List<Component> _workingDots = new List<Component>();
        private float _workingTime;

        /// <summary>
        /// Whether the art is actually on screen, which is the question every previous probe here
        /// has managed not to ask.
        ///
        /// LivePlanes counts references and so answers "do the planes exist". Deactivating a plane
        /// -- or any ancestor of it -- leaves every component non-null, so a plane can read 2/2
        /// while drawing nothing at all. Five aborts reported 2/2 over an empty menu room on the
        /// strength of that. This reports activeInHierarchy instead, and names the first ancestor
        /// that is switched off, because knowing the art is hidden is only half of it.
        /// </summary>
        private string PlaneVisibility()
        {
            if (_created.Count == 0) return "visible=0/0";

            var visible = 0;
            string culprit = null;

            foreach (var plane in _created)
            {
                if (plane == null) continue;

                if (plane.activeInHierarchy) { visible++; continue; }
                if (culprit != null) continue;

                // Walk up to whichever object actually switched off. The plane itself being
                // active while an ancestor is not is the case that matters, and it is the case
                // a check on the plane alone cannot see.
                for (var t = plane.transform; t != null; t = t.parent)
                {
                    if (t.gameObject.activeSelf) continue;
                    culprit = t.gameObject.name;
                    break;
                }

                if (culprit == null) culprit = "unknown";
            }

            var camera = _camera == null
                ? " camera=gone"
                : " camera-on=" + _camera.isActiveAndEnabled;

            // Said plainly. The last run left this to be inferred from a fade-group count reading
            // 2/3, which is true but needs somebody to notice it.
            var overlay = _holdOverlay == null
                ? " overlay=gone"
                : " overlay=" + (_holdOverlay.activeInHierarchy ? "up" : "inactive");

            return "visible=" + visible + "/" + _created.Count
                   + (culprit == null ? "" : " switched-off-by='" + culprit + "'")
                   + camera + overlay;
        }

        /// <summary>
        /// How far the backdrop camera has travelled since the abort, for the trace. Kept after it
        /// disproved its own hypothesis: two aborts reported 0.14m and 0.2 degrees across a whole
        /// hold, which is drift, so the camera being re-posed was never the reason the art went.
        /// </summary>
        private string CameraDelta()
        {
            if (_camera == null) return "camera=gone";
            if (!_holdCameraKnown) return "camera=unknown";

            var eye = _camera.transform;

            return "camera-moved=" + Vector3.Distance(eye.position, _holdCameraFrom).ToString("0.00")
                   + "m/" + Quaternion.Angle(eye.rotation, _holdCameraFacing).ToString("0.0") + "deg";
        }

        /// <summary>How many art planes still exist. Unity nulls a destroyed component for us.</summary>
        private int LivePlanes()
        {
            var alive = 0;

            foreach (var group in _planeFades)
            {
                if (group != null) alive++;
            }

            return alive;
        }

        /// <summary>
        /// The end of the hold whatever happens. The art waiting for a screen that never arrives
        /// is the one failure worse than letting go early, because there is no way out of it.
        ///
        /// Ten was not enough: a Shoreline abort still had nothing up at ten seconds, and the cap
        /// fired into the empty menu room that the hold exists to cover. Twenty is past anything
        /// measured and is still an end.
        /// </summary>
        /// <summary>
        /// The end of the hold whatever happens.
        ///
        /// Twenty was measured against a round trip and was right for one. It is not right for the
        /// menu rebuild: a Shoreline abort still read `busy=preloader` at 21.4s, so the cap fired
        /// on a client that was mid-work and the art came down onto the blurred room. Forty gives
        /// that rebuild room to finish, and costs nothing in the ordinary case because the hold
        /// now ends on the client going idle rather than on running out of patience.
        /// </summary>
        private const float HoldCapSeconds = 40f;

        /// <summary>
        /// How long to wait for the client to look busy at all before deciding it never will.
        ///
        /// The release watches for the preloader going up and coming back down, which cannot fire
        /// on a machine quick enough that it never goes up. Three traced aborts had it showing
        /// 3.5s, 3.7s and 4.0s after the hold began, so eight is that with the margin doubled: long
        /// enough that a slow client is never mistaken for an idle one, short enough that a fast
        /// one is not made to stare at a held picture for the length of the cap.
        /// </summary>
        private const float NeverBusySeconds = 8f;

        /// <summary>
        /// The hard end, used only while the client is demonstrably still working.
        ///
        /// The cap exists so the art cannot hold for ever, but firing it on a client that is still
        /// busy does the exact thing the hold was built to prevent: it drops the picture onto a
        /// half-built menu. So while Busy() keeps answering, the wait is allowed to run on to here
        /// instead. Extending on evidence rather than on hope is the difference -- this only ever
        /// applies when something is actually turning.
        /// </summary>
        private const float HoldCeilingSeconds = 120f;
        private float _dim;
        private bool _dimming;
        private string _released;

        /// <summary>
        /// How far down the picture goes while a cancel is waited out.
        ///
        /// Was a quarter, and a quarter was wrong for a reason the timings make obvious: this is
        /// not a flash, it is held for the whole wait, and that wait measured eleven seconds from
        /// click to menu. Eleven seconds at a quarter brightness is not an acknowledgement, it is
        /// a dark screen -- which is very close to what the stock deploy screen looks like, and
        /// was reported as having defaulted back to it.
        ///
        /// Two thirds instead. Enough to register as a change at the moment of the press, not
        /// enough to throw away the picture that is meant to be covering the wait.
        /// </summary>
        private const float Dimmed = 0.66f;

        /// <summary>
        /// Answers the press at once, while the game takes its time about the rest.
        ///
        /// The measured gap between the click and the screen actually closing is up to five
        /// seconds -- a server round trip we wait on and do not control. Holding the art at full
        /// brightness through it meant a press that had worked looked exactly like one that had
        /// not, and it was reported as Back not working three times over while the trace showed
        /// three clean aborts.
        ///
        /// Tint rather than alpha, and this is the point of it: lowering alpha would thin the art
        /// and show the game's own deploy screen through it, which is the bug the dissolve exists
        /// to avoid. Darkening the colour leaves the planes fully opaque, so nothing behind them
        /// can appear early.
        ///
        /// A quarter, not zero. Four seconds of black is a worse hang than four seconds of
        /// picture: the point is to say "heard you" and keep the scene alive underneath, not to
        /// end the screen before the game has.
        /// </summary>
        internal void BeginDimming()
        {
            if (!_built || _planeImages.Count == 0) return;

            _dimming = true;
            _dim = 0f;
        }

        /// <summary>One frame of that. Silent when nothing asked for it.</summary>
        internal void DimStep(float seconds)
        {
            if (!_dimming || GameTypes.Background_Color == null) return;

            _dim = Mathf.Clamp01(_dim + seconds / Mathf.Max(0.05f, DeployScreenPlugin.StagingDimSeconds.Value));

            var k = Mathf.Lerp(1f, Dimmed, _dim);

            for (var i = 0; i < _planeImages.Count; i++)
            {
                var image = _planeImages[i];
                if (image == null) continue;

                var was = _planeColours[i];

                try
                {
                    GameTypes.Background_Color.SetValue(
                        image, new Color(was.r * k, was.g * k, was.b * k, was.a), null);
                }
                catch { }
            }

            try { _grade.DimCharacter(Mathf.Lerp(1f, Dimmed, _dim)); }
            catch { }
        }

        /// <summary>
        /// Puts the menu back behind the art and hands over the art's own alpha, so what follows
        /// is a dissolve rather than a cut.
        ///
        /// Order is the whole of it. Restore destroys the planes first and un-hides the menu
        /// furniture afterwards, which is right when nobody is looking -- but run while the player
        /// is watching it is two pops in a row: the picture vanishes onto an empty room, and then
        /// the room fills in. So the furniture comes back *first*, underneath art that is still
        /// fully opaque and hiding it, and the grade goes back with it so the menu is already
        /// lit as itself. Only then does the art thin out, and what it reveals is a main menu that
        /// has been sitting there the whole time.
        ///
        /// False when there is nothing to dissolve, and the caller finishes the ordinary way.
        /// </summary>
        internal bool BeginFade()
        {
            if (!_built || _planeFades.Count == 0) return false;

            var any = false;

            foreach (var group in _planeFades)
            {
                if (group != null) any = true;
            }

            if (!any) return false;

            foreach (var go in _hidden)
            {
                try { if (go != null) go.SetActive(true); }
                catch (Exception error) { WarnOnce(error); }
            }

            _hidden.Clear();

            try { _grade.Restore(); }
            catch (Exception error) { WarnOnce(error); }

            // And the backdrop, started here rather than left to the teardown. Putting the
            // player's own choice back is a scene load; running it after the art had already gone
            // meant the swap happened in full view, so pressing Back showed the map's backdrop,
            // then their own arriving, then a hang, then the menu. Started now it happens behind
            // art that is still solid, and FadeStep does not begin thinning until it is done.
            //
            // Safe to call twice: Restore returns immediately once _changed is false, and the
            // teardown's own call lands after this one has already settled it.
            try { EnvironmentState.Restore(); }
            catch (Exception error) { WarnOnce(error); }

            EnvironmentState.WatchForMenu(_screensParent, _screen);

            _fade = 1f;
            _fadeWaited = 0f;
            _menuUpAt = -1f;
            _nextHoldSample = 4f;
            _released = null;

            // Where the camera is standing as the hold begins. Kept for the trace rather than for
            // the diagnosis: two aborts measured 0.14m across a whole hold, so the camera is not
            // what moves.
            _holdCameraKnown = _camera != null;
            _sawBusy = false;

            if (_holdCameraKnown)
            {
                _holdCameraFrom = _camera.transform.position;
                _holdCameraFacing = _camera.transform.rotation;
            }

            // What the state was before we touched it. Said first and unconditionally, because the
            // whole point is to find out whether the art was already being switched off here, and
            // a fix that also hides its own evidence is how this went wrong the last two times.
            LoadingPerformance.Note("art at the start of the hold: " + PlaneVisibility()
                                    + " parent='" + PlaneParentName() + "'");

            RaiseArtToOverlay();

            return true;
        }

        /// <summary>
        /// Puts the art on a screen-space overlay for the hold, above everything the game draws.
        ///
        /// The one that took a video to find. Everything else about the art was fine the whole
        /// time -- alive, active, correctly placed, on an enabled camera that does not move, all
        /// four of them measured -- and it still was not on screen, because the art is a
        /// *world-space* canvas and what covers it is PreloaderUI: the game's own between-screens
        /// overlay, the darkened blurred backdrop with the wheel in the bottom-right corner. A
        /// Screen Space - Overlay canvas draws after every camera, over all world-space content,
        /// whatever its layer or sorting order or which camera owns it. World space cannot win
        /// that, so it stops trying.
        ///
        /// Instead the picture is re-drawn flat, on our own overlay, sorted above the highest
        /// canvas currently live. What comes across is the art and the light it was graded with --
        /// the wash is already multiplied into each image's colour, so carrying the colour carries
        /// the grade, including however far the cancel dim has got by the time we arrive. What
        /// does not come across is the parallax, the depth between the two planes, and the
        /// character: parallax has no meaning without a camera, and the PMC and the writing belong
        /// to a screen that has already gone.
        ///
        /// The crop is matched rather than re-derived. The world planes are built oversize by the
        /// overscan the drift needs, so only the middle 1/overscan of each was ever visible;
        /// measuring that ratio back off the live plane and the live frustum reproduces exactly
        /// what the player was looking at, and the hand-over does not jump.
        /// </summary>
        private void RaiseArtToOverlay()
        {
            if (_camera == null || _created.Count == 0) return;
            if (_holdOverlay != null) return;

            try
            {
                var root = new GameObject("DeployScreen Hold");

                // A fresh GameObject lands in whatever scene is active, and the scene that is
                // active here is the one being torn down -- "Leaving the game..." is exactly that.
                // The first overlay was built correctly and then unloaded with its scene inside
                // three seconds, which the trace reported as planes=2/3: three fade groups
                // registered, two still alive, the missing one ours. This moves it out of every
                // scene, where nothing being unloaded can take it.
                try { UnityEngine.Object.DontDestroyOnLoad(root); }
                catch (Exception error) { WarnOnce(error); }

                var rootRect = root.AddComponent<RectTransform>();

                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = AboveEverything();

                // The dissolve already knows how to spend an alpha, and this is the thing that has
                // to thin out now rather than the world planes nobody can see.
                var group = root.AddComponent<CanvasGroup>();
                group.alpha = 1f;
                group.interactable = false;
                group.blocksRaycasts = false;

                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = Vector2.zero;
                rootRect.offsetMax = Vector2.zero;

                var copied = 0;

                for (var i = 0; i < _created.Count && i < _planeDistance.Count; i++)
                {
                    if (CopyPlaneToOverlay(_created[i], _planeDistance[i], rootRect)) copied++;
                }

                if (copied == 0)
                {
                    UnityEngine.Object.Destroy(root);
                    LoadingPerformance.Note("no art could be raised to the overlay");
                    return;
                }

                // The hold covers the game's own wheel, so it owes the player one of its own.
                try { BuildWorkingDots(rootRect); }
                catch (Exception error) { WarnOnce(error); }

                _holdOverlay = root;
                _planeFades.Add(group);

                LoadingPerformance.Note(
                    "art raised to a screen overlay for the hold: " + copied
                    + " layer(s) at sorting order " + canvas.sortingOrder
                    + " over a highest-live of " + HighestLiveCanvas()
                    + " on a " + Screen.width + "x" + Screen.height + " screen");
            }
            catch (Exception error)
            {
                WarnOnce(error);
                LoadingPerformance.Note(
                    "the art could not be raised to an overlay -- " + error.GetType().Name);
            }
        }

        /// <summary>
        /// One world plane re-drawn flat, keeping its picture, its graded colour and the crop that
        /// was actually on screen. False when there is nothing there worth copying.
        /// </summary>
        private bool CopyPlaneToOverlay(GameObject plane, float distance, RectTransform parent)
        {
            if (plane == null || GameTypes.BackgroundImage == null) return false;

            var rect = plane.transform as RectTransform;
            if (rect == null) return false;

            var source = SourceImage(plane);
            if (source == null) return false;

            object sprite = null;
            object colour = null;

            try
            {
                if (GameTypes.Background_Sprite != null)
                    sprite = GameTypes.Background_Sprite.GetValue(source, null);

                if (GameTypes.Background_Color != null)
                    colour = GameTypes.Background_Color.GetValue(source, null);
            }
            catch { return false; }

            if (sprite == null) return false;

            // What fraction of the plane the camera could actually see, measured off the live
            // frustum rather than recomputed from the config that built it.
            var frustum = _camera.orthographic
                ? _camera.orthographicSize * 2f
                : 2f * distance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

            var overscan = frustum > 0.001f ? Mathf.Max(1f, rect.sizeDelta.y / frustum) : 1f;

            var copy = new GameObject(plane.name + " (overlay)");
            var copyRect = copy.AddComponent<RectTransform>();
            copyRect.SetParent(parent, false);

            // Centred and oversized by the same ratio, so the middle of the picture lands where
            // the middle of the picture already was.
            copyRect.anchorMin = new Vector2(0.5f, 0.5f);
            copyRect.anchorMax = new Vector2(0.5f, 0.5f);
            copyRect.pivot = new Vector2(0.5f, 0.5f);
            copyRect.anchoredPosition = Vector2.zero;
            copyRect.sizeDelta = new Vector2(Screen.width * overscan, Screen.height * overscan);

            var image = copy.AddComponent(GameTypes.BackgroundImage);

            try
            {
                if (GameTypes.Background_Sprite != null)
                    GameTypes.Background_Sprite.SetValue(image, sprite, null);

                if (GameTypes.Background_Raycast != null)
                    GameTypes.Background_Raycast.SetValue(image, false, null);

                if (colour != null && GameTypes.Background_Color != null)
                {
                    GameTypes.Background_Color.SetValue(image, colour, null);

                    // Registered so the cancel dim keeps reaching the art after the hand-over. The
                    // base colour is the source plane's undimmed one, not the dimmed colour now on
                    // screen, or the dim would compound on itself.
                    _planeImages.Add(image);
                    _planeColours.Add(BaseColourOf(plane, (Color)colour));
                }
            }
            catch { }

            return true;
        }

        /// <summary>The image component on a built plane, or null if it has none.</summary>
        private Component SourceImage(GameObject plane)
        {
            for (var i = 0; i < _created.Count && i < _planeImages.Count; i++)
            {
                if (ReferenceEquals(_created[i], plane)) return _planeImages[i];
            }

            return GameTypes.BackgroundImage == null
                ? null
                : plane.GetComponent(GameTypes.BackgroundImage);
        }

        /// <summary>The undimmed colour a plane was built with, so the dim is never applied twice.</summary>
        private Color BaseColourOf(GameObject plane, Color fallback)
        {
            for (var i = 0; i < _created.Count && i < _planeColours.Count; i++)
            {
                if (ReferenceEquals(_created[i], plane)) return _planeColours[i];
            }

            return fallback;
        }

        /// <summary>
        /// Three dots, bottom right, breathing in sequence.
        ///
        /// The art holding still for twenty seconds reads as a hang, and it reads that way because
        /// the one thing on screen that said otherwise -- the game's own wheel, bottom right -- is
        /// now underneath our overlay. Covering it and putting nothing back is how a working wait
        /// becomes a frozen one to everybody who is not reading the trace.
        ///
        /// Bottom right on purpose: it is where the wheel was, so it lands where the eye already
        /// goes to ask this question. Drawn from a generated circle rather than a font or a game
        /// sprite, because neither can be relied on here -- the screen that owned them has gone --
        /// and a runtime texture has no such dependency.
        /// </summary>
        private void BuildWorkingDots(RectTransform parent)
        {
            if (GameTypes.BackgroundImage == null) return;

            _workingDots.Clear();
            _workingTime = 0f;

            var sprite = DotSprite();
            if (sprite == null) return;

            const float size = 14f;
            const float gap = 26f;

            var holder = new GameObject("Working");
            var holderRect = holder.AddComponent<RectTransform>();
            holderRect.SetParent(parent, false);
            holderRect.anchorMin = new Vector2(1f, 0f);
            holderRect.anchorMax = new Vector2(1f, 0f);
            holderRect.pivot = new Vector2(1f, 0f);
            holderRect.anchoredPosition = new Vector2(-90f, 80f);
            holderRect.sizeDelta = new Vector2(gap * 3f, size);

            for (var i = 0; i < 3; i++)
            {
                var dot = new GameObject("Dot " + (i + 1));
                var rect = dot.AddComponent<RectTransform>();
                rect.SetParent(holderRect, false);
                rect.anchorMin = new Vector2(1f, 0.5f);
                rect.anchorMax = new Vector2(1f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = new Vector2(-gap * (2 - i), 0f);
                rect.sizeDelta = new Vector2(size, size);

                var image = dot.AddComponent(GameTypes.BackgroundImage);

                try
                {
                    if (GameTypes.Background_Sprite != null)
                        GameTypes.Background_Sprite.SetValue(image, sprite, null);

                    if (GameTypes.Background_Raycast != null)
                        GameTypes.Background_Raycast.SetValue(image, false, null);
                }
                catch { }

                _workingDots.Add(image);
            }

            AnimateWorkingDots(0f);
        }

        /// <summary>One frame of the dots. Silent and free when there are none.</summary>
        private void AnimateWorkingDots(float seconds)
        {
            if (_workingDots.Count == 0 || GameTypes.Background_Color == null) return;

            _workingTime += seconds;

            for (var i = 0; i < _workingDots.Count; i++)
            {
                var image = _workingDots[i];
                if (image == null) continue;

                // A third of a cycle apart, so the three of them read as a travelling pulse rather
                // than a flash. Never fully out: a dot that vanishes looks like a dropped frame.
                var phase = _workingTime * 2f - i * (Mathf.PI * 2f / 3f);
                var lift = (Mathf.Sin(phase) + 1f) * 0.5f;

                try
                {
                    GameTypes.Background_Color.SetValue(
                        image, new Color(0.78f, 0.64f, 0.36f, 0.25f + lift * 0.65f), null);
                }
                catch { }
            }
        }

        /// <summary>
        /// A soft filled circle, built once and kept for the session. Antialiased across the last
        /// pixel so it does not read as a cog at this size.
        /// </summary>
        private static Sprite DotSprite()
        {
            if (_dotSprite != null) return _dotSprite;

            try
            {
                const int side = 64;
                var texture = new Texture2D(side, side, TextureFormat.ARGB32, false);
                var middle = (side - 1) * 0.5f;
                var radius = middle - 1f;

                for (var y = 0; y < side; y++)
                {
                    for (var x = 0; x < side; x++)
                    {
                        var distance = Mathf.Sqrt((x - middle) * (x - middle) + (y - middle) * (y - middle));
                        var alpha = Mathf.Clamp01(radius - distance);
                        texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                    }
                }

                texture.Apply();
                texture.hideFlags = HideFlags.HideAndDontSave;

                _dotSprite = Sprite.Create(
                    texture, new Rect(0f, 0f, side, side), new Vector2(0.5f, 0.5f));

                if (_dotSprite != null) _dotSprite.hideFlags = HideFlags.HideAndDontSave;
            }
            catch { _dotSprite = null; }

            return _dotSprite;
        }

        private static Sprite _dotSprite;

        /// <summary>
        /// The top of the sorting range, and the reasoning is the correction to a sweep that did
        /// not work.
        ///
        /// The first attempt took the highest canvas live at the abort and added ten, which came
        /// to 1010. The canvas that needed beating was PreloaderUI's, and the trace says why the
        /// sweep could not see it: `busy=no` at the moment of the sweep, `busy=preloader` four
        /// seconds later. It is not up yet when the abort fires, so no sweep taken then can
        /// account for it, and a sweep taken later is a race against a thing whose timing we do
        /// not control.
        ///
        /// So take the top of the range outright. Nothing can sort above it, the art is meant to
        /// cover everything for exactly as long as the hold lasts, and the overlay is destroyed at
        /// teardown -- so the cost of being at the top is bounded by the hold itself. The sweep is
        /// kept for the log only, because knowing what we had to beat is worth a line.
        /// </summary>
        private static int AboveEverything()
        {
            return short.MaxValue - 1;
        }

        /// <summary>The highest canvas currently drawing, for the trace. Swept once, never cached.</summary>
        private static int HighestLiveCanvas()
        {
            var highest = 0;

            try
            {
                foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (canvas == null || !canvas.isActiveAndEnabled) continue;
                    if (canvas.sortingOrder > highest) highest = canvas.sortingOrder;
                }
            }
            catch { }

            return highest;
        }

        /// <summary>What the planes hang from, for the trace.</summary>
        private string PlaneParentName()
        {
            foreach (var plane in _created)
            {
                if (plane == null) continue;

                var parent = plane.transform.parent;
                return parent == null ? "<none>" : parent.name + " active=" + parent.gameObject.activeInHierarchy;
            }

            return "<no planes>";
        }

        /// <summary>
        /// One frame of the dissolve. True when the art is gone and the rest of the teardown can
        /// run behind it without anyone seeing the seam.
        /// </summary>
        internal bool FadeStep(float seconds)
        {
            if (_planeFades.Count == 0) return true;

            // The hold is a wait, and has to look like one.
            try { AnimateWorkingDots(seconds); }
            catch (Exception error) { WarnOnce(error); }

            // Nothing moves until the menu is genuinely there. The art is at full alpha and is
            // the only thing on screen, which is the whole point: everything the player used to
            // watch -- the backdrop swapping, the raid tearing down, the menu rebuilding -- now
            // happens behind it.
            //
            // Two gates, in order, and the second is the one that was missing:
            //
            //   Settling    the backdrop swap is a scene load, and thinning mid-load shows it.
            //   MenuShown   the main menu is actually on screen.
            //
            // The previous version had no second gate that worked. It stood a *duration* in for
            // it -- hold a second and a half, then go -- because the signal it was using,
            // ShowEnvironment(true), never fires on the cancel path. Two live Lighthouse aborts
            // both released on that timer and reported "lingered", which means the art thinned
            // out over a restored backdrop with no menu on it yet. That gap is the waiting room.
            // A duration cannot fix it: the rebuild takes as long as it takes, and any number
            // short enough not to feel like a hang is too short to cover a slow one.
            //
            // So MenuShown now asks EFT.UI.MenuScreen itself, and the linger is demoted to what
            // it should always have been: a short grace *after* the menu is up, because Show
            // returns a frame or two before the menu is drawn, and dissolving into a half-built
            // menu looks much the same as dissolving into none.
            var grace = Mathf.Max(0f, DeployScreenPlugin.StagingLingerSeconds.Value);

            // Read once a frame, up here, because both the release below and the bound on the wait
            // are answers to the same question.
            var busy = EnvironmentState.Busy();

            if (busy != null) _sawBusy = true;

            // Still working is a reason to keep holding, not a reason to give up on schedule.
            var limit = busy != null ? HoldCeilingSeconds : HoldCapSeconds;

            if (_fadeWaited < limit)
            {
                if (EnvironmentState.Settling)
                {
                    _fadeWaited += seconds;
                    return false;
                }

                if (!EnvironmentState.MenuShown)
                {
                    // The release that actually fires.
                    //
                    // MenuShown wants Arrived() -- EFT.UI.MenuScreen -- and in every abort ever
                    // traced here that has read `menu=off` with `watching=0` beside it, so the
                    // hold has never once ended the way it was designed to. It ends on the cap,
                    // every time, and the cap is what put the art down onto a game that had not
                    // finished: `cap -- nothing came up ... busy=preloader` at 21.4s, dissolving
                    // onto the blurred room the whole transition exists to hide.
                    //
                    // Busy() is the half that does work. It read `no` at the top of the hold and
                    // `preloader` four seconds later, so it is live, it toggles, and it is telling
                    // the truth about the client. Watching it go up and come back down is a real
                    // end-of-work signal, and it does not need MenuScreen to be found at all.
                    // Having been busy and stopping is the end of the wait. What it is *not* is
                    // the end of the art: releasing has to fall through to the dissolve at the
                    // bottom of this method, which is the only thing that spends the alpha.
                    // Returning true here instead hands straight to the teardown, and the teardown
                    // destroys the overlay at whatever opacity it was at -- which is a hard cut,
                    // and was one, for exactly as long as this read `return true`.
                    //
                    // The second half of the test is for the machine that never looks busy at all.
                    // On a client quick enough to be done before the preloader draws a frame, the
                    // first half can never come true, and without this the reward for a fast PC
                    // would be the full cap spent staring at a held picture.
                    var finished = busy == null
                                   && (_sawBusy || _fadeWaited >= NeverBusySeconds);

                    if (!finished)
                    {
                        // Holding is only worth anything while there is art to hold, so the planes
                        // are counted rather than assumed and a count of zero ends the wait.
                        //
                        // Worth knowing what this check cannot see, because believing otherwise cost
                        // four attempts: it counts references, so it answers "do the planes exist",
                        // never "is the art on screen". Through every abort before the overlay it
                        // read 2/2 while the player looked at an empty menu room, because the planes
                        // were alive and simply no longer in front of the camera. Existence was never
                        // the thing going wrong.
                        if (LivePlanes() == 0)
                        {
                            _released = "the art is gone -- nothing left to hold";
                            LoadingPerformance.Note(
                                "art held " + _fadeWaited.ToString("0.0") + "s for the menu ("
                                + _released + ")");
                            return true;
                        }

                        // Sampled on the way, because a cap on its own says only that the wait was
                        // longer than the cap. If twenty seconds turns out not to be enough either,
                        // the shape of the wait is in the trace rather than needing another raid.
                        if (_fadeWaited >= _nextHoldSample)
                        {
                            _nextHoldSample = _fadeWaited + 4f;
                            LoadingPerformance.Note(
                                "still holding at " + _fadeWaited.ToString("0.0") + "s: "
                                + EnvironmentState.MenuState()
                                + " planes=" + LivePlanes() + "/" + _planeFades.Count
                                + " " + PlaneVisibility()
                                + " " + CameraDelta());
                        }

                        _fadeWaited += seconds;
                        return false;
                    }

                    // Released. The grace below then lets the menu draw a frame or two before the
                    // art starts thinning, the same courtesy the MenuShown path already gets.
                    if (_released == null)
                    {
                        // Which of the two it was matters to anyone reading this later: one is the
                        // ordinary ending, the other says this machine never showed a preloader.
                        _released = _sawBusy
                            ? "the client finished working"
                            : "the client never looked busy";

                        LoadingPerformance.Note(
                            "art held " + _fadeWaited.ToString("0.0") + "s for the menu ("
                            + _released + ", " + PlaneVisibility() + ")");
                    }
                }

                if (_menuUpAt < 0f) _menuUpAt = _fadeWaited;

                if (_fadeWaited - _menuUpAt < grace)
                {
                    _fadeWaited += seconds;
                    return false;
                }
            }

            if (_released == null)
            {
                // With the gate on the menu itself there are only two honest endings left. A
                // "cap" now means MenuScreen never came up either, which is a different bug from
                // the one this replaced and wants a different answer -- so it is worth being able
                // to tell them apart in one line of a report.
                _released = _fadeWaited >= HoldCapSeconds && !EnvironmentState.MenuShown
                    ? (busy != null
                        ? "ceiling -- still busy after " + HoldCeilingSeconds.ToString("0")
                          + "s, " + EnvironmentState.MenuState()
                        : "cap -- nothing came up, " + EnvironmentState.MenuState())
                    : EnvironmentState.MenuState();

                LoadingPerformance.Note(
                    "art held " + _fadeWaited.ToString("0.0") + "s for the menu (" + _released
                    + ", " + PlaneVisibility() + ", " + CameraDelta() + ")");
            }

            _fade -= seconds / Mathf.Max(0.05f, DeployScreenPlugin.StagingFadeSeconds.Value);

            var alpha = Mathf.Clamp01(_fade);

            foreach (var group in _planeFades)
            {
                try { if (group != null) group.alpha = alpha; }
                catch { }
            }

            return alpha <= 0f;
        }

        internal void Restore()
        {
            _built = false;
            _fadeWaited = 0f;
            _menuUpAt = -1f;
            _dimming = false;
            _dim = 0f;
            _released = null;
            _planeImages.Clear();
            _planeColours.Clear();
            _notice = null;
            _noticeUntil = -1;
            _planeFades.Clear();
            _fade = 1f;

            // The reading belongs to the picture that is going, not to the next one.
            ArtTone.Forget();

            // And the choice belongs to the raid that is going, so the next one moves on a place.
            ForgetPictureChoices();
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

            // The hold overlay is not in _created on purpose -- it is screen-space and would be
            // wrong for anything that treats that list as world planes -- so it is torn down here.
            try { if (_holdOverlay != null) UnityEngine.Object.Destroy(_holdOverlay); }
            catch (Exception error) { WarnOnce(error); }

            _holdOverlay = null;
            _workingDots.Clear();
            _workingTime = 0f;

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
