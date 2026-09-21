using System.Collections.Generic;
using StardewModdingAPI;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Records the order in which this mod's overlays and the vanilla tooltip are drawn within a
    /// single frame, for a handful of frames, then logs it.
    ///
    /// Z-order bugs here are ordering bugs, not layer-depth bugs, and ordering is invisible from a
    /// screenshot and from any single log line -- what matters is the *sequence*, which spans two
    /// SMAPI events and a Harmony postfix. So the notes are buffered per frame and flushed as one
    /// line, and only while explicitly armed (a tooltip is redrawn every frame it's hovered, so an
    /// always-on trace is unreadable).
    /// </summary>
    public static class DrawTrace
    {
        private static int framesRemaining;
        private static readonly List<string> notes = new();
        private static int frameNumber;

        public static bool Armed => framesRemaining > 0;

        /// <summary>Traces the next <paramref name="frames"/> frames.</summary>
        public static void Arm(int frames)
        {
            framesRemaining = frames;
            notes.Clear();
        }

        /// <summary>Flushes the previous frame's sequence and starts a new one.</summary>
        public static void BeginFrame()
        {
            if (framesRemaining <= 0)
                return;

            if (notes.Count > 0)
                ModEntry.Log($"[trace] frame {frameNumber}: {string.Join(" -> ", notes)}");

            notes.Clear();
            frameNumber++;
            framesRemaining--;

            if (framesRemaining == 0)
                ModEntry.Log("[trace] done");
        }

        public static void Note(string what)
        {
            if (framesRemaining > 0)
                notes.Add(what);
        }
    }
}
