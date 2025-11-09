# Redis Test Suite

This test suite connects to Azure Redis Cache and includes both static snapshot tests and reactive watch tests.

## Setup

1. The Redis password is stored in a `.env` file at the root of the project.
2. The `.env` file is automatically loaded when running tests

## Running the Tests

Run all tests using:

```bash
cd MarketMakerWorkerService.Tests
dotnet test
```

For detailed output:

```bash
dotnet test --logger "console;verbosity=detailed"
```

Run specific tests:

```bash
# Run only connection tests
dotnet test --filter "FullyQualifiedName~RedisConnectionTests"

# Run only reactive watch tests
dotnet test --filter "FullyQualifiedName~RedisReactiveWatchTests"
```

## Configuration

In AppSettings for MarketMakerWorkerService, or can be injected by the env.

## Test Files

### 1. RedisConnectionTests.cs

**Purpose**: Static snapshot of all Redis keys and values

**Features**:
- Connects to Azure Redis Cache using SSL
- Retrieves all keys from the Redis database
- Displays each key with its type and value
- Handles different Redis data types:
  - String
  - List
  - Set
  - Hash
  - Sorted Set
- Shows TTL (Time To Live) information for keys with expiration

### 2. RedisReactiveWatchTests.cs

**Purpose**: Discovery work for MM pattern and design. Reactive pipeline prototype for watching Redis key updates:

**Features**:
- Watches `spotindex:BTC_USD` key for N seconds
- Uses **Channel<T>** for async data streaming
- Uses **Rx.NET (System.Reactive)** for reactive pipeline pattern
- Polls Redis every 50ms for changes
- Emits updates only when values change
- Prints each update with timestamp

**Reactive Pipeline Pattern**:
```
Redis Polling → Channel<T> → Observable → Subscribers
```

This pattern provides:
- **Backpressure handling** via Channel<T>
- **Reactive composition** via Rx.NET operators
- **Clean separation** between data source and consumers
- **Scalable architecture** for real-time data processing

## Packages Used

- **StackExchange.Redis** (v2.9.32) - Redis client
- **DotNetEnv** (v3.1.1) - Environment variable loading
- **System.Reactive** (v6.1.0) - Reactive Extensions for .NET
- **xUnit** - Test framework
