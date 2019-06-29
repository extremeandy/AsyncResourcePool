using System;

namespace AsyncResourcePool
{
    public sealed class AsyncResourcePoolOptions
    {
        public static readonly TimeSpan DefaultResourceCreationRetryInterval = TimeSpan.FromSeconds(1);
        public const int DefaultNumResourceCreationRetries = 3;

        /// <param name="minNumResources"></param>
        /// <param name="maxNumResources"></param>
        /// <param name="resourcesExpireAfter">Resources will not expire if this is not set</param>
        /// <param name="maxNumResourceCreationAttempts"></param>
        /// <param name="resourceCreationRetryInterval"></param>
        public AsyncResourcePoolOptions(
            int minNumResources,
            int maxNumResources = int.MaxValue,
            TimeSpan? resourcesExpireAfter = null,
            int maxNumResourceCreationAttempts = DefaultNumResourceCreationRetries,
            TimeSpan? resourceCreationRetryInterval = null)
        {
            if (minNumResources < 0)
            {
                throw new ArgumentException($"{nameof(minNumResources)} must be >= 0");
            }

            if (maxNumResources < 1)
            {
                throw new ArgumentException($"{nameof(maxNumResources)} must be > 0");
            }

            if (minNumResources > maxNumResources)
            {
                throw new ArgumentException($"{nameof(minNumResources)} must be <= {nameof(maxNumResources)}");
            }

            MinNumResources = minNumResources;
            MaxNumResources = maxNumResources;
            ResourcesExpireAfter = resourcesExpireAfter;
            MaxNumResourceCreationAttempts = maxNumResourceCreationAttempts;
            ResourceCreationRetryInterval = resourceCreationRetryInterval ?? DefaultResourceCreationRetryInterval;
        }

        public int MinNumResources { get; }

        public int MaxNumResources { get; }

        public TimeSpan? ResourcesExpireAfter { get; }

        public int MaxNumResourceCreationAttempts { get; }

        public TimeSpan ResourceCreationRetryInterval { get; }
    }
}
