using System;

namespace MusicBeePlugin
{
    // MusicBee exposes position polling but no seek notification. Confirm a
    // discontinuity in two samples before asking the receiver to seek.
    internal sealed class SeekDetector
    {
        private int baselinePosition;
        private DateTime baselineTime;
        private DateTime ignoreUntil;
        private DateTime lastSeekTime;
        private int pendingDirection;
        private bool baselinePlaying;

        internal void Reset(int position, bool playing, DateTime now)
        {
            baselinePosition = position;
            baselineTime = now;
            baselinePlaying = playing;
            ignoreUntil = now.AddMilliseconds(750);
            pendingDirection = 0;
        }

        internal bool ShouldSeek(int position, bool playing, DateTime now)
        {
            if (baselineTime == default(DateTime) || playing != baselinePlaying || now < baselineTime)
            {
                Reset(position, playing, now);
                return false;
            }
            if (now < ignoreUntil) return false;

            var elapsed = playing ? Math.Max(0, (now - baselineTime).TotalMilliseconds) : 0;
            int direction = 0;
            // A stalled local clock is not a rewind. Only a real decrease is.
            if (position < baselinePosition - 1500) direction = -1;
            else if (position > baselinePosition + elapsed + 1500) direction = 1;

            if (direction == 0)
            {
                // Preserve the clock anchor while MusicBee's position is behind
                // wall time. Its gradual catch-up is then ordinary playback.
                if (!playing || position >= baselinePosition + elapsed - 1000)
                {
                    baselinePosition = position;
                    baselineTime = now;
                }
                pendingDirection = 0;
                return false;
            }
            if (pendingDirection != direction)
            {
                pendingDirection = direction;
                return false;
            }
            if ((now - lastSeekTime).TotalSeconds < 2) return false;

            lastSeekTime = now;
            Reset(position, playing, now);
            return true;
        }
    }
}
