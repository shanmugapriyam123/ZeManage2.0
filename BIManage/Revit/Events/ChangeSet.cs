using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIManage.Revit.Events
{
    /// <summary>
    ///     Represents a set of changes to a document (added, modified, deleted elements)
    /// </summary>
    public class ChangeSet
    {
        public IReadOnlyList<ElementId> AddedElementIds { get; }
        public IReadOnlyList<ElementId> ModifiedElementIds { get; }
        public IReadOnlyList<ElementId> DeletedElementIds { get; }
        public Document Document { get; }
        public DateTime Timestamp { get; }
        public int TotalChanges => AddedElementIds.Count + ModifiedElementIds.Count + DeletedElementIds.Count;

        public ChangeSet(
            Document document,
            ICollection<ElementId> addedElementIds,
            ICollection<ElementId> modifiedElementIds,
            ICollection<ElementId> deletedElementIds)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            AddedElementIds = addedElementIds?.ToList() ?? new List<ElementId>();
            ModifiedElementIds = modifiedElementIds?.ToList() ?? new List<ElementId>();
            DeletedElementIds = deletedElementIds?.ToList() ?? new List<ElementId>();
            Timestamp = DateTime.UtcNow;
        }

        public static ChangeSet Empty(Document document) =>
            new ChangeSet(document, Array.Empty<ElementId>(), Array.Empty<ElementId>(), Array.Empty<ElementId>());

        public bool HasChanges => TotalChanges > 0;

        public override string ToString() =>
            $"ChangeSet: +{AddedElementIds.Count} ~{ModifiedElementIds.Count} -{DeletedElementIds.Count} at {Timestamp:HH:mm:ss}";
    }
}
