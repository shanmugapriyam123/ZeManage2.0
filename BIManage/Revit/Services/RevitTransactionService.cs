using System;
using Autodesk.Revit.DB;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Services
{
    /// <summary>
    ///     Provides safe transaction helpers.
    /// </summary>
    public class RevitTransactionService : IRevitTransactionService
    {
        private readonly ILogger? _logger;

        public RevitTransactionService(ILogger? logger)
        {
            _logger = logger;
        }

        public void Run(Document document, string name, Action<Transaction> action)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (action == null) throw new ArgumentNullException(nameof(action));

            using var transaction = new Transaction(document, name);

            transaction.Start();
            _logger?.LogDebug($"Transaction started: {name}");

            try
            {
                action(transaction);
                transaction.Commit();
                _logger?.LogInfo($"Transaction committed: {name}");
            }
            catch (Exception ex)
            {
                transaction.RollBack();
                _logger?.LogError($"Transaction rolled back: {name} - {ex.Message}", ex);
                throw;
            }
        }
    }
}
