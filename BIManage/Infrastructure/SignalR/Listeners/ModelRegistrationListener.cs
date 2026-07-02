using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Listens for ModelRegistered / ModelDeregistered messages from the API
    /// and upserts the local SQLite registered_models table.
    /// </summary>
    public class ModelRegistrationListener : SignalListenerBase
    {
        private readonly RegisteredModelsRepository _repository;
        private readonly ISignalREventBus _eventBus;

        public override string Name => "ModelRegistration";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.ModelRegistered,
            SignalRMethods.ModelDeregistered,
            SignalRMethods.ModelSettingsChanged
        };

        public ModelRegistrationListener(
            RegisteredModelsRepository repository,
            ISignalREventBus eventBus,
            ILogger logger) : base(logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        protected override async Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            if (message.Method == SignalRMethods.ModelRegistered)
            {
                await HandleModelRegisteredAsync(message);
            }
            else if (message.Method == SignalRMethods.ModelDeregistered)
            {
                await HandleModelDeregisteredAsync(message);
            }
            else if (message.Method == SignalRMethods.ModelSettingsChanged)
            {
                await HandleModelSettingsChangedAsync(message);
            }
        }

        private async Task HandleModelRegisteredAsync(SignalRMessageInfo message)
        {
            var payload = GetPayloadSafe<ModelRegisteredPayload>(message);
            if (payload == null)
            {
                _logger?.LogWarning("[ModelRegistration] Failed to deserialize ModelRegistered payload");
                return;
            }

            var modelGuid = payload.ModelGuid.ToString();
            var existing = await _repository.GetModelAsync(modelGuid);
            bool isNew = existing == null;

            var model = new RegisteredModel
            {
                ModelGuid = modelGuid,
                ModelName = payload.ModelName,
                CentralModelPath = payload.CentralModelPath,
                ProjectName = payload.LocalProjectName,
                ZemanageProjectId = payload.ZemanageProjectId?.ToString(),
                IsActive = payload.IsActive,
                RegisteredAt = payload.RegisteredAt ?? DateTime.UtcNow,
                RegisteredBy = payload.RegisteredBy,
                IsWorkshared = payload.IsWorkshared,
                IsFamily = payload.IsFamily,
                IsCloudModel = payload.IsCloudModel,
                Notes = payload.Notes,
                LastOpenedAt = payload.LastOpenedAt,
                LastOpenedBy = payload.LastOpenedBy
            };

            bool success;
            if (isNew)
            {
                success = await _repository.RegisterModelAsync(model);
                _logger?.LogInfo($"[ModelRegistration] Inserted new model: {model.ModelName} ({modelGuid})");
            }
            else
            {
                success = await _repository.UpdateModelAsync(model);
                _logger?.LogInfo($"[ModelRegistration] Updated existing model: {model.ModelName} ({modelGuid})");
            }

            if (success)
            {
                _eventBus.Publish(new ModelRegisteredEvent
                {
                    ModelGuid = modelGuid,
                    ModelName = model.ModelName,
                    IsActive = model.IsActive,
                    IsNew = isNew,
                    Source = Name
                });
            }
        }

        private async Task HandleModelDeregisteredAsync(SignalRMessageInfo message)
        {
            var payload = GetPayloadSafe<ModelRegisteredPayload>(message);
            if (payload == null)
            {
                _logger?.LogWarning("[ModelRegistration] Failed to deserialize ModelDeregistered payload");
                return;
            }

            var modelGuid = payload.ModelGuid.ToString();
            var existing = await _repository.GetModelAsync(modelGuid);
            if (existing == null)
            {
                _logger?.LogDebug($"[ModelRegistration] Deregister ignored - model not in local DB: {modelGuid}");
                return;
            }

            existing.IsActive = false;
            existing.Notes = payload.DeactivationReason ?? existing.Notes;

            var success = await _repository.UpdateModelAsync(existing);
            if (success)
            {
                _logger?.LogInfo($"[ModelRegistration] Deactivated model: {existing.ModelName} ({modelGuid})");

                _eventBus.Publish(new ModelRegisteredEvent
                {
                    ModelGuid = modelGuid,
                    ModelName = existing.ModelName,
                    IsActive = false,
                    IsNew = false,
                    Source = Name
                });
            }
        }

        private async Task HandleModelSettingsChangedAsync(SignalRMessageInfo message)
        {
            var payload = GetPayloadSafe<ModelRegisteredPayload>(message);
            if (payload == null)
            {
                _logger?.LogWarning("[ModelRegistration] Failed to deserialize ModelSettingsChanged payload");
                return;
            }

            var modelGuid = payload.ModelGuid.ToString();
            var existing = await _repository.GetModelAsync(modelGuid);
            if (existing == null)
            {
                _logger?.LogDebug($"[ModelRegistration] ModelSettingsChanged ignored — model not in local DB: {modelGuid}");
                return;
            }

            existing.ModelName = payload.ModelName ?? existing.ModelName;
            existing.CentralModelPath = payload.CentralModelPath ?? existing.CentralModelPath;
            existing.Notes = payload.Notes ?? existing.Notes;
            existing.IsActive = payload.IsActive;

            var success = await _repository.UpdateModelAsync(existing);
            if (success)
            {
                _logger?.LogInfo($"[ModelRegistration] Updated model settings: {existing.ModelName} ({modelGuid})");

                _eventBus.Publish(new ModelRegisteredEvent
                {
                    ModelGuid = modelGuid,
                    ModelName = existing.ModelName,
                    IsActive = existing.IsActive,
                    IsNew = false,
                    Source = Name
                });
            }
        }

    }
}
