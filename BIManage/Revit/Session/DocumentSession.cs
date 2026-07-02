using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using BIManage.Revit.Events;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.Session
{
    /// <summary>
    ///     Per-document session object tracking document state and history
    ///     Created on DocumentOpened, disposed on DocumentClosed
    /// </summary>
    public class DocumentSession : IDisposable
    {
        private bool _disposed;

        public DocumentSession(Document document)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            OpenedAt = DateTime.UtcNow;
            ChangeSetHistory = new List<ChangeSet>();

            // Detect document type
            IsWorkshared = DocumentTypeHelper.IsWorkshared(document);
            IsCloud = DocumentTypeHelper.IsCloud(document);
        }

        /// <summary>
        ///     Associated Revit document
        /// </summary>
        public Document Document { get; }

        /// <summary>
        ///     When this session was created (document opened)
        /// </summary>
        public DateTime OpenedAt { get; }

        /// <summary>
        ///     Last modification time (updated on DocumentChanged)
        /// </summary>
        public DateTime? LastModified { get; set; }

        /// <summary>
        ///     Whether this is a workshared document
        /// </summary>
        public bool IsWorkshared { get; }

        /// <summary>
        ///     Whether this is a cloud document (BIM360/Revit Cloud)
        /// </summary>
        public bool IsCloud { get; }

        /// <summary>
        ///     Current active view (updated by ViewTrackingService)
        /// </summary>
        public View? CurrentView { get; set; }

        /// <summary>
        ///     Current selection element IDs (updated by SelectionTrackingService)
        /// </summary>
        public ICollection<ElementId> CurrentSelection { get; set; } = new List<ElementId>();

        /// <summary>
        ///     History of changesets for this document session
        ///     Limited to last 100 changesets to prevent memory growth
        /// </summary>
        public List<ChangeSet> ChangeSetHistory { get; }

        /// <summary>
        ///     Add a changeset to history (with limit)
        /// </summary>
        public void AddChangeSet(ChangeSet changeSet)
        {
            if (changeSet == null) throw new ArgumentNullException(nameof(changeSet));

            ChangeSetHistory.Add(changeSet);
            LastModified = changeSet.Timestamp;

            // Limit history size to prevent memory growth
            const int MAX_HISTORY = 100;
            if (ChangeSetHistory.Count > MAX_HISTORY)
            {
                ChangeSetHistory.RemoveAt(0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            ChangeSetHistory.Clear();
            _disposed = true;
        }
    }
}
