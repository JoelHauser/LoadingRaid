using System;
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
    /// This is BSG's UI, not ours. How it is recorded and put back is ScreenFurniture; what is
    /// moved where is here.
    /// </summary>
    internal sealed class ScreenLayout : ScreenFurniture
    {
        /// <summary>
        /// The character, and where he stands when the camera is at rest. Held rather than left to
        /// the re-assert list because this class writes his position every frame: the scene drifts
        /// and he has to drift with it. See DriftCharacter.
        /// </summary>
        private RectTransform _character;
        private Vector2 _characterHome;
        private float _canvasHeight;

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

            // Canvas units, not pixels: the drift arrives as a fraction of the frame, and this is
            // what turns it back into a distance on this screen.
            _canvasHeight = height;

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
                // The panel ships with a backing plate sized for a 24pt label tucked into the
                // corner -- 184x29, fixed, no fitter. Promote the label to a 44pt title and that
                // plate stays the size it was, sitting behind the first few letters as a dark box.
                // It exists to keep a small label readable over the backdrop; the scrim does that
                // job now for everything in the corner.
                Hide(Find(root, "Location Name Panel/Background"));

                var title = Find(root, "Location Name Panel/Name");
                var intel = Find(root, "CaptionsHolder/SubCation");

                Resize(title, 44f);

                // A place names itself in capitals, spaced, the way a title card does.
                Style(title, upper: true, spacing: 6f);

                // The intel line writes its header in a colour, which TMP prints as literal tags
                // unless rich text is on. Off by default on some of these fields, so set it.
                Style(intel, rich: true);

                // The intel line lives under it. CaptionsHolder is a vertical layout group that
                // now holds only the sub-caption, so moving the holder moves the line.
                Place(Find(root, "CaptionsHolder"), TopLeft, TopLeft,
                    new Vector2(side, -(top + height * 0.055f)));

                // Progress bottom left, the way out bottom right, the middle left to the art.
                var progress = Find(root, "Deploying Caption");
                Place(progress, BottomLeft, BottomLeft, new Vector2(side, bottom));
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

                // A dark halo carried by the glyphs themselves. It costs the picture nothing --
                // it is only where the letters are -- and it is what keeps type legible against
                // a busy background rather than merely a bright one, which no amount of flat
                // dimming can do.
                if (DeployScreenPlugin.StagingTextShadow.Value)
                {
                    Shadow(title, 0.16f, 0.50f);
                    Shadow(intel, 0.12f, 0.45f);
                    Shadow(progress, 0.12f, 0.45f);
                }

                // Last, and behind everything: white text on a bright sky is unreadable, and a
                // real screenshot has plenty of bright sky. Two soft gradients give the type
                // something to sit on without dimming the middle of the picture, which is where
                // the character is and where nothing is written.
                //
                // How dark they are is the picture's business, not a number chosen once. See
                // ScrimStrength.
                var above = ScrimStrength(true);
                var below = ScrimStrength(false);

                AddScrim(root, "DeployScreen Scrim Top", true, 0.30f, above);
                AddScrim(root, "DeployScreen Scrim Bottom", false, 0.20f, below);

                Settle();

                DeployScreenPlugin.Log.LogInfo("[DeployScreen] screen layout: " + Description
                    + "; scrim top=" + above.ToString("0.00") + " bottom=" + below.ToString("0.00"));
            }
            catch (Exception error)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] the screen layout could not be applied, putting it back: " + error.Message);
                Restore();
            }
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

            _character = view;
            _characterHome = new Vector2(shift, view.anchoredPosition.y);

            MoveOnce(view, _characterHome);
        }

        /// <summary>
        /// Walks the character with the camera.
        ///
        /// The backdrop is a real scene and the drift parallaxes it for real, but the PMC is a
        /// preview composited on top as a UI image, so without this he is the one thing in the
        /// frame that does not move -- and he is the nearest thing in it. Giving him the shift an
        /// object at the near plane would have is what stops him reading as printed on the
        /// picture. SceneDepth works out how far; this is where it lands.
        ///
        /// This doubles as the re-assert for his position: he is deliberately kept out of Keep's
        /// list, since Keep would put him back at rest and call our own drift somebody else's
        /// doing. A move the game makes is far larger than the step below, so it is still caught.
        ///
        /// Written in steps rather than every frame, and that is not a micro-optimisation.
        /// PlayerModelView carries an AspectRatioFitter, so writing anchoredPosition marks the
        /// rect dirty and queues a layout rebuild -- every frame, on the frames the game can least
        /// afford it, for a drift that moves about a tenth of a unit between them. Half a unit is
        /// comfortably under a pixel on any screen this runs on, so stepping at that size is
        /// invisible and skips roughly nine writes in ten.
        /// </summary>
        private const float DriftStep = 0.5f;

        internal void DriftCharacter(Vector2 fraction)
        {
            if (!Applied || _character == null) return;

            var wanted = _characterHome + fraction * _canvasHeight;
            var at = _character.anchoredPosition;

            if (Mathf.Abs(wanted.x - at.x) < DriftStep && Mathf.Abs(wanted.y - at.y) < DriftStep) return;

            _character.anchoredPosition = wanted;
        }
    }
}
