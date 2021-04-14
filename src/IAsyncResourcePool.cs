using System;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncResourcePool
{
    public interface IAsyncResourcePool<TResource> : IDisposable
    {
        Task<ReusableResource<TResource>> Get(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns <see langword="true"/> if a resource is immediately available and <see langword="false" /> if not.
        /// If one is available, it is output in <paramref name="reusableResource"/>.
        /// </summary>
        /// <param name="reusableResource"></param>
        /// <returns></returns>
        bool TryGetExisting(out ReusableResource<TResource> reusableResource);

        /// <summary>
        /// Returns <see langword="true"/> if a resource is immediately available and <see langword="false" /> if not.
        /// If one is available, it is output in <paramref name="resource"/> and the resource is permanently removed from the pool.
        /// </summary>
        /// <param name="resource"></param>
        /// <returns></returns>
        bool TryRemove(out TResource resource);

        /// <summary>
        /// Tries to add a resource to the pool.
        /// Returns <see langword="true"/> if add succeeded (i.e. the pool was not full) and <see langword="false" /> if not (i.e. the pool was full).
        /// </summary>
        /// <param name="resource"></param>
        /// <returns></returns>
        bool TryAdd(TResource resource);
    }
}