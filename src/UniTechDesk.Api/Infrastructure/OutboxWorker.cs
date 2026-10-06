using System.Threading.Channels;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Email;
using UniTechDesk.Core.Options;

namespace UniTechDesk.Api.Infrastructure;

/// <summary>Despierta al proceso de envío cuando hay correos nuevos (así salen en segundos, sin esperar al sondeo).</summary>
public sealed class OutboxSignal : IOutboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Notify() => _channel.Writer.TryWrite(true);
    public ValueTask<bool> WaitAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

/// <summary>Proceso en segundo plano: vacía la bandeja de salida al recibir la señal y, de todos modos, cada PollSeconds.</summary>
public sealed class OutboxWorker : BackgroundService
{
    private readonly OutboxDispatcher _dispatcher;
    private readonly OutboxSignal _signal;
    private readonly EmailOptions _options;
    private readonly ILogger<OutboxWorker> _log;

    public OutboxWorker(OutboxDispatcher dispatcher, OutboxSignal signal, EmailOptions options, ILogger<OutboxWorker> log)
    {
        _dispatcher = dispatcher; _signal = signal; _options = options; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(5, _options.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Se vacía por lotes hasta que no quede nada vencido.
                int processed;
                do { processed = await _dispatcher.ProcessBatchAsync(stoppingToken); } while (processed >= Math.Max(1, _options.BatchSize));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Por ejemplo, la BD no responde: se registra y se reintenta en el siguiente sondeo.
                _log.LogError(ex, "Falló el ciclo de envío de correos; se reintentará.");
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(poll);
            try { await _signal.WaitAsync(wait.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* venció el sondeo */ }
            catch (OperationCanceledException) { break; }
        }
    }
}
