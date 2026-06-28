namespace Accounting101.Interchange.Tests;

public sealed class InterchangeRegistryTests
{
    private sealed record Thing(string Value);

    private sealed class FakeThingImporter(InterchangeFormat format) : IImporter<Thing>
    {
        public InterchangeFormat Format => format;
        public ImportResult<Thing> Import(Stream source, ImportOptions options) =>
            new([new Thing("x")], []);
    }

    [Fact]
    public void Resolves_a_registered_importer_by_entity_and_format()
    {
        InterchangeRegistry registry = new();
        FakeThingImporter csv = new(InterchangeFormat.Csv);
        registry.Register<Thing>(csv);

        Assert.Same(csv, registry.Resolve<Thing>(InterchangeFormat.Csv));
    }

    [Fact]
    public void Returns_null_for_an_unregistered_format_or_entity()
    {
        InterchangeRegistry registry = new();
        registry.Register<Thing>(new FakeThingImporter(InterchangeFormat.Csv));

        Assert.Null(registry.Resolve<Thing>(InterchangeFormat.Ofx));   // wrong format
        Assert.Null(registry.Resolve<string>(InterchangeFormat.Csv));  // wrong entity type
    }
}
