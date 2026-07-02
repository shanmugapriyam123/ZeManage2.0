using System;
using BIManage.Infrastructure.DependencyInjection;

namespace BIManage.Revit.Applications
{
    /// <summary>
    ///     Represents the outcome of the bootstrap process.
    /// </summary>
    public class InitResult
    {
        private InitResult() { }

        public bool IsSuccessful { get; private set; }
        public string? ErrorMessage { get; private set; }
        public Exception? Exception { get; private set; }
        public ServiceRegistry? Services { get; private set; }

        public static InitResult Success(ServiceRegistry services)
        {
            return new InitResult
            {
                IsSuccessful = true,
                Services = services
            };
        }

        public static InitResult Failure(string message, Exception? exception = null)
        {
            return new InitResult
            {
                IsSuccessful = false,
                ErrorMessage = message,
                Exception = exception
            };
        }
    }
}
