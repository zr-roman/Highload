using Microsoft.Extensions.Options;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>Options that a test changes as a reload of appsettings.json would: <see cref="Set"/> tells the listeners.</summary>
public sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> listeners = [];

    public T CurrentValue { get; private set; } = value;

    public T Get(string? name) => CurrentValue;

    public void Set(T value)
    {
        CurrentValue = value;
        foreach (var listener in listeners.ToList())
            listener(value, Options.DefaultName);
    }

    public IDisposable OnChange(Action<T, string?> listener)
    {
        listeners.Add(listener);
        return new Unsubscribe(() => listeners.Remove(listener));
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
