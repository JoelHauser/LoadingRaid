using System;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// The last six seconds.
    ///
    /// 'Matchmaker Final Countdown' is a sibling of the deploy screen under Menu UI/UI, not part
    /// of it, which is why every arrangement made so far left it stock: nothing had ever looked
    /// at it. It appears after the deploy screen is disabled -- 55.9s against 62.4s for the raid
    /// starting, on the run this was built from -- and by then the staging area used to have been
    /// torn down already, so the picture you had been looking at for a minute vanished and BSG's
    /// centred logo came up over the menu room for the walk-out.
    ///
    /// Two things make it a continuation instead. The staging area is held rather than restored
    /// while this screen is up -- see LoadingPerformance -- so the art is still there; and the
    /// countdown's own furniture is moved into the corners the deploy screen was using, so
    /// nothing jumps at the handover. The map name stays top-left where it was, and where the
    /// progress line said how far along the load was, GET READY and the count say how long you
    /// have.
    ///
    /// What cannot follow is the character: PlayerModelView belongs to the deploy screen and goes
    /// dark with it. There is no preview on this screen to move, and putting one here would mean
    /// loading a second one for six seconds.
    ///
    /// Everything is read from the countdown by name, moved, and put back. The layout these names
    /// come from is in CLAUDE.md, from a live dump of one game version.
    /// </summary>
    internal sealed class CountdownScreen : ScreenFurniture
    {
        /// <summary>The width of the GET READY plate when it cannot be measured. From the dump.</summary>
        private const float PlateWidth = 230f;

        /// <summary>Between the plate and the count that follows it, in canvas units.</summary>
        private const float Gap = 28f;

        private RectTransform _root;

        /// <summary>False once the game takes this screen away, which is when the hold ends.</summary>
        internal bool Showing
        {
            get { return _root != null && _root.gameObject.activeInHierarchy; }
        }

        /// <summary>
        /// Arranges the countdown around the art. The deploy screen passed in is the one that
        /// has just closed: it is disabled by now but its objects are still there, and the map
        /// name written on it is the one this screen should carry on showing.
        /// </summary>
        /// <returns>
        /// False when the screen is there but not laid out yet -- its rect is still empty -- so
        /// the caller can come back next frame rather than arranging against nothing.
        /// </returns>
        internal bool Apply(RectTransform root, Component deployScreen)
        {
            if (root == null) return false;

            var width = root.rect.width;
            var height = root.rect.height;
            if (width < 1f || height < 1f) return false;

            _root = root;

            var side = width * SideMargin;
            var top = height * TopMargin;

            try
            {
                // The wordmark again, 713x317 and centred, over the picture.
                Hide(Find(root, "Logo"));

                // Top-left, exactly where the deploy screen's own name panel was standing a frame
                // ago. The panel is the player's -- name over role -- and it is the right shape
                // for a title over a subtitle, so the destination takes the large line and the
                // player's name drops to the small one underneath it. Nothing is lost and nothing
                // new has to be built.
                var panel = Find(root, "Player Name Panel");

                // Not by name. The dump this was written from has 'Name' over 'Description' and
                // the running build does not -- the first run of this looked for 'Player Name
                // Panel/Name', did not find it, and left the corner stock. What the panel is for
                // survives a rename where the names do not: the big line and the small line under
                // it, found by sorting its labels by font size.
                var labels = LabelsUnder(panel);
                var name = labels.Length > 0 ? labels[0] : null;
                var role = labels.Length > 1 ? labels[1] : null;

                Place(panel, TopLeft, TopLeft, new Vector2(side, -top));

                var place = PlaceName(deployScreen);
                var player = TextOf(name);

                if (place != null)
                {
                    Retext(name, place);
                    if (player != null) Retext(role, player);
                }

                Resize(name, 44f);
                Style(name, upper: true, spacing: 6f);

                // Centre of the screen. The deploy screen keeps its middle clear because the
                // character is standing in it; this screen has no character -- PlayerModelView
                // goes dark with the screen it belongs to -- so the middle is the only empty
                // place on it, and the last thing before a raid deserves the middle rather than
                // a corner.
                //
                // 'Deploying in:' goes: the plate beside the number already says what the number
                // is, and two labels saying it is nearly time is one more than the moment needs.
                Hide(Find(root, "Deploying Caption"));

                // The clock face beside the count is 43x43 of chrome from the stock arrangement.
                // In the corner, against the art, the number carries it alone.
                Hide(Find(root, "Time Icon"));

                var plate = Find(root, "Get Ready Panel");
                var count = Find(root, "Time");

                // The pair is centred by centring the gap between them rather than by measuring
                // them: the plate's right edge lands half a gap left of centre and the count's
                // left edge half a gap right of it. Their widths are close enough -- a 230px
                // plate against a 42pt clock -- that this reads centred, and it stays centred
                // when a locale makes GET READY longer, which measuring one of them would not.
                Place(plate, Centre, new Vector2(1f, 0.5f), new Vector2(-Gap * 0.5f, 0f));

                // Where the count sits vertically is measured off the plate's own writing, not off
                // the plate. Putting both rects on the same line aligns the rects, which is not
                // what an eye reads: it reads the two rows of letters, and the plate's are not
                // necessarily centred in it -- there is no promise a 230x36 image with a caption
                // on it has that caption in the middle, and a first attempt at nudging the count
                // down by a guessed 4 units made it worse rather than better.
                //
                // So the caption says where the line is, and the count is put on it.
                var caption = Find(root, "Get Ready Panel/Text");
                var line = LineOf(root, caption);

                Place(count, Centre, new Vector2(0f, 0.5f), new Vector2(Gap * 0.5f, line));

                // Logged rather than used, now that nothing is measured off it: it is the number
                // to reach for the day the pair stops looking centred.
                var plateWidth = plate == null || plate.rect.width < 1f ? PlateWidth : plate.rect.width;

                if (DeployScreenPlugin.StagingTextShadow.Value)
                {
                    Shadow(name, 0.16f, 0.50f);
                    Shadow(role, 0.12f, 0.45f);
                    Shadow(count, 0.14f, 0.50f);
                    Shadow(caption, 0.10f, 0.40f);
                }

                // The deploy screen's scrims were children of the deploy screen and went dark with
                // it. Same picture, same corners, same reading -- ArtTone still holds it, because
                // the staging area has not been restored -- so the same two gradients are built
                // again here.
                var above = ScrimStrength(true);
                var below = ScrimStrength(false);

                AddScrim(root, "DeployScreen Countdown Scrim Top", true, 0.30f, above);
                AddScrim(root, "DeployScreen Countdown Scrim Bottom", false, 0.20f, below);

                Settle();

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] countdown layout: " + Description
                    + "; labels=" + LabelNames(labels)
                    + "; plate=" + plateWidth.ToString("0") + "px"
                    + "; line=" + line.ToString("0.0")
                    + "; scrim top=" + above.ToString("0.00") + " bottom=" + below.ToString("0.00"));
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] the countdown layout could not be applied, putting it back: "
                    + error.Message);
                Restore();
            }

            // True either way once the screen has been arranged against: a failure has already
            // put itself back and there is nothing to be gained by trying it again next frame.
            return true;
        }

        /// <summary>
        /// The height the plate's caption is written at, in the countdown's own units, so the
        /// count beside it can be put on the same line.
        ///
        /// The plate has just been moved to the middle of the screen, and a transform move is
        /// immediate -- it does not wait for the canvas to rebuild -- so the caption's world
        /// position is already the one it will be drawn at.
        ///
        /// The subtraction is the whole of it, and leaving it out sent the count half a screen
        /// off the top: `line=540.0` in the log, on a screen 1080 tall. InverseTransformPoint
        /// answers in the root's own local space, which is measured from its **pivot**, while the
        /// anchoredPosition this feeds is measured from its **anchor** -- the middle of the rect,
        /// because that is what Centre asks for. Two origins half a screen apart. Taking the
        /// rect's centre off converts between them.
        ///
        /// Zero when there is no caption to measure, which puts the count on the middle of the
        /// screen: the same answer the plate has, and wrong only by however far the plate's own
        /// writing sits off its middle.
        /// </summary>
        private static float LineOf(RectTransform root, RectTransform caption)
        {
            if (root == null || caption == null) return 0f;

            try { return root.InverseTransformPoint(caption.position).y - root.rect.center.y; }
            catch { return 0f; }
        }

        /// <summary>Named in the log, because the next build will rename them again.</summary>
        private static string LabelNames(RectTransform[] labels)
        {
            if (labels == null || labels.Length == 0) return "none";

            var names = new string[labels.Length];
            for (var i = 0; i < labels.Length; i++) names[i] = labels[i].name;

            return string.Join("/", names);
        }

        /// <summary>
        /// The destination, taken from the screen that has just closed rather than worked out
        /// again. It is the localised name the game itself put there, so the two screens cannot
        /// disagree about where you are going. The capitals are not in the string -- the deploy
        /// screen's layout sets them as a font style -- so this screen sets them the same way.
        /// </summary>
        private static string PlaceName(Component deployScreen)
        {
            if (deployScreen == null) return null;

            var root = deployScreen.transform as RectTransform;
            if (root == null) return null;

            var name = root.Find("Location Name Panel/Name") as RectTransform;
            var text = TextOf(name);

            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
