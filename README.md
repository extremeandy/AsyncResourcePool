# AsyncResourcePool

> Non-blocking, managed pool of re-usable resources, with a specified minimum and optional maximum number of resources, and optional expiry time for resources to be cleaned up if not used for a time.

## Installation

```
dotnet add package AsyncResourcePool
```

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