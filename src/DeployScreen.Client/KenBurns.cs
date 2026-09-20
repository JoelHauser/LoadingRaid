using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// A slow zoom and drift on one banner image.
    ///
    /// Nothing on the deploy screen moves, which is most of why it reads as dead. The game
    /// animates only the CanvasGroup alpha when banners switch, so the RectTransform is free
    /// and this never fights it.
    ///
    /// It ping-pongs rather than running away: scale eases between 1 and Zoom, position
    /// between zero and Drift along one direction picked per banner, so no banner ever ends
    /// up somewhere it cannot come back from. The original scale and position are captured on
    /// enable and put back on destroy, so removing the mod leaves nothing behind.
    /// </summary>
    internal sealed class KenBurns : MonoBehaviour
    {
        internal float Zoom = 1.06f;
        internal float Period = 18f;
        internal float DriftPixels = 14f;

        private RectTransform _rect;
        private Vector3 _originalScale;
        private Vector2 _originalPosition;
        private Vector2 _direction;
        private float _phase;
        private bool _captured;

        private void OnEnable()
        {
            _rect = transform as RectTransform;
            if (_rect == null)
            {
                enabled = false;
                return;
            }

            if (!_captured)
            {
                _originalScale = _rect.localScale;
                _originalPosition = _rect.anchoredPosition;

                // A direction per banner, so a page of them does not all slide the same way.
                var angle = Random.Range(0f, Mathf.PI * 2f);
                _direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

                // Start each one somewhere different in the cycle.
                _phase = Random.Range(0f, 1f);

                _captured = true;
            }
        }

        private void Update()
        {
            if (_rect == null || Period <= 0f) return;

            _phase += Time.unscaledDeltaTime / Period;
            if (_phase > 1f) _phase -= Mathf.Floor(_phase);

            // Ping-pong 0..1..0, then smoothstep so the turn at each end is not a visible jolt.
            var t = Mathf.PingPong(_phase * 2f, 1f);
            t = t * t * (3f - 2f * t);

            var scale = Mathf.Lerp(1f, Zoom, t);
            _rect.localScale = new Vector3(_originalScale.x * scale, _originalScale.y * scale, _originalScale.z);
            _rect.anchoredPosition = _originalPosition + _direction * (DriftPixels * t);
        }

        private void OnDestroy()
        {
            Restore();
        }

        /// <summary>
        /// How far this banner is zoomed right now, so a measurement of it can divide the zoom back
        /// out and get the banner's size at rest.
        /// </summary>
        internal float CurrentZoom
        {
            get
            {
                if (_rect == null || !_captured || Mathf.Approximately(_originalScale.x, 0f)) return 1f;

                return _rect.localScale.x / _originalScale.x;
            }
        }

        /// <summary>Puts the transform back exactly as it was found.</summary>
        internal void Restore()
        {
            if (_rect == null || !_captured) return;

            _rect.localScale = _originalScale;
            _rect.anchoredPosition = _originalPosition;
        }
    }
}
