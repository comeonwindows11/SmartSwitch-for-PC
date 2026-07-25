using SmartSwitch.Core.Abstractions;
using SmartSwitch.Core.Models;
using SmartSwitch.Core.Services;

namespace SmartSwitch.Core.Tests;

public sealed class MigrationEngineTests
{
    [Fact]
    public async Task ScanAsyncExecutesDependenciesBeforeDependentModule()
    {
        var executionOrder = new List<string>();
        var dependency = new FakeModule("dependency", [], executionOrder);
        var main = new FakeModule("main", ["dependency"], executionOrder);
        var engine = new MigrationEngine([main, dependency], new TestLogger());
        var request = new MigrationRequest(
            MigrationMode.Safe,
            new HashSet<MigrationCategory>(),
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main" });

        await engine.ScanAsync(request);

        Assert.Equal(["dependency", "main"], executionOrder);
    }

    [Fact]
    public async Task ScanAsyncRejectsCircularDependencies()
    {
        var first = new FakeModule("first", ["second"], []);
        var second = new FakeModule("second", ["first"], []);
        var engine = new MigrationEngine([first, second], new TestLogger());
        var request = new MigrationRequest(
            MigrationMode.Safe,
            new HashSet<MigrationCategory>(),
            [],
            new HashSet<string> { "first" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ScanAsync(request));
    }

    private sealed class FakeModule : IMigrationModule
    {
        private readonly ICollection<string> _executionOrder;

        public FakeModule(
            string id,
            IReadOnlyCollection<string> dependencies,
            ICollection<string> executionOrder)
        {
            Id = id;
            Dependencies = dependencies;
            _executionOrder = executionOrder;
        }

        public string Id { get; }

        public string DisplayName => Id;

        public IReadOnlyCollection<string> Dependencies { get; }

        public IReadOnlyCollection<MigrationCategory> SupportedCategories =>
            [MigrationCategory.CustomFiles];

        public Task<ModuleScanResult> ScanAsync(
            MigrationRequest request,
            IProgress<MigrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            _executionOrder.Add(Id);
            return Task.FromResult(
                new ModuleScanResult(
                    Id,
                    [],
                    [],
                    new Dictionary<string, string>()));
        }
    }
}
