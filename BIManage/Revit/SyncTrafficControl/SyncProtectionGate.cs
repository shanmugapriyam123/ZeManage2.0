using System;
using System.Threading;

namespace BIManage.Revit.SyncTrafficControl
{
    /// <summary>
    /// Thread-local marker that distinguishes user-initiated syncs (ribbon Sync to Central,
    /// keyboard shortcut, File menu) from internal/programmatic syncs initiated by:
    ///   • BackgroundSyncEngine — the auto-sync loop.
    ///   • AutoExitExternalEventHandler — sync-and-exit on shutdown.
    ///   • TriggerSyncExternalEventHandler — sync queue dispatching "your turn".
    ///
    /// Command Protection on Sync (configured via command_settings) must fire ONLY for
    /// user-initiated syncs. Background and queued-retry syncs already represent prior user
    /// consent (the user clicked Sync to enter the queue, or enabled background sync in
    /// settings). Re-prompting on every queued/auto sync would break:
    ///   • Background sync's auto-retry loop (would retry-storm against the dialog).
    ///   • Sync queue UX (user already consented at queue-entry; second dialog at "your turn"
    ///     is a regression).
    ///
    /// Usage: wrap the programmatic sync invocation in `using (SyncProtectionGate.EnterInternalSync())`.
    /// The DocumentSynchronizingWithCentral handler reads <see cref="IsInternalSync"/> and skips
    /// Command Protection enforcement when set.
    ///
    /// ThreadLocal because Revit dispatches sync on its UI thread; the marker only needs to
    /// survive the synchronous span of doc.SynchronizeWithCentral / PostCommand(SynchronizeNow).
    /// </summary>
    internal static class SyncProtectionGate
    {
        private static readonly ThreadLocal<int> _depth = new ThreadLocal<int>(() => 0);
        // Time-windowed arm for sync invocations that are asynchronous from the marker site.
        // Used by TriggerSyncExternalEventHandler which calls app.PostCommand — the actual
        // sync runs on a later Revit dispatch tick, after a `using` scope would have already
        // disposed. ArmNextSync stamps an expiry time; IsInternalSync returns true while the
        // window is still open. Both Phase 1.e (BeforeExecuted) and Phase 1.f
        // (DocumentSynchronizingWithCentral) consult the same flag without consuming it, so
        // a single arm covers the full sync lifecycle. 60s is generous for typical Revit
        // sync queue wait + sync execution; after the window closes the next user-initiated
        // click prompts normally.
        private const int ArmWindowSeconds = 60;
        private static long _armedUntilTicksUtc;

        /// <summary>
        /// True when sync is automated (background loop, auto-exit, or queued retry from
        /// TriggerSync). Peek-only: reading does not consume the arming. Used by the
        /// Command Protection enforcers in SyncCommandBinding.OnBeforeExecuted and
        /// EventRegistryService.OnDocumentSynchronizingWithCentral to skip dialogs that
        /// would otherwise re-prompt for sync the user already consented to.
        /// </summary>
        public static bool IsInternalSync
        {
            get
            {
                if (_depth.Value > 0) return true;
                var until = Interlocked.Read(ref _armedUntilTicksUtc);
                return until > 0 && DateTime.UtcNow.Ticks < until;
            }
        }

        /// <summary>
        /// Enter a synchronous internal-sync scope. Reentrant — nested scopes increment a
        /// counter so IsInternalSync stays true until the outermost scope disposes. Always
        /// use with `using` to guarantee the marker clears even if the sync call throws.
        /// For paths where the sync runs synchronously on the marker thread (BackgroundSync,
        /// AutoExit calling doc.SynchronizeWithCentral directly).
        /// </summary>
        public static IDisposable EnterInternalSync() => new Scope();

        /// <summary>
        /// Arm an internal-sync marker valid for <see cref="ArmWindowSeconds"/> seconds.
        /// Use this when the sync dispatch is asynchronous from the marker site — e.g.
        /// PostCommand(SynchronizeNow) where Revit runs the actual sync on a later dispatch
        /// tick. The window auto-expires so a stuck/lost sync doesn't permanently suppress
        /// future user-initiated dialogs.
        /// </summary>
        public static void ArmNextSync()
        {
            var expiresAt = DateTime.UtcNow.AddSeconds(ArmWindowSeconds).Ticks;
            Interlocked.Exchange(ref _armedUntilTicksUtc, expiresAt);
        }

        /// <summary>
        /// Immediately clear any armed window. Optional — call when a sync handler knows the
        /// sync has finished and wants to release the suppression early.
        /// </summary>
        public static void Disarm() => Interlocked.Exchange(ref _armedUntilTicksUtc, 0);

        private sealed class Scope : IDisposable
        {
            private bool _disposed;
            public Scope() { _depth.Value++; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _depth.Value--;
            }
        }
    }
}
