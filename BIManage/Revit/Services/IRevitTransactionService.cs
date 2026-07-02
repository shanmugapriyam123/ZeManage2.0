using System;
using Autodesk.Revit.DB;

namespace BIManage.Revit.Services
{
    /// <summary>
    ///     Interface for providing safe transaction helpers.
    /// </summary>
    public interface IRevitTransactionService
    {
        /// <summary>
        ///     Run an action within a transaction. Automatically commits on success, rolls back on exception.
        /// </summary>
        /// <param name="document">The document to run the transaction in</param>
        /// <param name="name">The transaction name</param>
        /// <param name="action">The action to execute within the transaction</param>
        void Run(Document document, string name, Action<Transaction> action);
    }
}
