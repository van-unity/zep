using System;
using System.Collections.Generic;

namespace Zep.Core.State
{
    /// <summary>
    /// How the split workspace divides its width between panes: one weight per
    /// pane, always non-negative and summing to one. Owns inserting and removing
    /// a pane, dragging and equalizing a divider, restoring a saved layout, and
    /// keeping every pane at or above its minimum width.
    /// </summary>
    public sealed class SplitPaneWeights
    {
        private readonly List<float> _weights = new List<float>();

        public SplitPaneWeights()
        {
            Values = _weights.AsReadOnly();
        }

        /// <summary>A live read-only view, left to right.</summary>
        public IReadOnlyList<float> Values { get; }

        public int Count => _weights.Count;

        public float this[int index] => _weights[index];

        /// <summary>
        /// Adopts a saved layout when it has one weight per pane; otherwise the
        /// panes share the width equally.
        /// </summary>
        public void Restore(IReadOnlyList<float> weights)
        {
            if (weights == null || weights.Count != _weights.Count)
            {
                EqualizeAll();
                return;
            }

            for (var i = 0; i < weights.Count; i++)
            {
                var weight = weights[i];
                _weights[i] = float.IsNaN(weight) || float.IsInfinity(weight)
                    ? 0f
                    : Max(0f, weight);
            }

            Normalize();
        }

        /// <summary>
        /// Makes room for a pane at <paramref name="index"/> (or at the end when
        /// it is past the last pane): the new pane takes an equal share and the
        /// existing ones keep their proportions.
        /// </summary>
        public void Insert(int index)
        {
            var oldCount = _weights.Count;
            if (oldCount == 0)
            {
                _weights.Add(1f);
                return;
            }

            var newWeight = 1f / (oldCount + 1f);
            for (var i = 0; i < _weights.Count; i++)
            {
                _weights[i] *= 1f - newWeight;
            }

            if (index >= oldCount)
            {
                _weights.Add(newWeight);
                return;
            }

            _weights.Insert(index, newWeight);
        }

        /// <summary>Removes a pane and hands its share to the others in proportion.</summary>
        public void RemoveAt(int index)
        {
            _weights.RemoveAt(index);
            Normalize();
        }

        /// <summary>
        /// Moves the divider between pane <paramref name="index"/> and the next
        /// one so the left pane takes <paramref name="leftWidth"/> of the pair's
        /// <paramref name="pairWidth"/>. The pair's combined share is unchanged.
        /// </summary>
        public void ResizePair(int index, float leftWidth, float pairWidth)
        {
            var pairWeight = _weights[index] + _weights[index + 1];
            _weights[index] = pairWeight * (leftWidth / pairWidth);
            _weights[index + 1] = pairWeight - _weights[index];
            Normalize();
        }

        public void EqualizePair(int index)
        {
            var pairWeight = _weights[index] + _weights[index + 1];
            _weights[index] = pairWeight * 0.5f;
            _weights[index + 1] = pairWeight * 0.5f;
            Normalize();
        }

        /// <summary>
        /// Raises every pane to at least <paramref name="minimumPaneWidth"/>,
        /// taking the difference from the panes that have room, given the width
        /// left for panes after the dividers. Equal shares when the minimums
        /// cannot all fit.
        /// </summary>
        public void ClampToWidth(float availableWidth, float dividersWidth, float minimumPaneWidth)
        {
            Normalize();
            if (_weights.Count == 0 || availableWidth <= 0f)
            {
                return;
            }

            var paneWidth = Max(0f, availableWidth - dividersWidth);
            if (paneWidth <= 0f)
            {
                EqualizeAll();
                return;
            }

            var minimumWeight = Min(
                1f / _weights.Count,
                minimumPaneWidth / paneWidth);
            if ((minimumWeight * _weights.Count) >= 0.9999f)
            {
                EqualizeAll();
                return;
            }

            var sum = 0f;
            for (var i = 0; i < _weights.Count; i++)
            {
                _weights[i] = Max(_weights[i], minimumWeight);
                sum += _weights[i];
            }

            var excess = sum - 1f;
            if (excess <= 0.0001f)
            {
                Normalize();
                return;
            }

            var reducible = 0f;
            for (var i = 0; i < _weights.Count; i++)
            {
                reducible += _weights[i] - minimumWeight;
            }

            if (reducible <= 0.0001f)
            {
                EqualizeAll();
                return;
            }

            for (var i = 0; i < _weights.Count; i++)
            {
                var available = _weights[i] - minimumWeight;
                _weights[i] -= excess * (available / reducible);
            }

            Normalize();
        }

        private void Normalize()
        {
            if (_weights.Count == 0)
            {
                return;
            }

            var sum = 0f;
            for (var i = 0; i < _weights.Count; i++)
            {
                _weights[i] = Max(0f, _weights[i]);
                sum += _weights[i];
            }

            if (sum <= 0.0001f)
            {
                EqualizeAll();
                return;
            }

            for (var i = 0; i < _weights.Count; i++)
            {
                _weights[i] /= sum;
            }
        }

        private void EqualizeAll()
        {
            if (_weights.Count == 0)
            {
                return;
            }

            var weight = 1f / _weights.Count;
            for (var i = 0; i < _weights.Count; i++)
            {
                _weights[i] = weight;
            }
        }

        // The comparisons the view's own math used, kept so a NaN weight moves
        // through them exactly as it did.
        private static float Max(float a, float b) => a > b ? a : b;

        private static float Min(float a, float b) => a < b ? a : b;
    }
}
