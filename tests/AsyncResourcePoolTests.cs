using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncResourcePool.Tests
{
    public class AsyncResourcePoolTests
    {
        private const int Timeout = 10000;

        [Fact]
        public async Task ShouldCreateExactlyMinNumberOfResources()
        {
            var testHarness = new TestHarness();

            const int minNumResources = 10;

            using (var sut = CreateSut(testHarness, minNumResources, minNumResources + 10))
            {
                await Task.Delay(100);
                Assert.Equal(minNumResources, testHarness.CreatedResources.Count);
            }
        }

        [Fact(Timeout = Timeout)]
        public async Task ShouldCreateResourcesWhenMinNumResourcesIsZero()
        {
            var testHarness = new TestHarness();

            const int minNumResources = 0;
            const int numResourcesToCreate = 5;

            using (var sut = CreateSut(testHarness, minNumResources, minNumResources + 10))
            {
                var tasks = Enumerable.Range(0, numResourcesToCreate)
                    .Select(_ => sut.Get());

                await Task.WhenAll(tasks);

                await Task.Delay(100);
                Assert.Equal(numResourcesToCreate, testHarness.CreatedResources.Count);
            }
        }

        /// <summary>
        /// Basically we want the pool to always maintain a pool of minNumResources as resources are handed out,
        /// up to a maximum of maxNumResources
        /// </summary>
        /// <param name="numResourcesToRetrieve"></param>
        /// <returns></returns>
        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(15)]
        public async Task Get_ShouldCauseAdditionalResourceCreation_WhenResourcesAreRetrieved_UpToMaxNumResources(int numResourcesToRetrieve)
        {
            const int minNumResources = 5;
            const int maxNumResources = 10;

            var minimumResourcesCreated = new TaskCompletionSource<bool>();
            var count = 0;
            Task<TestResource> ResourceFactory()
            {
                var value = Interlocked.Increment(ref count);
                var result = new TestResource(value);
                if (value == minNumResources)
                {
                    minimumResourcesCreated.SetResult(true);
                }

                return Task.FromResult(result);
            }

            var testHarness = new TestHarness(ResourceFactory);

            var expectedNumResourcesCreated = Math.Min(minNumResources + numResourcesToRetrieve, maxNumResources);

            using (var sut = CreateSut(testHarness, minNumResources, maxNumResources))
            {
                // Make sure we wait for all resources to be created initially. Without this, this test was
                // passing when the actual implementation was failing under real conditions.
                await minimumResourcesCreated.Task;

                for (var i = 0; i < numResourcesToRetrieve; ++i)
                {
                    // Don't await: once we get past maxNumResources, if we await, we'll be waiting an awful long time
                    sut.Get();
                }

                // Allow some time for the non-awaited tasks to run until they either finish or get stuck waiting
                await Task.Delay(100);

                Assert.Equal(expectedNumResourcesCreated, testHarness.CreatedResources.Count);
            }
        }

        [Fact(Timeout = Timeout)]
        public async Task Get_ShouldRetryResourceCreation_WhenMaxResourceCreationLimitNotReached()
        {
            var exception = new Exception("Deliberate resource creation failure");
            var count = 0;
            const int maxNumAttempts = 3;
            async Task<TestResource> FailingFactory()
            {
                // Only fail the first maxNumAttempts-1 times, THEN succeed.
                if (++count < maxNumAttempts)
                {
                    throw exception;
                }

                return new TestResource(count);
            }

            var testHarness = new TestHarness(FailingFactory);

            using (var sut = CreateSut(testHarness, 1, 1, maxNumResourceCreationAttempts: maxNumAttempts))
            {
                var reusableResource = await sut.Get();
                Assert.Equal(maxNumAttempts, reusableResource.Resource.Value);
            }
        }

        [Fact(Timeout = Timeout)]
        public async Task Get_ShouldThrowException_WhenResourceFactoryThrowsException()
        {
            var exception = new Exception("Expect to receive me from Get");
            
            const int maxNumAttempts = 3;
            async Task<TestResource> FailingFactory()
            {
                throw exception;
            }

            var testHarness = new TestHarness(FailingFactory);

            using (var sut = CreateSut(testHarness, 1, 1, maxNumResourceCreationAttempts: maxNumAttempts))
            {
                await Assert.ThrowsAsync<Exception>(() => sut.Get());
            }
        }

        [Fact(Timeout = Timeout)]
        public async Task ResourceShouldBeDisposedAfterExpiry()
        {
            var count = 0;
            var numDisposals = 0;

            async Task<TestResource> DisposeWatchFactory()
            {
                return new TestResource(++count, _ => Interlocked.Increment(ref numDisposals));
            }

            var testHarness = new TestHarness(DisposeWatchFactory);

            var expiry = TimeSpan.FromMilliseconds(1000);
            const int numIntervals = 3;

            using (var sut = CreateSut(testHarness, 1, 1, expiry))
            {
                // Wait for numIntervals plus a small amount
                await Task.Delay(expiry * (numIntervals + 0.5));

                Assert.Equal(numIntervals, numDisposals);
            }
        }

        [Fact(Timeout = Timeout)]
        public async Task ResourcePool_ShouldContainMinNumResources_AfterAdditionalResourcesExpire()
        {
            var count = 0;
            var numDisposals = 0;
            const int numRequestedResources = 8;

            var initialResourcesDisposedTaskCompletionSource = new TaskCompletionSource<bool>();

            async Task<TestResource> DisposeWatchFactory()
            {
                return new TestResource(++count, _ =>
                {
                    var num = Interlocked.Increment(ref numDisposals);
                    if (num == numRequestedResources)
                    {
                        initialResourcesDisposedTaskCompletionSource.SetResult(true);
                    }
                });
            }

            var testHarness = new TestHarness(DisposeWatchFactory);

            var expiry = TimeSpan.FromMilliseconds(1000);
            const int minNumResources = 5;

            using (var sut = CreateSut(testHarness, minNumResources, numRequestedResources, expiry))
            {
                // Request 8 resources - 3 over the minimum
                var reusableResources = await Task.WhenAll(Enumerable.Range(0, numRequestedResources).Select(_ => sut.Get()));
                foreach (var reusableResource in reusableResources)
                {
                    reusableResource.Dispose(); // Return it to the pool.
                }

                // Wait for the 8 resources to be disposed from expiry.
                await Task.Delay(expiry * 1.5);

                var numResourcesRemainingInPool = testHarness.CreatedResources.Count - numDisposals;
                Assert.Equal(minNumResources, numResourcesRemainingInPool);
            }
        }

        [Fact(Timeout = Timeout)]
        public async Task ResourcesAreReturnedInOrderOfTaskCompletion()
        {
            var delays = new[]
            {
                200,
                100,
                50,
                300
            };

            var index = 0;
            async Task<TestResource> ResourceFactory()
            {
                var delay = delays[index++];
                await Task.Delay(delay);
                return new TestResource(delay);
            }

            var testHarness = new TestHarness(ResourceFactory);

            using (var sut = CreateSut(testHarness, 2, delays.Length))
            {
                var resourceTasks = Enumerable.Range(0, delays.Length)
                    .Select(_ => sut.Get())
                    .ToList();

                var reusableResources = await Task.WhenAll(resourceTasks);
                var resources = reusableResources.Select(ru => ru.Resource).ToList();

                Assert.Equal(testHarness.CreatedResources, resources);
            }
        }

        [Fact(Timeout = Timeout)]
        public async Task AllResourcesShouldBeDisposedAfterConnectionPoolIsDisposedOnceReusableResourceIsDisposed()
        {
            var testHarness = new TestHarness();
            
            ReusableResource<TestResource> reusableResource;
            using (var sut = CreateSut(testHarness, 3, 5))
            {
                reusableResource = await sut.Get();
                
                // Dispose the pool *before* we dispose reusableResource.
            }

            reusableResource.Dispose();

            // The pool should still handle disposal of the resource wrapped by the disposed
            // ReusableResource even when the pool itself has already been disposed.
            Assert.All(testHarness.CreatedResources, resource => Assert.True(resource.IsDisposed));
        }

        [Fact(Timeout = Timeout)]
        public async Task ResourceShouldNotBeDisposedAfterConnectionPoolIsDisposedIfReusableResourceIsNotDisposed()
        {
            var testHarness = new TestHarness();
            ReusableResource<TestResource> reusableResource;
            using (var sut = CreateSut(testHarness, 1, 1))
            {
                reusableResource = await sut.Get();

                // Dispose the pool *without* ever disposing reusableResource.
            }

            // The inner resource wrapped by the ReusableResource should still be alive
            Assert.False(reusableResource.Resource.IsDisposed);
        }

        [Fact(Timeout = Timeout)]
        public async Task SlowDisposingResources_ShouldNotBlock()
        {
            var count = 0;
            var disposalDelay = TimeSpan.FromMilliseconds(1000);
            var expiryTime = TimeSpan.FromMilliseconds(500);

            var firstResourceFinishedDisposing = new TaskCompletionSource<bool>();
            var secondResourceCreated = new TaskCompletionSource<bool>();

            async Task<TestResource> SlowDisposingResourceFactory()
            {
                var countClosure = count;

                if (count == 1)
                {
                    secondResourceCreated.SetResult(true);
                }

                return new TestResource(++count, _ =>
                {
                    Thread.Sleep(disposalDelay);
                    if (countClosure == 0)
                    {
                        firstResourceFinishedDisposing.SetResult(true);
                    }
                });
            }

            var testHarness = new TestHarness(SlowDisposingResourceFactory);

            using (var sut = CreateSut(testHarness, 1, 1, expiry: expiryTime))
            {
                // Allow time for the first automatically created resource to expire. It should be re-created automatically
                // by the expiry purge handler.
                await Task.Delay(expiryTime * 1.1);
            }

            var firstActualTask = await Task.WhenAny(firstResourceFinishedDisposing.Task, secondResourceCreated.Task);

            var firstExpectedTask = secondResourceCreated.Task;

            Assert.Equal(firstExpectedTask, firstActualTask);
        }

        private AsyncResourcePool<TestResource> CreateSut(TestHarness testHarness,
            int minNumResources,
            int maxNumResources,
            TimeSpan? expiry = null,
            int maxNumResourceCreationAttempts = AsyncResourcePoolOptions.DefaultNumResourceCreationRetries,
            TimeSpan? resourceCreationRetryInterval = null)
        {
            var options = new AsyncResourcePoolOptions(
                minNumResources,
                maxNumResources: maxNumResources,
                resourcesExpireAfter: expiry,
                maxNumResourceCreationAttempts: maxNumResourceCreationAttempts,
                resourceCreationRetryInterval: resourceCreationRetryInterval ?? TimeSpan.FromMilliseconds(10));
            return new AsyncResourcePool<TestResource>(testHarness.ResourceFactory, options);
        }

        private class TestHarness
        {
            private readonly int _delay;
            private int _value = 1;
            private readonly Func<Task<TestResource>> _resourceFactory;

            private readonly List<TestResource> _createdResources = new List<TestResource>();

            public TestHarness(Func<Task<TestResource>> resourceFactory, int delay = 0)
            {
                _resourceFactory = resourceFactory;
                _delay = delay;
            }

            public TestHarness(int delay = 0)
            {
                _resourceFactory = DefaultResourceFactory;
                _delay = delay;
            }

            private int GetNextValue()
            {
                return _value++;
            }

            public IReadOnlyCollection<TestResource> CreatedResources => _createdResources;

            public Func<Task<TestResource>> ResourceFactory => LogResource;

            private async Task<TestResource> LogResource()
            {
                var resource = await _resourceFactory();

                _createdResources.Add(resource);

                return resource;
            }

            private async Task<TestResource> DefaultResourceFactory()
            {
                await Task.Delay(_delay);

                var value = GetNextValue();
                return new TestResource(value);
            }
        }

        private class TestResource : IDisposable
        {
            private readonly Action<TestResource> _disposeAction;

            public TestResource(int value, Action<TestResource> disposeAction = null)
            {
                _disposeAction = disposeAction;
                Value = value;
            }

            public int Value { get; }

            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                IsDisposed = true;
                _disposeAction?.Invoke(this);

            }
        }
    }
}
