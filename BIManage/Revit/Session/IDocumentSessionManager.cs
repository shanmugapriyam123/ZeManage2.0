using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace BIManage.Revit.Session
{
    /// <summary>
    ///     Interface for document session lifecycle management
    /// </summary>
    public interface IDocumentSessionManager : IDisposable
    {
        /// <summary>
        ///     Create a new session when document opens
        /// </summary>
        DocumentSession CreateSession(Document document);

        /// <summary>
        ///     Get active session for a document
        /// </summary>
        DocumentSession? GetSession(Document document);

        /// <summary>
        ///     Close and dispose session when document closes
        /// </summary>
        void CloseSession(Document document);

        /// <summary>
        ///     Get all active sessions
        /// </summary>
        IEnumerable<DocumentSession> GetAllSessions();
    }
}
