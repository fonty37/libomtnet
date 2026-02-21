/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System.Diagnostics;

namespace libomtnet.sync
{
    /// <summary>
    /// Default time source using System.Diagnostics.Stopwatch.
    /// Provides local monotonic time with no external synchronization.
    /// This preserves the existing OMTClock behavior exactly.
    /// </summary>
    public class OMTLocalTimeSource : IOMTTimeSource
    {
        private Stopwatch clock;

        public OMTLocalTimeSource()
        {
            clock = Stopwatch.StartNew();
        }

        public long GetTimestamp()
        {
            return clock.ElapsedMilliseconds * 10000;
        }

        public long ElapsedMilliseconds
        {
            get { return clock.ElapsedMilliseconds; }
        }

        public bool IsSynchronized
        {
            get { return false; }
        }

        public double OffsetMicroseconds
        {
            get { return 0; }
        }

        public void Reset()
        {
            clock = Stopwatch.StartNew();
        }
    }
}
