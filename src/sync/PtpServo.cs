#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*/

using System;

namespace libomtnet.sync
{
    /// <summary>
    /// PTP synchronization state.
    /// </summary>
    public enum OMTPtpState
    {
        /// <summary>No PTP master detected yet.</summary>
        Unlocked = 0,
        /// <summary>Initial large offset correction (clock step).</summary>
        Stepping = 1,
        /// <summary>Clock is tracking the master within tolerance.</summary>
        Locked = 2
    }

    /// <summary>
    /// PI (Proportional-Integral) clock servo for PTP clock discipline.
    /// Matches the approach used by linuxptp for smooth clock correction.
    /// </summary>
    internal class PtpServo
    {
        // PI gains - tuned for software PTP (~10-100μs target)
        private double kp;  // Proportional gain
        private double ki;  // Integral gain

        // Servo state
        private double integral;
        private long lastCorrection;
        private int sampleCount;
        private bool stepped;

        // Configuration
        private readonly long stepThreshold;  // In 100ns units: step if offset exceeds this

        // Statistics
        private double lastOffsetNs;
        private double lastPathDelayNs;

        /// <summary>
        /// Current synchronization state.
        /// </summary>
        public OMTPtpState State { get; private set; }

        /// <summary>
        /// Current offset from master in nanoseconds.
        /// </summary>
        public double OffsetNanoseconds => lastOffsetNs;

        /// <summary>
        /// Current path delay in nanoseconds.
        /// </summary>
        public double PathDelayNanoseconds => lastPathDelayNs;

        /// <summary>
        /// Number of sync samples processed.
        /// </summary>
        public int SampleCount => sampleCount;

        public PtpServo(double kp = 0.7, double ki = 0.3, long stepThresholdMs = 100)
        {
            this.kp = kp;
            this.ki = ki;
            this.stepThreshold = stepThresholdMs * 10000; // Convert ms to 100ns units
            this.integral = 0;
            this.lastCorrection = 0;
            this.sampleCount = 0;
            this.stepped = false;
            this.State = OMTPtpState.Unlocked;
        }

        /// <summary>
        /// Process a new offset measurement and return the clock correction to apply.
        /// All values in 100-nanosecond units.
        /// </summary>
        /// <param name="offset">Measured offset from master (positive = local clock ahead)</param>
        /// <param name="pathDelay">Measured one-way path delay</param>
        /// <returns>Correction to apply to local clock (subtract from local time)</returns>
        public long ProcessSample(long offset, long pathDelay)
        {
            sampleCount++;
            lastOffsetNs = offset * 100.0; // Convert 100ns units to nanoseconds
            lastPathDelayNs = pathDelay * 100.0;

            long absOffset = Math.Abs(offset);

            // First sample or very large offset: step the clock
            if (!stepped || absOffset > stepThreshold)
            {
                stepped = true;
                integral = 0;
                lastCorrection = offset;
                State = OMTPtpState.Stepping;
                return offset; // Full step correction
            }

            // PI servo: gradual correction
            double correction = kp * offset + ki * integral;
            integral += offset;

            // Clamp integral to prevent windup
            double maxIntegral = stepThreshold * 10.0;
            if (integral > maxIntegral) integral = maxIntegral;
            if (integral < -maxIntegral) integral = -maxIntegral;

            lastCorrection = (long)correction;

            // Determine state based on offset magnitude
            // Locked if offset < 1ms (10000 * 100ns units)
            if (absOffset < 10000)
                State = OMTPtpState.Locked;
            else
                State = OMTPtpState.Stepping;

            return lastCorrection;
        }

        /// <summary>
        /// Reset the servo to its initial state.
        /// </summary>
        public void Reset()
        {
            integral = 0;
            lastCorrection = 0;
            sampleCount = 0;
            stepped = false;
            State = OMTPtpState.Unlocked;
        }
    }
}
#endif
