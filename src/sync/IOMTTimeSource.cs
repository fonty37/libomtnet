/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

namespace libomtnet.sync
{
    /// <summary>
    /// Abstraction for a time source used by OMTClock.
    /// Implementations can provide local monotonic time (default),
    /// PTP-disciplined time, or any other synchronized time reference.
    /// All timestamps are in 100-nanosecond units (1 second = 10,000,000).
    /// </summary>
    public interface IOMTTimeSource
    {
        /// <summary>
        /// Get the current timestamp in 100-nanosecond units.
        /// This is a monotonic, ever-increasing value starting from 0
        /// when the time source was created.
        /// </summary>
        long GetTimestamp();

        /// <summary>
        /// Returns the current elapsed time in milliseconds.
        /// Used by OMTClock for throttling comparisons.
        /// </summary>
        long ElapsedMilliseconds { get; }

        /// <summary>
        /// Whether this time source is synchronized to an external reference
        /// (e.g., PTP grandmaster). False for local-only time sources.
        /// </summary>
        bool IsSynchronized { get; }

        /// <summary>
        /// Current offset from the external reference in microseconds.
        /// Only meaningful when IsSynchronized is true.
        /// </summary>
        double OffsetMicroseconds { get; }

        /// <summary>
        /// Reset the time source to zero.
        /// </summary>
        void Reset();
    }
}
