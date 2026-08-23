using System;
using System.IO;
using System.Text;

namespace BotMod.Config
{
    /// <summary>
    /// Crash-safe text file replacement for the operator-maintained
    /// Config/botmod.json. A dashboard toggle or console persist that dies
    /// mid-write must never leave a torn JSON file behind: BotConfig.Load
    /// falls back to defaults on an unparseable primary and every persisted
    /// setting (team assignments, toggles) would be lost silently.
    ///
    /// Write protocol: contents land in "path.tmp" first (fsynced), the
    /// previous good content is copied to "path.bak", then the tmp file moves
    /// over the live path. Readers therefore see either the old or the new
    /// complete file, never a partial one, and Load has a last-known-good to
    /// recover from if the primary is ever corrupted by other means.
    /// </summary>
    internal static class AtomicTextFile
    {
        internal static string TmpPath(string path) { return path + ".tmp"; }
        internal static string BackupPath(string path) { return path + ".bak"; }

        // Process-wide writer serialization: Write stages into a fixed
        // "<path>.tmp" and finishes with delete-then-move over the live path,
        // so two overlapping Writes on the same path could otherwise move a
        // half-written tmp onto the primary (torn file) or lose one update.
        // Callers that read-modify-write (PersistConfigField) take their own
        // gate first; acquisition order is always caller-gate -> WriteGate, so
        // no deadlock. Pure in-memory/FS work under the lock, no callbacks.
        internal static readonly object WriteGate = new object();

        /// <summary>Replace path with contents atomically, keeping the previous
        /// content at path.bak. Throws only if the new content could not be
        /// staged; a failure after staging leaves the old file intact.</summary>
        public static void Write(string path, string contents)
        {
            lock (WriteGate)
            {
                string tmp = TmpPath(path);
                byte[] bytes = Encoding.UTF8.GetBytes(contents ?? "");
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true); // flush to disk: survive power loss, not just process death
                }
                // Best-effort snapshot of the current good content BEFORE the swap,
                // so every interruption point stays recoverable: crash before this
                // line leaves the old primary intact; crash during the swap leaves
                // .bak as the last good copy, which BotConfig.Load picks up.
                try { if (File.Exists(path)) File.Copy(path, BackupPath(path), overwrite: true); }
                catch (Exception) { }
                // File.Move cannot overwrite on .NET Framework/Windows, and
                // File.Replace is unavailable on some filesystems; delete-then-move
                // behaves identically everywhere. The momentary absence of path is
                // covered by the .bak above.
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        /// <summary>Read the best available copy: the primary, else the .bak
        /// written by the last successful Write. Returns false when neither is
        /// readable; parse errors are the caller's problem.</summary>
        public static bool TryRead(string path, out string contents)
        {
            contents = null;
            foreach (string candidate in new[] { path, BackupPath(path) })
            {
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
                // Explicit UTF-8: Write() stages Encoding.UTF8 bytes, so reads
                // must not depend on the platform default codepage to round-trip.
                try { contents = File.ReadAllText(candidate, Encoding.UTF8); return true; }
                catch (Exception) { }
            }
            return false;
        }
    }
}
