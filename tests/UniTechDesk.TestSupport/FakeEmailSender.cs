using UniTechDesk.Core.Abstractions;

namespace UniTechDesk.TestSupport;

/// <summary>Guarda los correos "enviados" para revisarlos en las pruebas. Puede simular fallos.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly object _gate = new();
    public List<EmailMessage> Sent { get; } = new();
    /// <summary>Si se asigna, se lanza en cada envío (por ejemplo, un EmailSendException).</summary>
    public Func<EmailMessage, Exception?>? Failure { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var fail = Failure?.Invoke(message);
        if (fail is not null) throw fail;
        lock (_gate) Sent.Add(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> Snapshot() { lock (_gate) return Sent.ToList(); }
}

public sealed class NoOpSignal : IOutboxSignal { public void Notify() { } }
