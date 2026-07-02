using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Session
{
    /// <summary>
    ///     Manages per-document sessions across document lifecycle
    /// </summary>
    public class DocumentSessionManager : IDocumentSessionManager
    {
        private readonly ILogger? _logger;
        private readonly Dictionary<string, DocumentSession> _sessions;
        private bool _disposed;

        public DocumentSessionManager(ILogger? logger)
        {
            _logger = logger;
            _sessions = new Dictionary<string, DocumentSession>();
        }

        /// <summary>
        ///     Create a new session when document opens
        /// </summary>
        public DocumentSession CreateSession(Document document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            var key = GetDocumentKey(document);

            // Clean up existing session if any
            if (_sessions.TryGetValue(key, out var existing))
            {
                _logger?.LogWarning($"Session already exists for document: {document.Title}. Disposing old session.");
                existing.Dispose();
                _sessions.Remove(key);
            }

            var session = new DocumentSession(document);
            _sessions[key] = session;

            _logger?.LogInfo($"Document session created: {document.Title} (Workshared: {session.IsWorkshared}, Cloud: {session.IsCloud})");

            return session;
        }

        /// <summary>
        ///     Get active session for a document
        /// </summary>
        public DocumentSession? GetSession(Document document)
        {
            if (document == null) return null;

            var key = GetDocumentKey(document);
            return _sessions.TryGetValue(key, out var session) ? session : null;
        }

        /// <summary>
        ///     Close and dispose session when document closes
        /// </summary>
        public void CloseSession(Document document)
        {
            if (document == null) return;

            var key = GetDocumentKey(document);
            if (_sessions.TryGetValue(key, out var session))
            {
                var duration = DateTime.UtcNow - session.OpenedAt;
                _logger?.LogInfo($"Document session closed: {document.Title} (Duration: {duration.TotalMinutes:F1} minutes, ChangeSets: {session.ChangeSetHistory.Count})");

                session.Dispose();
                _sessions.Remove(key);
            }
        }

        /// <summary>
        ///     Get all active sessions
        /// </summary>
        public IEnumerable<DocumentSession> GetAllSessions()
        {
            return _sessions.Values;
        }

        /// <summary>
        ///     Get document key for dictionary lookup
        ///     Uses document hash code for stable per-instance identification
        /// </summary>
        private string GetDocumentKey(Document document)
        {
            try
            {
                // Use document hash code (stable per document instance)
                var guid = document.GetHashCode();
                return guid.ToString();
            }
            catch
            {
                // Fallback: use title + path hash
                return $"{document.Title}_{document.PathName?.GetHashCode() ?? 0}";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();

            _logger?.LogInfo("DocumentSessionManager disposed");
            _disposed = true;
        }
    }
}
