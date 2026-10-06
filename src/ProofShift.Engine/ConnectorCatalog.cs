using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Engine;

public sealed class ConnectorCatalog
{
    private readonly Dictionary<string, ISourceConnector> _readers;
    private readonly Dictionary<string, IShadowTargetConnector> _writers;

    public ConnectorCatalog(IEnumerable<ISourceConnector> readers, IEnumerable<IShadowTargetConnector> writers)
    {
        _readers = readers.ToDictionary(reader => reader.Id.Value, StringComparer.Ordinal);
        _writers = writers.ToDictionary(writer => writer.Id.Value, StringComparer.Ordinal);
    }

    public ISourceConnector Reader(string id) => _readers.TryGetValue(id, out var reader) ? reader :
        throw new ConnectorResolutionException(ConnectorIssueCodes.UnknownConnector, "Configured artifact reader is not installed.");
    public ITargetObserver Observer(string id) => new ReaderTargetObserver(Reader(id));
    public IShadowTargetConnector ShadowWriter(string id) => _writers.TryGetValue(id, out var writer) ? writer :
        throw new ConnectorResolutionException(ConnectorIssueCodes.UnknownConnector, "Configured connector has no shadow-write capability.");

    public IReadOnlyList<ConnectorCapabilityDescriptor> Capabilities => _readers.Values.OrderBy(reader => reader.Id.Value, StringComparer.Ordinal)
        .Select(reader => reader.Capabilities with { ShadowWrite = _writers.ContainsKey(reader.Id.Value) }).ToArray();

    public IReadOnlyList<ConnectorConfigurationSchema> ConfigurationSchemas => _readers.Values
        .OfType<IConnectorConfigurationSchemaProvider>().Select(provider => provider.ConfigurationSchema)
        .OrderBy(schema => schema.ConnectorId, StringComparer.Ordinal).ToArray();

    public ConnectorConfigurationSchema ConfigurationSchema(string id) => ConfigurationSchemas
        .SingleOrDefault(schema => schema.ConnectorId == id) ??
        throw new ConnectorResolutionException(ConnectorIssueCodes.UnknownConnector, "Configured connector schema is not installed.");
}