using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Moving BSG's furniture around and putting every last piece of it back.
    ///
    /// Two screens need this now -- the deploy screen and the final countdown that follows it --
    /// and they need exactly the same care, for the same reason: neither is ours. Every anchor,
    /// pivot, position, font size, colour, string and material is recorded before it is touched
    /// and restored in reverse order, whatever happens. These screens are reused between raids,
    /// so a value left behind is permanent for the session.
    ///
    /// Anything not found by name is skipped and said out loud rather than guessed at: the names
    /// come from a live dump of one game version and nothing guarantees the next one.
    ///
    /// Everything here is reflection. The plugin holds no reference to the game's UI assemblies,
    /// and TextMeshProUGUI lives in one -- so a label is "a component with a fontSize property"
    /// and is written to through PropertyInfo. A build without one of these properties keeps
    /// what it had rather than failing.
    /// </summary>
    internal abstract class ScreenFurniture
    {
        /// <summary>Margins as a fraction of the screen, matching the preview this was drawn from.</summary>
        protected const float SideMargin = 0.04f;
        protected const float TopMargin = 0.09f;
        protected const float BottomMargin = 0.08f;

        /// <summary>How far past this screen's own edges a scrim is stretched, in canvas units.</summary>
        private const float Overhang = 2000f;

        /// <summary>How far past the top or bottom edge a scrim is stretched, in canvas units.</summary>
        private const float EdgeOverhang = 140f;

        protected struct Placed
        {
            internal RectTransform Rect;
            internal Vector2 AnchorMin, AnchorMax, Pivot, Position;
        }

        protected struct Resized
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
        private readonly List<Material> _materials = new List<Material>();
        private readonly List<string> _missing = new List<string>();

        private bool _applied;

        internal bool Applied { get { return _applied; } }

        /// <summary>Set once the subclass has finished arranging, from what it managed to touch.</summary>
        protected void Settle()
        {
            _applied = _placed.Count > 0 || _hidden.Count > 0 || _created.Count > 0;
        }

        internal string Description
        {
            get
            {
                return "moved=" + _placed.Count + "; resized=" + _resized.Count
                    + "; hidden=" + _hidden.Count
                    + (_missing.Count == 0 ? "" : "; not found: " + string.Join(", ", _missing.ToArray()));
            }
        }

        protected static Vector2 TopLeft { get { return new Vector2(0f, 1f); } }
        protected static Vector2 BottomLeft { get { return new Vector2(0f, 0f); } }
        protected static Vector2 BottomRight { get { return new Vector2(1f, 0f); } }
        protected static Vector2 Centre { get { return new Vector2(0.5f, 0.5f); } }

        // ------------------------------------------------------------------ finding

        /// <summary>
        /// By path under the screen root, because that is what the layout dump prints and what a
        /// person can check against a log. A miss is recorded, never guessed around.
        /// </summary>
        protected RectTransform Find(RectTransform root, string path)
        {
            if (root == null) return null;

            var found = root.Find(path) as RectTransform;
            if (found == null) _missing.Add(path);
            return found;
        }

        // ------------------------------------------------------------------ moving

        protected void Place(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 position)
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

        /// <summary>Moves without re-anchoring, for anything whose anchors are doing work.</summary>
        protected void MoveTo(RectTransform rect, Vector2 position)
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

        /// <summary>
        /// Moves and records for restore, but does not re-assert. For anything this class writes
        /// every frame itself: putting it in the re-assert list as well means Keep sets it back
        /// to where it started and then the driver moves it again, and the "the game moved this
        /// back" line fires on our own writes.
        /// </summary>
        protected void MoveOnce(RectTransform rect, Vector2 position)
        {
            if (rect == null) return;

            Remember(rect);
            rect.anchoredPosition = position;
        }

        protected void Remember(RectTransform rect)
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

        // ------------------------------------------------------------------- type

        /// <summary>
        /// Font size through reflection, for the same reason everything else here is: the plugin
        /// holds no reference to the game's UI assemblies, and TextMeshProUGUI lives in one.
        /// </summary>
        protected void Resize(Component text, float size)
        {
            if (text == null) return;

            var label = LabelOn(text);
            if (label == null) return;

            var property = label.GetType().GetProperty("fontSize");
            if (property == null || !property.CanRead || !property.CanWrite) return;
            if (property.PropertyType != typeof(float)) return;

            _resized.Add(new Resized
            {
                Text = label,
                Size = property,
                Was = property.GetValue(label, null),
            });

            property.SetValue(label, size, null);
        }

        /// <summary>
        /// Type on a title card rather than type in a menu: capitals, a little tracking, and rich
        /// text where a colour is going to be written into it. All optional -- a build that does
        /// not have one of these properties simply keeps what it had.
        /// </summary>
        protected void Style(Component text, bool upper = false, float spacing = float.NaN, bool rich = false)
        {
            var label = LabelOn(text);
            if (label == null) return;

            var type = label.GetType();

            if (upper) Remember(type, label, "fontStyle", UpperCaseOf(type));
            if (!float.IsNaN(spacing)) Remember(type, label, "characterSpacing", spacing);
            if (rich) Remember(type, label, "richText", true);
        }

        /// <summary>What a label says now, so it can be moved somewhere else on the screen.</summary>
        protected static string TextOf(Component text)
        {
            var label = LabelOn(text);
            if (label == null) return null;

            var property = label.GetType().GetProperty("text");
            if (property == null || !property.CanRead) return null;

            try { return property.GetValue(label, null) as string; }
            catch { return null; }
        }

        /// <summary>Writes a label, remembering what it said.</summary>
        protected void Retext(Component text, string value)
        {
            if (value == null) return;

            var label = LabelOn(text);
            if (label == null) return;

            Remember(label.GetType(), label, "text", value);
        }

        /// <summary>
        /// The labels under a panel, largest first, found by asking rather than by name.
        ///
        /// Names are the fragile part of all of this -- 'Player Name Panel/Name' is in a dump of
        /// one build and was already wrong on the next one -- but what a panel is *for* survives a
        /// rename: the big line and the small line under it. Sorting its labels by font size finds
        /// those two whatever they are called.
        /// </summary>
        protected static RectTransform[] LabelsUnder(RectTransform panel)
        {
            if (panel == null) return new RectTransform[0];

            var found = new List<RectTransform>();
            var sizes = new List<float>();

            foreach (var child in panel.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child == panel) continue;

                var rect = child as RectTransform;
                if (rect == null) continue;

                var label = LabelOn(rect);
                if (label == null) continue;

                var property = label.GetType().GetProperty("fontSize");
                if (property == null || !property.CanRead || property.PropertyType != typeof(float)) continue;

                float size;
                try { size = (float)property.GetValue(label, null); }
                catch { continue; }

                found.Add(rect);
                sizes.Add(size);
            }

            // Small enough that anything cleverer than an insertion sort would be for show: a
            // panel has two or three labels on it.
            for (var i = 1; i < found.Count; i++)
            {
                for (var j = i; j > 0 && sizes[j] > sizes[j - 1]; j--)
                {
                    var size = sizes[j]; sizes[j] = sizes[j - 1]; sizes[j - 1] = size;
                    var rect = found[j]; found[j] = found[j - 1]; found[j - 1] = rect;
                }
            }

            return found.ToArray();
        }

        /// <summary>
        /// The text component on an object, as this file understands one: something with a
        /// fontSize. That rules out the fitters and layout groups sitting beside it.
        /// </summary>
        private static Component LabelOn(Component target)
        {
            if (target == null) return null;

            foreach (var component in target.GetComponents<Component>())
            {
                if (component == null) continue;
                if (component.GetType().GetProperty("fontSize") == null) continue;

                return component;
            }

            return null;
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

        protected void Remember(Type type, object component, string name, object value)
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

        // ----------------------------------------------------------------- shadow

        /// <summary>
        /// A soft dark halo behind one label, through TextMeshPro's own underlay.
        ///
        /// A scrim dims a band of the picture; this dims only what is behind the letters, which
        /// is why the two are worth having together. It is also the only one of the two that
        /// helps against a *busy* background -- branches, rubble, a chain-link fence -- where the
        /// problem is not brightness but that the type has no clean edge to read against.
        ///
        /// Reading fontMaterial is the load-bearing line. TMP hands back a copy made for this
        /// label alone and quietly assigns it, so nothing else sharing the font is touched -- and
        /// it is why the shared material it had is recorded first and put back in Restore, with
        /// the copy destroyed after it.
        /// </summary>
        protected void Shadow(Component text, float dilate, float softness)
        {
            var label = LabelOn(text);
            if (label == null) return;

            var type = label.GetType();
            var shared = type.GetProperty("fontSharedMaterial");
            var owned = type.GetProperty("fontMaterial");
            if (shared == null || owned == null || !shared.CanRead || !shared.CanWrite) return;

            try
            {
                var was = shared.GetValue(label, null) as Material;
                if (was == null) return;

                var mine = owned.GetValue(label, null) as Material;
                if (mine == null) return;

                // A font drawn by something other than TMP's distance-field shader has no
                // underlay to switch on. Put back what was there rather than leaving the label
                // with a private copy of a material for no reason.
                if (!mine.HasProperty("_UnderlayColor"))
                {
                    shared.SetValue(label, was, null);
                    return;
                }

                mine.EnableKeyword("UNDERLAY_ON");
                mine.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.85f));

                // Centred rather than offset: a drop shadow says which way the light comes from,
                // and there is no light in a UI. A halo just holds the letter.
                mine.SetFloat("_UnderlayOffsetX", 0f);
                mine.SetFloat("_UnderlayOffsetY", 0f);
                mine.SetFloat("_UnderlayDilate", dilate);
                mine.SetFloat("_UnderlaySoftness", softness);

                // The quad round each glyph is sized for the glyph. An underlay spreading past it
                // is clipped at the edge -- a halo with its corners cut off -- unless the label is
                // asked to work its padding out again.
                var padding = type.GetMethod("UpdateMeshPadding", Type.EmptyTypes);
                if (padding != null) padding.Invoke(label, null);

                _resized.Add(new Resized { Text = label, Size = shared, Was = was });
                _materials.Add(mine);
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] no shadow behind '" + text.name + "': " + error.Message);
            }
        }

        // ------------------------------------------------------------------ scrim

        /// <summary>
        /// How dark a scrim has to be for the picture that is actually up there.
        ///
        /// These are the player's own screenshots, so the range is the whole range. A dawn
        /// treeline needs almost nothing and any more is a bruise across the art; a snow field or
        /// a white sky needs everything this will give. One number chosen by eye is wrong for one
        /// of those two, and until now it was wrong for the bright one.
        ///
        /// So the measurement drives it, and the player still gets a multiplier over the top of
        /// it: taste is not a thing a luminance reading can settle.
        /// </summary>
        protected static float ScrimStrength(bool top)
        {
            // Over a dark picture, over a bright one, and what to use when nothing was measured
            // -- which is the value this shipped with, so an install where the reading fails
            // looks exactly as it did before.
            var quiet = top ? 0.40f : 0.26f;
            var loud = top ? 0.88f : 0.68f;
            var standing = top ? 0.62f : 0.44f;

            var taste = Mathf.Clamp(DeployScreenPlugin.StagingScrimStrength.Value, 0f, 2f);
            var tone = ArtTone.Current;

            if (!tone.Known || !DeployScreenPlugin.StagingScrimAdaptive.Value)
                return Mathf.Clamp01(standing * taste);

            // 0.22 and 0.60: below the first, white type reads over the picture unaided; above
            // it, the picture is brighter than the type and the scrim is the only thing standing
            // between them. Smoothed so two similar pictures do not land either side of a step.
            var t = Mathf.InverseLerp(0.22f, 0.60f, top ? tone.Top : tone.Bottom);
            t = t * t * (3f - 2f * t);

            return Mathf.Clamp01(Mathf.Lerp(quiet, loud, t) * taste);
        }

        /// <summary>
        /// A gradient panel pinned to the top or bottom edge, drawn first so it sits over the art
        /// but under the character and the type. Built through the same image component the art
        /// planes use, so no new reference to the game's UI assembly is needed.
        /// </summary>
        protected void AddScrim(RectTransform root, string name, bool top, float fraction, float strength)
        {
            if (root == null || GameTypes.BackgroundImage == null) return;

            var sprite = ScrimSprite(top);
            if (sprite == null) return;

            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;

            rect.SetParent(root, false);
            rect.anchorMin = top ? new Vector2(0f, 1f - fraction) : new Vector2(0f, 0f);
            rect.anchorMax = top ? new Vector2(1f, 1f) : new Vector2(1f, fraction);

            // Anchored 0..1 of THIS screen, not of the screen. The deploy screen's own rect is
            // narrower than the canvas -- measured at x 440..3000 on a 3440 wide display -- so a
            // scrim that fills it stops 440px short on each side and draws two hard vertical
            // edges over the art. That is the border. Overhang far enough that no width can reach
            // the ends: the gradient runs vertically, so stretching it sideways costs nothing and
            // shows nothing.
            // Vertically too, and for the same reason: this screen's rect does not reach the
            // bottom of the display either -- it was measured starting 21px up -- so a scrim
            // flush with it still leaves a line with bright art beneath. Past the edge, the
            // darkest end of the gradient falls off-screen and what is left runs out of frame
            // with nothing to draw an edge against.
            rect.offsetMin = new Vector2(-Overhang, top ? 0f : -EdgeOverhang);
            rect.offsetMax = new Vector2(Overhang, top ? EdgeOverhang : 0f);

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

        // ------------------------------------------------------------------- hide

        protected void Hide(Component target)
        {
            if (target == null || !target.gameObject.activeSelf) return;

            target.gameObject.SetActive(false);
            _hidden.Add(target.gameObject);
        }

        // ------------------------------------------------------------------- keep

        /// <summary>
        /// Puts the arrangement back whenever the game undoes it.
        ///
        /// These screens are not laid out once. The game sets these positions again when it
        /// changes state, and whatever it writes last wins. Applying the arrangement at
        /// screen-show and walking away means the screen snaps back to stock partway through the
        /// load, which is exactly what it did.
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
            // Reverse order throughout: these screens are reused between raids, so anything left
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

            // After the shared materials are back on the labels, never before: destroying one
            // still assigned leaves a label drawing with nothing.
            for (var i = _materials.Count - 1; i >= 0; i--)
            {
                try { if (_materials[i] != null) UnityEngine.Object.Destroy(_materials[i]); }
                catch { }
            }
            _materials.Clear();

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
