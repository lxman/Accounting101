using EphemeralMongo;

namespace Accounting101.TestSupport;

/// <summary>
/// One shared EphemeralMongo replica set per test process. The <see cref="Lazy{T}"/> (default thread-safe
/// mode) guarantees exactly one <c>mongod</c> even when many test classes request it in parallel — which is
/// what eliminates the replica-set-election storm. Per-test isolation comes from GUID databases/collections
/// in the fixtures, so sharing one server is safe. Disposed once at process exit; never by a fixture.
/// </summary>
public static class SharedMongo
{
    private static readonly Lazy<Task<IMongoRunner>> Runner = new(StartAsync);

    private static async Task<IMongoRunner> StartAsync()
    {
        IMongoRunner runner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => runner.Dispose();
        return runner;
    }

    /// <summary>The process-wide shared runner; fixtures consume its <c>ConnectionString</c>.</summary>
    public static Task<IMongoRunner> InstanceAsync() => Runner.Value;
}
