namespace Evidgate.Modules.Healing.Application;

internal sealed partial class RedactionPipeline
{
    /// <summary>The deadline of one payload and the decode budget of the window being scanned.</summary>
    internal sealed class ScanBudget(TimeProvider clock)
    {
        private readonly long _start = clock.GetTimestamp();

        internal bool Aborted { get; private set; }

        internal int DecodedTokens { get; set; }

        internal bool Truncated { get; set; }

        internal void StartWindow()
        {
            DecodedTokens = 0;
            Truncated = false;
        }

        /// <summary>True once the payload's time is spent; stays true afterwards.</summary>
        internal bool Check()
        {
            if (!Aborted && clock.GetElapsedTime(_start) > Budget)
            {
                Aborted = true;
            }

            return Aborted;
        }
    }
}
