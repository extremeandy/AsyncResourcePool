using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace AsyncResourcePool
{
    public sealed class AsyncResourcePool<TResource> : IAsyncResourcePool<TResource>
    {
        private readonly int _minNumResources;
        private readonly int _maxNumResources;
        private readonly TimeSpan? _resourcesExpireAfter;
        private readonly int _maxNumResourceCreationAttempts;
        private readonly TimeSpan _resourceCreationRetryInterval;
        private readonly Func<Task<TResource>> _resourceTaskFactory;
        private readonly Queue<TimestampedResource> _availableResources;
        private readonly Queue<ResourceRequestMessage> _pendingResourceRequests;
        private readonly ActionBlock<IResourceMessage> _messageHandler;

        /// <summary>
        /// The total number of resources produced, including available resources and resources
        /// that are currently in use
        /// </summary>
        private int _numResources = 0;
        private int _numResourcesInUse = 0;

        /// <summary>
        /// This constructor should be used only if asynchronous resource creation is not available
        /// </summary>
        /// <param name="resourceFactory">Function to create a resource</param>
        /// <param name="options"></param>
        public AsyncResourcePool(Func<TResource> resourceFactory, AsyncResourcePoolOptions options)
            : this(() => Task.FromResult(resourceFactory()), options)
        {
        }

        /// <summary>
        /// This constructor should be used for asynchronous resource creation
        /// </summary>
        /// <param name="resourceTaskFactory">Function to create a task that returns a resource</param>
        /// <param name="options"></param>
        public AsyncResourcePool(Func<Task<TResource>> resourceTaskFactory, AsyncResourcePoolOptions options)
        {
            _minNumResources = options.MinNumResources;
            _maxNumResources = options.MaxNumResources;
            _resourcesExpireAfter = options.ResourcesExpireAfter;
            _maxNumResourceCreationAttempts = options.MaxNumResourceCreationAttempts;
            _resourceCreationRetryInterval = options.ResourceCreationRetryInterval;
            _availableResources = new Queue<TimestampedResource>();
            _pendingResourceRequests = new Queue<ResourceRequestMessage>();
            _resourceTaskFactory = resourceTaskFactory;

            // Important: These functions must be called after all instance members have been initialised!
            _messageHandler = GetMessageHandler();
            SetupErrorHandling();
            SetupPeriodicPurge();

            _messageHandler.Post(new EnsureAvailableResourcesMessage());
        }

        private ActionBlock<IResourceMessage> GetMessageHandler()
        {
            return new ActionBlock<IResourceMessage>(message =>
            {
                switch (message)
                {
                    case ResourceRequestMessage resourceRequest:
                        _pendingResourceRequests.Enqueue(resourceRequest);
                        HandlePendingResourceRequests();
                        break;
                    case ResourceAvailableMessage resourceAvailableMessage:
                        HandleResourceAvailable(resourceAvailableMessage);

                        // A new resource is available, so handle any pending requests
                        HandlePendingResourceRequests();
                        break;
                    case PurgeExpiredResourcesMessage purgeExpiredResources:
                        HandlePurgeExpiredResource(purgeExpiredResources);
                        break;
                    case EnsureAvailableResourcesMessage ensureAvailableResourcesMessage:
                        HandleEnsureAvailableResourcesMessage(ensureAvailableResourcesMessage);
                        break;
                    case CreateResourceFailedMessage createResourceFailedMessage:
                        // TODO: Log the exception!
                        if (createResourceFailedMessage.AttemptNumber >= _maxNumResourceCreationAttempts)
                        {
                            ClearAllPendingRequests(createResourceFailedMessage.Exception);
                        }
                        else
                        {
                            Task.Run(async () =>
                            {
                                await Task.Delay(_resourceCreationRetryInterval);
                                var nextAttemptNumber = createResourceFailedMessage.AttemptNumber + 1;
                                _messageHandler.Post(new EnsureAvailableResourcesMessage(nextAttemptNumber));
                            });
                        }

                        break;
                    default:
                        throw new InvalidOperationException($"Unhandled {nameof(message)} type: {message.GetType()}");
                }
            });
        }

        private async void SetupErrorHandling()
        {
            try
            {
                await _messageHandler.Completion;
            }
            catch (Exception ex)
            {
                ClearAllPendingRequests(ex);
            }
        }

        private void ClearAllPendingRequests(Exception ex)
        {
            while (_pendingResourceRequests.Count > 0)
            {
                var request = _pendingResourceRequests.Dequeue();
                request.TaskCompletionSource.SetException(ex);
            }
        }

        /// <summary>
        /// Periodically purge expired resources from _availableResources
        /// </summary>
        private async void SetupPeriodicPurge()
        {
            if (_resourcesExpireAfter != null)
            {
                // Run the clean-up 10X as often as resources will expire. This will ensure that we
                // keep the number of resources that have expired in the queue to a minimum.
                const int frequency = 10;
                var interval = new TimeSpan(_resourcesExpireAfter.Value.Ticks / frequency);
                var timer = new Timer(
                    _ => _messageHandler.Post(PurgeExpiredResourcesMessage.Instance),
                    null,
                    interval,
                    interval);

                try
                {
                    await _messageHandler.Completion;
                }
                finally
                {
                    timer.Dispose();
                }
            }
            
        }

        private ReusableResource<TResource> TryGetReusableResource()
        {
            ReusableResource<TResource> reusableResource = null;
            while (_availableResources.Count > 0 && reusableResource is null)
            {
                var timestampedResource = _availableResources.Dequeue();
                var resource = timestampedResource.Resource;
                if (IsResourceExpired(timestampedResource))
                {
                    DisposeResource(resource);
                }
                else
                {
                    reusableResource = GetReusableResource(resource);
                }
            }

            _messageHandler.Post(new EnsureAvailableResourcesMessage());

            return reusableResource;
        }

        private void HandlePendingResourceRequests()
        {
            while (_pendingResourceRequests.Count > 0)
            {
                if (_pendingResourceRequests.Peek().CancellationToken.IsCancellationRequested)
                {
                    _pendingResourceRequests.Dequeue(); // Throw away cancelled requests
                    continue;
                }

                var result = TryGetReusableResource();
                if (result != null)
                {
                    var request = _pendingResourceRequests.Dequeue();
                    request.TaskCompletionSource.SetResult(result);
                }
                else
                {
                    break;
                }
            }
        }

        private void HandleResourceAvailable(ResourceAvailableMessage resourceAvailableMessage)
        {
            var resource = resourceAvailableMessage.Resource;
            var timestampedResource = TimestampedResource.Create(resource);
            _availableResources.Enqueue(timestampedResource);
        }

        private void HandlePurgeExpiredResource(PurgeExpiredResourcesMessage purgeExpiredResourcesMessage)
        {
            var nonExpiredResources = new List<TimestampedResource>();
            while (_availableResources.Count > 0)
            {
                var timestampedResource = _availableResources.Dequeue();
                if (IsResourceExpired(timestampedResource))
                {
                    DisposeResource(timestampedResource.Resource);
                }
                else
                {
                    nonExpiredResources.Add(timestampedResource);
                }
            }

            foreach (var timestampedResource in nonExpiredResources)
            {
                _availableResources.Enqueue(timestampedResource);
            }

            _messageHandler.Post(new EnsureAvailableResourcesMessage());
        }

        /// <summary>
        /// This function seeds resource production with _minNumResources
        /// </summary>
        private async void HandleEnsureAvailableResourcesMessage(EnsureAvailableResourcesMessage ensureAvailableResourcesMessage)
        {
            var effectiveNumResourcesAvailable = _numResources - _numResourcesInUse;
            var numResourcesRequired = Math.Max(_minNumResources, _pendingResourceRequests.Count);
            var availableResourcesGap = numResourcesRequired - effectiveNumResourcesAvailable;
            var remainingCapacity = _maxNumResources - _numResources;
            var numResourcesToCreate = Math.Max(0, Math.Min(availableResourcesGap, remainingCapacity));

            var createResourceTasks = Enumerable.Range(0, numResourcesToCreate)
                .Select(_ => TryCreateResource());

            try
            {
                await Task.WhenAll(createResourceTasks);
            }
            catch (Exception ex)
            {
                var errorMessage = new CreateResourceFailedMessage(ex, ensureAvailableResourcesMessage.AttemptNumber);
                _messageHandler.Post(errorMessage);
            }
        }

        private async Task TryCreateResource()
        {
            var resourceTask = _resourceTaskFactory();
            try
            {
                // Increment before we wait for the task. Otherwise, while
                // waiting, another thread may read the incorrect value.
                Interlocked.Increment(ref _numResources);

                var resource = await resourceTask;

                MakeResourceAvailable(resource);
            }
            catch (Exception)
            {
                // Roll back!
                Interlocked.Decrement(ref _numResources);
                throw;
            }
        }

        private void MakeResourceAvailable(TResource resource)
        {
            var resourceAvailableMessage = new ResourceAvailableMessage(resource);
            if (!_messageHandler.Post(resourceAvailableMessage))
            {
                DisposeResource(resource);
            }
        }

        private bool IsResourceExpired(TimestampedResource timestampedResource)
        {
            return _resourcesExpireAfter != null
                && timestampedResource.Created.Add(_resourcesExpireAfter.Value) < DateTime.Now;
        }

        private ReusableResource<TResource> GetReusableResource(TResource resource)
        {
            Interlocked.Increment(ref _numResourcesInUse);
            return new ReusableResource<TResource>(resource, () =>
            {
                Interlocked.Decrement(ref _numResourcesInUse);
                MakeResourceAvailable(resource);
            });
        }

        public Task<ReusableResource<TResource>> Get(CancellationToken cancellationToken = default)
        {
            var taskCompletionSource = new TaskCompletionSource<ReusableResource<TResource>>();
            var request = new ResourceRequestMessage(taskCompletionSource, cancellationToken);

            if (!_messageHandler.Post(request))
            {
                taskCompletionSource.SetException(GetObjectDisposedException());
            }

            return taskCompletionSource.Task;
        }

        private async void DisposeResource(TResource resource)
        {
            Interlocked.Decrement(ref _numResources);

            if (resource is IDisposable disposableResource)
            {
                await Task.Run(() => { disposableResource.Dispose(); });
            }
        }

        public async void Dispose()
        {
            _messageHandler.Complete();
            await _messageHandler.Completion; // Even after we mark Complete, still need to finish processing.

            // Clean up any remaining requests.
            while (_pendingResourceRequests.Count > 0)
            {
                var request = _pendingResourceRequests.Dequeue();
                request.TaskCompletionSource.SetException(GetObjectDisposedException());
            }

            // Clean up any remaining resources.
            while (_availableResources.Count > 0)
            {
                var timestampedResource = _availableResources.Dequeue();
                DisposeResource(timestampedResource.Resource);
            }
        }

        private ObjectDisposedException GetObjectDisposedException() =>
            new ObjectDisposedException($"Requested a resource on a disposed {nameof(AsyncResourcePool<TResource>)}");

        private sealed class ResourceRequestMessage : IResourceMessage
        {
            public ResourceRequestMessage(TaskCompletionSource<ReusableResource<TResource>> taskCompletionSource, CancellationToken cancellationToken)
            {
                TaskCompletionSource = taskCompletionSource;
                CancellationToken = cancellationToken;
            }

            public TaskCompletionSource<ReusableResource<TResource>> TaskCompletionSource { get; }

            public CancellationToken CancellationToken { get; }
        }

        private sealed class ResourceAvailableMessage : IResourceMessage
        {
            public ResourceAvailableMessage(TResource resource)
            {
                Resource = resource;
            }

            public TResource Resource { get; }
        }

        private sealed class PurgeExpiredResourcesMessage : IResourceMessage
        {
            private PurgeExpiredResourcesMessage()
            {
            }

            public static readonly PurgeExpiredResourcesMessage Instance = new PurgeExpiredResourcesMessage();
        }

        private sealed class EnsureAvailableResourcesMessage : IResourceMessage
        {
            public EnsureAvailableResourcesMessage(int attemptNumber = 1)
            {
                AttemptNumber = attemptNumber;
            }

            public int AttemptNumber { get; }
        }

        private sealed class CreateResourceFailedMessage : IResourceMessage
        {
            public CreateResourceFailedMessage(Exception exception, int attemptNumber)
            {
                Exception = exception;
                AttemptNumber = attemptNumber;
            }

            public Exception Exception { get; }

            public int AttemptNumber { get; }
        }

        private interface IResourceMessage
        {
        }

        private readonly struct TimestampedResource
        {
            private TimestampedResource(TResource resource, DateTime created)
            {
                Resource = resource;
                Created = created;
            }

            public TResource Resource { get; }

            public DateTime Created { get; }

            public static TimestampedResource Create(TResource resource)
            {
                return new TimestampedResource(resource, DateTime.Now);
            }
        }
    }
}