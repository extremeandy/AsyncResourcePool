using System;

namespace AsyncResourcePool
{
    /// <summary>
    /// A wrapper for a <see cref="TResource"/> that can be re-used by <see cref="AsyncResourcePool{TResource}"/>
    /// when disposed.
    /// 
    /// Be careful not to dispose the inner <see cref="Resource">; we don't want to add disposed
    /// resources back to the resource pool!
    /// </summary>
    /// <typeparam name="TResource"></typeparam>
    public sealed class ReusableResource<TResource> : IDisposable
    {
        private readonly Action _disposeAction;

        public ReusableResource(TResource resource, Action disposeAction)
        {
            Resource = resource;
            _disposeAction = disposeAction;
        }

        public TResource Resource { get; }

        public void Dispose() => _disposeAction();
    }
}
