using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Rearranges the deploy screen's own furniture into the shape the staging area was designed
    /// around: the destination named large in the top corner with its intel underneath, the
    /// progress line and the way out in the bottom corners, and the middle of the screen left to
    /// the art and the character standing in it.
    ///
    /// The stock screen is built for a banner panel taking up the right-hand half. With the panel
    /// gone and the art filling the frame, the centred title and the centred logo sit on top of
    /// the picture instead of beside it, which is what makes a full-screen backdrop read as
    /// wallpaper behind a menu rather than as a place.
    ///
    /// This is BSG's UI, not ours. Every anchor, pivot, position and font size is recorded before
    /// it is touched and put back in Restore, in reverse order, whatever happens -- the deploy
    /// screen is reused between raids, so a value left behind is permanent for the session.
    /// Anything not found by name is skipped and said out loud rather than guessed at: these
    /// names come from a live dump of one game version and nothing guarantees the next one.
    /// </summary>
    internal sealed class ScreenLayout
    {
        /// <summary>Margins as a fraction of the screen, matching the preview this was drawn from.</summary>
        private const float SideMargin = 0.04f;
        private const float TopMargin = 0.09f;
        private const float BottomMargin = 0.08f;

        /// <summary>Canvas pixels of menu task bar to keep clear along the bottom edge.</summary>
        private const float TaskBarHeight = 64f;

        private struct Placed
        {
            internal RectTransform Rect;
            internal Vector2 AnchorMin, AnchorMax, Pivot, Position;
        }

        private struct Resized
        {
            internal object Text;
            internal PropertyInfo Size;
            internal object Was;
        }

        private readonly List<Placed> _placed = new List<Placed>();
        private readonly List<Placed> _wanted = new List<Placed>();
        private readonly List<Resized> _resized = new List<Resized>();
        private readonly List<GameObject> _hidden = new List<GameObject>();
        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private readonly List<string> _missing = new List<string>();

        private bool _applied;

        internal bool Applied { get { return _applied; } }

        internal string Description
        {
            get
            {
                return "moved=" + _placed.Count + "; resized=" + _resized.Count
                    + "; hidden=" + _hidden.Count
                    + (_missing.Count == 0 ? "" : "; not found: " + string.Join(", ", _missing.ToArray()));
            }
        }

        // ------------------------------------------------------------------ apply

        internal void Apply(Component screen)
        {
            if (screen == null) return;

            var root = screen.transform as RectTransform;
            if (root == null) return;

            // Margins come off the screen's own rect rather than Screen.width: this is canvas
            // space, and on this UI the two are not the same number.
            var width = root.rect.width;
            var height = root.rect.height;
            if (width < 1f || height < 1f) return;

            var side = width * SideMargin;
            var top = height * TopMargin;
            var bottom = height * BottomMargin;

            try
            {
                // The centred title says "deploying on location" directly above a map name that
                // says the same thing better. With the name promoted, it is noise over the art.
                Hide(Find(root, "CaptionsHolder/MainCaption"));

                // The logo is a 713x317 image anchored to the bottom centre -- across the art and
                // through the character. Vanilla gets away with it because what is behind it is a
                // dim scene; a picture cannot.
                Hide(Find(root, "Logo"));

                // The destination, named where a place names itself.
                Place(Find(root, "Location Name Panel"), TopLeft, TopLeft, new Vector2(side, -top));
                Resize(Find(root, "Location Name Panel/Name"), 44f);

                // A place names itself in capitals, spaced, the way a title card does.
                Style(Find(root, "Location Name Panel/Name"), upper: true, spacing: 6f);

                // The intel line writes its header in a colour, which TMP prints as literal tags
                // unless rich text is on. Off by default on some of these fields, so set it.
                Style(Find(root, "CaptionsHolder/SubCation"), rich: true);

                // The intel line lives under it. CaptionsHolder is a vertical layout group that
                // now holds only the sub-caption, so moving the holder moves the line.
                Place(Find(root, "CaptionsHolder"), TopLeft, TopLeft,
                    new Vector2(side, -(top + height * 0.055f)));

                // Progress bottom left, the way out bottom right, the middle left to the art.
                Place(Find(root, "Deploying Caption"), BottomLeft, BottomLeft, new Vector2(side, bottom));
                // Not moved: the spinner carries an Animation that drives its own transform, so
                // re-anchoring it leaves the animation playing against the old frame and the
                // thing loops around the screen instead of spinning in place. The caption below
                // already says the percentage, and the arrangement this is built from has no
                // spinner at all, so it goes rather than moves.
                Hide(Find(root, "Loader"));
                Place(Find(root, "Back Button Panel"), BottomRight, BottomRight, new Vector2(-side, bottom));

                // The character sits 120px left of centre because the stock screen keeps the
                // right-hand half for the banner panel. With the panel gone that reads as
                // off-centre, so put the character in the middle of its own picture. Position
                // only: this one is anchored to stretch vertically, and pinning it to a point
                // would collapse its height.
                CentreCharacter(Find(root, "PlayerModelView"));

                // Last, and behind everything: white text on a bright sky is unreadable, and a
                // real screenshot has plenty of bright sky. Two soft gradients give the type
                // something to sit on without dimming the middle of the picture, which is where
                // the character is and where nothing is written.
                AddScrim(root, "DeployScreen Scrim Top", true, 0.30f, 0.62f);
                AddScrim(root, "DeployScreen Scrim Bottom", false, 0.20f, 0.44f);

                _applied = _placed.Count > 0 || _hidden.Count > 0 || _created.Count > 0;

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] screen layout: " + Description);
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] the screen layout could not be applied, putting it back: " + error.Message);
                Restore();
            }
        }

        private static Vector2 TopLeft { get { return new Vector2(0f, 1f); } }
        private static Vector2 BottomLeft { get { return new Vector2(0f, 0f); } }
        private static Vector2 BottomRight { get { return new Vector2(1f, 0f); } }

        /// <summary>
        /// By path under the screen root, because that is what the layout dump prints and what a
        /// person can check against a log. A miss is recorded, never guessed around.
        /// </summary>
        private RectTransform Find(RectTransform root, string path)
        {
            var found = root.Find(path) as RectTransform;
            if (found == null) _missing.Add(path);
            return found;
        }

        /// <summary>
        /// Puts the character in the middle of the screen rather than the middle of its own
        /// texture. Moving the view to x=0 is not enough: the character is not centred inside it.
        /// The game says where it is -- DragTrigger is anchored around the character so the mouse
        /// can turn it, 0.24 to 0.60 on this build -- so the centre of that is the character's
        /// centre, and shifting the view by the difference lands it on the middle of the screen.
        ///
        /// Position only. This one stretches vertically, and pinning it to a point would collapse
        /// its height.
        /// </summary>
        private void CentreCharacter(RectTransform view)
        {
            if (view == null) return;

            var drag = view.Find("DragTrigger") as RectTransform;
            var centre = drag == null ? 0.5f : (drag.anchorMin.x + drag.anchorMax.x) * 0.5f;
            var shift = (0.5f - centre) * view.rect.width;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] character sits at " + centre.ToString("0.00")
                + " across its view, shifting " + shift.ToString("0") + "px to centre it");

            MoveTo(view, new Vector2(shift, view.anchoredPosition.y));
        }

        /// <summary>Moves without re-anchoring, for anything whose anchors are doing work.</summary>
        private void MoveTo(RectTransform rect, Vector2 position)
        {
            if (rect == null) return;

            Remember(rect);
            rect.anchoredPosition = position;

            _wanted.Add(new Placed
            {
                Rect = rect,
                AnchorMin = rect.anchorMin,
                AnchorMax = rect.anchorMax,
                Pivot = rect.pivot,
                Position = position,
            });
        }

        private void Remember(RectTransform rect)
        {
            _placed.Add(new Placed
            {
                Rect = rect,
                AnchorMin = rect.anchorMin,
                AnchorMax = rect.anchorMax,
                Pivot = rect.pivot,
                Position = rect.anchoredPosition,
            });
        }

        private void Place(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 position)
        {
            if (rect == null) return;

            Remember(rect);

            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;

            _wanted.Add(new Placed
            {
                Rect = rect,
                AnchorMin = anchor,
                AnchorMax = anchor,
                Pivot = pivot,
                Position = position,
            });
        }

        /// <summary>
        /// Font size through reflection, for the same reason everything else here is: the plugin
        /// holds no reference to the game's UI assemblies, and TextMeshProUGUI lives in one.
        /// </summary>
        private void Resize(Component text, float size)
        {
            if (text == null) return;

            foreach (var component in text.GetComponents<Component>())
            {
                if (component == null) continue;

                var property = component.GetType().GetProperty("fontSize");
                if (property == null || !property.CanRead || !property.CanWrite) continue;
                if (property.PropertyType != typeof(float)) continue;

                _resized.Add(new Resized
                {
                    Text = component,
                    Size = property,
                    Was = property.GetValue(component, null),
                });

                property.SetValue(component, size, null);
                return;
            }
        }

        /// <summary>
        /// Type on a title card rather than type in a menu: capitals, a little tracking, and rich
        /// text where a colour is going to be written into it. All by reflection and all optional
        /// -- a build that does not have one of these properties simply keeps what it had.
        /// </summary>
        private void Style(Component text, bool upper = false, float spacing = float.NaN, bool rich = false)
        {
            if (text == null) return;

            foreach (var component in text.GetComponents<Component>())
            {
                if (component == null) continue;

                var type = component.GetType();
                if (type.GetProperty("fontSize") == null) continue;   // a text component, not a fitter

                if (upper) Remember(type, component, "fontStyle", UpperCaseOf(type));
                if (!float.IsNaN(spacing)) Remember(type, component, "characterSpacing", spacing);
                if (rich) Remember(type, component, "richText", true);
                return;
            }
        }

        /// <summary>
        /// TMP's FontStyles is a flags enum in an assembly this plugin does not reference, so the
        /// value is built from the live type. UpperCase is 0x10 and has been since TextMeshPro 1.x.
        /// </summary>
        private static object UpperCaseOf(Type textType)
        {
            var property = textType.GetProperty("fontStyle");
            if (property == null) return null;

            try { return Enum.ToObject(property.PropertyType, 16); }
            catch { return null; }
        }

        private void Remember(Type type, object component, string name, object value)
        {
            if (value == null) return;

            var property = type.GetProperty(name);
            if (property == null || !property.CanRead || !property.CanWrite) return;
            if (!property.PropertyType.IsInstanceOfType(value)) return;

            try
            {
                _resized.Add(new Resized
                {
                    Text = component,
                    Size = property,
                    Was = property.GetValue(component, null),
                });

                property.SetValue(component, value, null);
            }
            catch { }
        }

        /// <summary>
        /// A gradient panel pinned to the top or bottom edge, drawn first so it sits over the art
        /// but under the character and the type. Built through the same image component the art
        /// planes use, so no new reference to the game's UI assembly is needed.
        /// </summary>
        private void AddScrim(RectTransform root, string name, bool top, float fraction, float strength)
        {
            if (GameTypes.BackgroundImage == null) return;

            var sprite = ScrimSprite(top);
            if (sprite == null) return;

            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;

            rect.SetParent(root, false);
            rect.anchorMin = top ? new Vector2(0f, 1f - fraction) : new Vector2(0f, 0f);
            rect.anchorMax = top ? new Vector2(1f, 1f) : new Vector2(1f, fraction);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            // The task bar along the bottom of the menu -- hideout, traders, and whatever tabs
            // other mods have added to it -- is not part of this screen and is drawn under it.
            // A scrim that runs to the very bottom edge therefore dims someone else's UI, which
            // reads as that mod being broken. Lift the band clear of it.
            if (!top) rect.offsetMin = new Vector2(0f, TaskBarHeight);
            rect.localScale = Vector3.one;

            // First sibling: UI draws in hierarchy order, so this lands over the backdrop and
            // under the character and everything written on top of it.
            rect.SetAsFirstSibling();
            go.layer = root.gameObject.layer;

            var image = go.AddComponent(GameTypes.BackgroundImage);

            if (GameTypes.Background_Sprite != null) GameTypes.Background_Sprite.SetValue(image, sprite, null);
            if (GameTypes.Background_Color != null)
                GameTypes.Background_Color.SetValue(image, new Color(0.02f, 0.025f, 0.03f, strength), null);
            if (GameTypes.Background_Raycast != null) GameTypes.Background_Raycast.SetValue(image, false, null);

            _created.Add(go);
        }

        /// <summary>
        /// One pixel wide and sixty-four tall: the stretch does the rest. Squared falloff, so the
        /// darkness gathers at the edge instead of greying the whole band.
        /// </summary>
        private Sprite ScrimSprite(bool top)
        {
            const int Height = 64;

            try
            {
                var texture = new Texture2D(1, Height, TextureFormat.RGBA32, false);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;

                var pixels = new Color32[Height];

                for (var y = 0; y < Height; y++)
                {
                    var t = y / (float)(Height - 1);
                    var a = top ? t : 1f - t;
                    a *= a;
                    pixels[y] = new Color32(255, 255, 255, (byte)Mathf.Clamp(a * 255f, 0f, 255f));
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                _textures.Add(texture);

                return Sprite.Create(texture, new Rect(0f, 0f, 1f, Height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch
            {
                return null;
            }
        }

        private void Hide(Component target)
        {
            if (target == null || !target.gameObject.activeSelf) return;

            target.gameObject.SetActive(false);
            _hidden.Add(target.gameObject);
        }

        // ------------------------------------------------------------------- keep

        /// <summary>
        /// Puts the arrangement back whenever the game undoes it.
        ///
        /// The deploy screen is not laid out once. The game sets these positions again when it
        /// changes state -- the countdown before a raid is the one that shows -- and whatever it
        /// writes last wins. Applying the layout at screen-show and walking away means the screen
        /// snaps back to stock partway through the load, which is exactly what it did.
        ///
        /// So the arrangement is re-asserted rather than set: cheap, because it compares first
        /// and only writes when something has actually moved.
        /// </summary>
        internal void Keep()
        {
            if (!_applied) return;

            for (var i = 0; i < _wanted.Count; i++)
            {
                var one = _wanted[i];
                if (one.Rect == null) continue;

                if (one.Rect.anchorMin == one.AnchorMin && one.Rect.anchorMax == one.AnchorMax
                    && one.Rect.pivot == one.Pivot && one.Rect.anchoredPosition == one.Position) continue;

                one.Rect.anchorMin = one.AnchorMin;
                one.Rect.anchorMax = one.AnchorMax;
                one.Rect.pivot = one.Pivot;
                one.Rect.anchoredPosition = one.Position;

                Reasserted(one.Rect.name);
            }

            for (var i = 0; i < _hidden.Count; i++)
            {
                if (_hidden[i] == null || !_hidden[i].activeSelf) continue;

                _hidden[i].SetActive(false);
                Reasserted(_hidden[i].name);
            }
        }

        /// <summary>Said once per element: a log line every frame would be worse than the bug.</summary>
        private readonly HashSet<string> _reasserted = new HashSet<string>();

        private void Reasserted(string name)
        {
            if (!_reasserted.Add(name)) return;

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] screen layout: the game moved '" + name + "' back, re-applying");
        }

        // ---------------------------------------------------------------- restore

        internal void Restore()
        {
            // Reverse order throughout: the screen is reused between raids, so anything left
            // behind here is permanent until the game is restarted.
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                try { if (_created[i] != null) UnityEngine.Object.Destroy(_created[i]); }
                catch { }
            }
            _created.Clear();

            for (var i = _textures.Count - 1; i >= 0; i--)
            {
                try { if (_textures[i] != null) UnityEngine.Object.Destroy(_textures[i]); }
                catch { }
            }
            _textures.Clear();

            for (var i = _hidden.Count - 1; i >= 0; i--)
            {
                try { if (_hidden[i] != null) _hidden[i].SetActive(true); }
                catch { }
            }
            _hidden.Clear();

            for (var i = _resized.Count - 1; i >= 0; i--)
            {
                try
                {
                    var one = _resized[i];
                    if (one.Text != null && one.Size != null) one.Size.SetValue(one.Text, one.Was, null);
                }
                catch { }
            }
            _resized.Clear();

            for (var i = _placed.Count - 1; i >= 0; i--)
            {
                try
                {
                    var one = _placed[i];
                    if (one.Rect == null) continue;

                    one.Rect.anchorMin = one.AnchorMin;
                    one.Rect.anchorMax = one.AnchorMax;
                    one.Rect.pivot = one.Pivot;
                    one.Rect.anchoredPosition = one.Position;
                }
                catch { }
            }
            _placed.Clear();
            _wanted.Clear();
            _reasserted.Clear();

            _missing.Clear();
            _applied = false;
        }
    }
}
