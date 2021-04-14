using System;

namespace AsyncResourcePool
{
    public static class AsyncResourcePoolExtensions
    {
        /// <summary>
        /// Tries to add the resource to the pool. If adding fails (because the pool is already full), then dispose the resource.
        /// </summary>
        /// <typeparam name="TResource"></typeparam>
        /// <param name="resourcePool"></param>
        /// <param name="resource"></param>
        public static void AddOrDispose<TResource>(this IAsyncResourcePool<TResource> resourcePool, TResource resource)
            where TResource : IDisposable
        {
            if (!resourcePool.TryAdd(resource))
            {
                resource.Dispose();
            }
        }
    }
}