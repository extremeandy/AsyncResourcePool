# AsyncResourcePool

> Thread-safe, non-blocking, managed pool of re-usable resources, with a specified minimum and optional maximum number of resources, and optional expiry time for resources to be cleaned up if not used for a time.

## Installation

```
dotnet add package AsyncResourcePool
```

## Options

Behaviour of `AsyncResourcePool` can be specified using `AsyncResourcePoolOptions`.

| Property                         | Default        | Description                                                                                                                                                                                                                                                               |
| -------------------------------- | -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MinNumResources`                | N/A            | The minimum number of resources that will be maintained by the pool. This number of resources will be created regardless of whether or not they are requested. If a resource is requested an allocated, an additional resource will be created to maintain the pool size. |
| `MaxNumResources`                | `int.MaxValue` | The maximum number of resources that the pool is allowed to create.                                                                                                                                                                                                       |
| `ResourcesExpireAfter`           | `null`         | If a resource is unused for this time, it will be dispsosed. If this causes the number of available resources to drop below the minimum, additional resources will be created to replace the disposed ones.                                                               |
| `MaxNumResourceCreationAttempts` | `3`            | Maximum number of attempts for creating a resource before an exception is thrown and passed back to the requestor                                                                                                                                                         |
| `ResourceCreationRetryInterval`  | 1 second       | Amount of time to wait after a failed resource creation attempt before trying again                                                                                                                                                                                       |

## Example usage: Connection Pool for Snowflake Connector for .NET

https://github.com/snowflakedb/snowflake-connector-net

   1. Define a `ConnectionPool` class which consumes `AsyncResourcePool` internally

```csharp
public sealed class ConnectionPool
{
    private readonly IAsyncResourcePool<SnowflakeDbConnection> _resourcePool;

    public ConnectionPool(string connectionString)
    {
        var connectionFactory = GetConnectionFactoryFunc(connectionString);

        var asyncResourcePoolOptions = new AsyncResourcePoolOptions(
            minNumResources: 20,
            resourcesExpireAfter: TimeSpan.FromMinutes(15));

        _resourcePool = new AsyncResourcePool<SnowflakeDbConnection>(connectionFactory, asyncResourcePoolOptions);
    }

    public Task<ReusableResource<SnowflakeDbConnection>> Get(CancellationToken cancellationToken) => _resourcePool.Get(cancellationToken);

    public void Dispose() => _resourcePool.Dispose();

    private static Func<Task<SnowflakeDbConnection>> GetConnectionFactoryFunc(string connectionString)
    {
        return async () =>
        {
            var conn = new SnowflakeDbConnection
            {
                ConnectionString = connectionString
            };

            await conn.OpenAsync();

            return conn;
        };
    }
}
```

2. Optionally register `ConnectionPool` as `Singleton` in dependency injection configuration

```
services.AddSingleton<ConnectionPool>(sp => new ConnectionPool(...));
```

3. Consume `ConnectionPool`

```csharp
public class ConnectionConsumer
{
    private readonly ConnectionPool _connectionPool;

    public ConnectionConsumer(ConnectionPool connectionPool) {
        _connectionPool = connectionPool;
    }

    public async Task DoSomething(CancellationToken cancellationToken) {
        using (var reusableConnection = await _connectionPool.Get(cancellationToken)) {
            var connection = reusableConnection.Resource;
            // Do something with the connection
        }
    }
} 
```