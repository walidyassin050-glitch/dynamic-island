using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyDynamicIsland
{
    // ════════════════════════════════════════════════════════════════════════
    // True word timing for the karaoke glow, in two layers over the line-level
    // LRCLIB stamps the service already returns:
    //
    // ════════════════════════════════════════════════════════════════════════
    // Karaoke timing, two honest layers over LRCLIB's line-level stamps:
    //
    // WordAligner (always on, zero network): the island HEARS the song
    // through the loopback capture that drives the waveform bars. Within a
    // lyric line's window it tracks vocal bursts and nudges the width-
    // proportional word guess by at most ±1 — acoustics correct the estimate,
    // they can never wreck it.
    //
    // (An unofficial YouTube-Music per-word fetcher lived here too; it was
    // removed before publishing. Gray-area endpoints don't belong in a
    // user's network footprint, and the acoustic layer + width ruler cover
    // the job honestly.)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Acoustic onset tracker that refines the karaoke word estimate. Song
    /// times are mapped onto one wall-clock base at BeginLine, so the audio
    /// thread, the paint loop and the lyric window finally share a unit.
    /// </summary>
    internal sealed class WordAligner
    {
        readonly object gate = new();
        double avg = -1;                 // running loudness baseline
        double windowA, windowB, offsetMs;
        double lastOnset = double.MinValue;
        int words, lastIdx;
        bool armed, deadOnce;
        readonly List<double> onsets = new();

        public volatile bool SourceLive;

        static readonly DateTime epoch = new DateTime(2026, 1, 1);
        public static double NowMs => (DateTime.Now - epoch).TotalMilliseconds;

        /// <summary>One capture block's loudness (≈0..1), from the audio thread.</summary>
        public void Push(double wallMs, double level)
        {
            lock (gate)
            {
                if (!SourceLive) return;
                if (avg < 0) avg = level;

                if (!armed || wallMs < windowA - 150 || wallMs > windowB)
                {
                    avg += (level - avg) * 0.001;
                    return;
                }

                double alpha = 0.004;
                if (level > Math.Max(0.012, avg * 1.55) && wallMs - lastOnset > 130)
                {
                    onsets.Add(wallMs);
                    lastOnset = wallMs;
                    alpha = 0.02;       // a crash must not lift its own baseline instantly
                }
                avg += (level - avg) * alpha;
            }
        }

        /// <summary>Arm for one lyric line; all three times are song-relative.</summary>
        public void BeginLine(double wallNow, double posMs, double lineStartMs, double lineEndMs, int wordCount)
        {
            lock (gate)
            {
                offsetMs = wallNow - posMs;
                windowA = lineStartMs + offsetMs;
                windowB = Math.Min(lineEndMs + offsetMs, windowA + 20000);
                words = wordCount;
                onsets.Clear();
                lastOnset = double.MinValue;
                lastIdx = 0;
                deadOnce = false;
                armed = SourceLive && wordCount > 1 && lineEndMs - lineStartMs >= 700;
            }
        }

        /// <summary>
        /// Blend the width-ruler estimate with the sung bursts. The result may
        /// lead or trail the pure estimate by one word at most: a missed burst
        /// costs a syllable, a false burst (drum in the mix) never yeets the
        /// glow lines ahead, and a track the aligner simply cannot hear
        /// (silent capture, no vocals) degrades to the old estimate by itself.
        /// </summary>
        public int Refine(double posMs, int widthIdx)
        {
            lock (gate)
            {
                if (!armed || widthIdx < 0) return Math.Max(0, widthIdx);

                int acoustic = 0;
                for (int i = 0; i < onsets.Count; i++)
                    if (onsets[i] - offsetMs <= posMs + 90) acoustic = i + 1;

                if (widthIdx >= 2 && onsets.Count == 0)
                {
                    if (deadOnce) return Math.Min(words - 1, Math.Max(0, widthIdx));
                    deadOnce = true;
                }

                int idx = (widthIdx + acoustic) / 2;
                if (idx > widthIdx + 1) idx = widthIdx + 1;
                if (idx < widthIdx - 1) idx = widthIdx - 1;
                if (idx < lastIdx) idx = lastIdx;            // words, like time, never rewind
                lastIdx = idx;
                return Math.Min(words - 1, Math.Max(0, idx));
            }
        }
    }

    // (WordLine + the unofficial YouTube-Music fetcher were removed before this
    //   app went public: gray-area endpoints have no business being part of a
    //   user's network footprint. The acoustic layer above stands on its own.)
}
