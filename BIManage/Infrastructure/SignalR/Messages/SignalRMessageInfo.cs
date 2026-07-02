using System;
using System.Text.Json;

namespace BIManage.Infrastructure.SignalR.Messages
{
    /// <summary>
    /// Base wrapper class for all SignalR messages
    /// </summary>
    public class SignalRMessageInfo
    {
        /// <summary>
        /// SignalR method name (matches the wire method name from the API).
        /// Use constants from <see cref="SignalRMethods"/>.
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// JSON serialized payload data
        /// </summary>
        public string Payload { get; set; }

        /// <summary>
        /// Timestamp when the message was created/sent
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Session ID of the sender
        /// </summary>
        public string SenderSessionId { get; set; }

        /// <summary>
        /// User ID of the sender
        /// </summary>
        public string SenderUserId { get; set; }

        /// <summary>
        /// Username of the sender for display
        /// </summary>
        public string SenderUsername { get; set; }

        /// <summary>
        /// Target group for this message
        /// </summary>
        public string TargetGroup { get; set; }

        /// <summary>
        /// Model GUID if message is model-specific
        /// </summary>
        public string ModelGuid { get; set; }

        /// <summary>
        /// Project ID if message is project-specific
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// Company ID if message is company-specific
        /// </summary>
        public string CompanyId { get; set; }

        /// <summary>
        /// Unique message identifier
        /// </summary>
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Deserializes the payload to the specified type
        /// </summary>
        /// <typeparam name="T">Target type for deserialization</typeparam>
        /// <returns>Deserialized payload or default if empty/null</returns>
        public T GetPayload<T>() where T : class
        {
            if (string.IsNullOrEmpty(Payload))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(Payload, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets the payload from an object
        /// </summary>
        /// <typeparam name="T">Type of the payload object</typeparam>
        /// <param name="payload">Payload object to serialize</param>
        public void SetPayload<T>(T payload) where T : class
        {
            if (payload == null)
            {
                Payload = null;
                return;
            }

            Payload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        /// <summary>
        /// Creates a new message with the specified method and payload
        /// </summary>
        public static SignalRMessageInfo Create<T>(string method, T payload) where T : class
        {
            var message = new SignalRMessageInfo
            {
                Method = method,
                Timestamp = DateTime.UtcNow
            };
            message.SetPayload(payload);
            return message;
        }

        /// <summary>
        /// Creates a new message with just a method (no payload)
        /// </summary>
        public static SignalRMessageInfo Create(string method)
        {
            return new SignalRMessageInfo
            {
                Method = method,
                Timestamp = DateTime.UtcNow
            };
        }

        public override string ToString()
        {
            return $"[{Method}] {MessageId} from {SenderUsername ?? SenderUserId ?? "unknown"} at {Timestamp:HH:mm:ss}";
        }
    }
}
