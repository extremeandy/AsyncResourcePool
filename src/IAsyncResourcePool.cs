using System;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncResourcePool
{
    public interface IAsyncResourcePool<TResource> : IDisposable
    {
        Task<ReusableResource<TResource>> Get(CancellationToken cancellationToken = default);
    }
}