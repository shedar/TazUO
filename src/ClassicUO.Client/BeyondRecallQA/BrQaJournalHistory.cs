using System;
using System.Collections.Generic;

namespace ClassicUO.BeyondRecallQA
{
    /// <summary>
    /// Retains a small in-memory journal window so a two-client QA barrier cannot lose a
    /// message that arrived between the preceding observation and the wait action.
    /// Matched entries are consumed and never reused as evidence for a later barrier.
    /// </summary>
    internal sealed class BrQaJournalHistory
    {
        private const int MaximumMessageLength = 1024;
        private readonly int _capacity;
        private readonly Queue<string> _entries = new Queue<string>();

        public BrQaJournalHistory(int capacity = 64)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
        }

        public void Add(string text)
        {
            if (text == null)
                return;

            _entries.Enqueue(
                text.Length <= MaximumMessageLength
                    ? text
                    : text.Substring(0, MaximumMessageLength)
            );

            while (_entries.Count > _capacity)
                _entries.Dequeue();
        }

        public bool TryConsumeMatch(string expected, out string matched)
        {
            matched = null;
            if (string.IsNullOrEmpty(expected))
                return false;

            while (_entries.Count > 0)
            {
                string candidate = _entries.Dequeue();
                if (candidate.Contains(expected, StringComparison.Ordinal))
                {
                    matched = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
