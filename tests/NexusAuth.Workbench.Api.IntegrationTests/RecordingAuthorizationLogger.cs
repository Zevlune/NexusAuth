using Microsoft.Extensions.Logging;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class RecordingAuthorizationLogger<T> : ILogger<T>
{
    private readonly Dictionary<string, object?> scopeProperties =
        new(StringComparer.Ordinal);

    public List<(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var previousProperties = new Dictionary<string, object?>(scopeProperties);
        if (state is IEnumerable<KeyValuePair<string, object?>> properties)
        {
            foreach (var property in properties)
                scopeProperties[property.Key] = property.Value;
        }

        return new RecordingAuthorizationLogScope(() =>
        {
            scopeProperties.Clear();
            foreach (var property in previousProperties)
                scopeProperties[property.Key] = property.Value;
        });
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, object?>(scopeProperties, StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> stateProperties)
        {
            foreach (var property in stateProperties)
                properties[property.Key] = property.Value;
        }

        Entries.Add((logLevel, formatter(state, exception), exception, properties));
    }
}
