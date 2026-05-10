using System.Text;
using System.Text.Json;
using CryptoPlatform.Application.UseCases;
using CryptoPlatform.Domain.Messages;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace CryptoPlatform.Infrastructure.Messaging;

public class RabbitMqOptions
{
    public string Host        { get; set; } = "localhost";
    public string VirtualHost { get; set; } = "crypto";
    public string Username    { get; set; } = "rabbit";
    public string Password    { get; set; } = "rabbitpass";
}

// ── Publisher (.NET → RabbitMQ → Node) ────────────────────────────
// Thread-safe: SemaphoreSlim serialises concurrent calls.
// Resilient: lazily connects on first publish and reconnects after drops.

public class RabbitMqPublisher : IMessagePublisher, IDisposable
{
    private readonly RabbitMqOptions _opts;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqPublisher(IOptions<RabbitMqOptions> opts, ILogger<RabbitMqPublisher> logger)
        => (_opts, _logger) = (opts.Value, logger);

    public async Task PublishAsync<T>(string routingKey, T message, CancellationToken ct = default) where T : class
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);

            var props = _channel!.CreateBasicProperties();
            props.Persistent  = true;
            props.ContentType = "application/json";
            props.Type        = typeof(T).Name;
            props.MessageId   = Guid.NewGuid().ToString();
            props.Timestamp   = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            _channel.BasicPublish("platform.commands", routingKey, props, body);
            _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection?.IsOpen == true && _channel?.IsOpen == true)
            return;

        _channel?.Dispose();
        _connection?.Dispose();

        var factory = new ConnectionFactory
        {
            HostName    = _opts.Host,
            VirtualHost = _opts.VirtualHost,
            UserName    = _opts.Username,
            Password    = _opts.Password,
        };

        const int maxAttempts = 10;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _connection = factory.CreateConnection();
                _channel    = _connection.CreateModel();
                _channel.ConfirmSelect();
                _logger.LogInformation("RabbitMQ publisher connected");
                return;
            }
            catch (BrokerUnreachableException) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 30));
                _logger.LogWarning("RabbitMQ not reachable; retrying in {Delay}s (attempt {Attempt}/{Max})",
                    delay.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException("RabbitMQ publisher could not connect after multiple attempts");
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _lock.Dispose();
    }
}

// ── Consumer (Node → RabbitMQ → .NET) ─────────────────────────────
// Resilient: reconnects with exponential backoff whenever the connection drops.

public class RabbitMqConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scope;
    private readonly IOptions<RabbitMqOptions> _opts;
    private readonly ILogger<RabbitMqConsumer> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RabbitMqConsumer(IServiceScopeFactory scope, IOptions<RabbitMqOptions> opts,
        ILogger<RabbitMqConsumer> logger)
        => (_scope, _opts, _logger) = (scope, opts, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                attempt++;
                await RunConsumerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // clean shutdown
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 60));
                _logger.LogWarning(ex, "RabbitMQ consumer disconnected; reconnecting in {Delay}s", delay.TotalSeconds);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunConsumerLoopAsync(CancellationToken stoppingToken)
    {
        var o = _opts.Value;
        var factory = new ConnectionFactory
        {
            HostName               = o.Host,
            VirtualHost            = o.VirtualHost,
            UserName               = o.Username,
            Password               = o.Password,
            DispatchConsumersAsync = true,
        };

        IConnection? connection = null;
        const int maxAttempts = 10;
        for (int i = 1; i <= maxAttempts; i++)
        {
            try
            {
                connection = factory.CreateConnection();
                break;
            }
            catch (BrokerUnreachableException) when (i < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(i * 2, 30));
                _logger.LogInformation("RabbitMQ not ready; retrying in {Delay}s (attempt {Attempt}/{Max})",
                    delay.TotalSeconds, i, maxAttempts);
                await Task.Delay(delay, stoppingToken);
            }
        }

        if (connection is null)
            throw new InvalidOperationException("RabbitMQ consumer could not connect after multiple attempts");

        // Signal when the connection is lost so the outer loop can reconnect
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionShutdown += (_, _) => disconnected.TrySetResult();

        var channel = connection.CreateModel();
        channel.BasicQos(0, 10, false);

        SubscribeQueue(channel, "deposit.confirmed", async body =>
        {
            var evt = JsonSerializer.Deserialize<DepositConfirmedEvent>(body, _jsonOpts)!;
            using var scope = _scope.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new CreditDepositCommand(evt));
        });

        SubscribeQueue(channel, "withdrawal.completed", async body =>
        {
            var evt = JsonSerializer.Deserialize<WithdrawalCompletedEvent>(body, _jsonOpts)!;
            using var scope = _scope.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new FinalizeWithdrawalCommand(evt));
        });

        SubscribeQueue(channel, "deposit.detected", async body =>
        {
            var evt = JsonSerializer.Deserialize<DepositDetectedEvent>(body, _jsonOpts)!;
            using var scope = _scope.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new RecordDepositDetectedCommand(evt));
        });

        SubscribeQueue(channel, "wallet.created", async body =>
        {
            var evt = JsonSerializer.Deserialize<WalletsCreatedEvent>(body, _jsonOpts)!;
            using var scope = _scope.CreateScope();
            var wallets    = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            var walletKeys = scope.ServiceProvider.GetRequiredService<IWalletKeyRepository>();
            await wallets.SaveWalletsAsync(evt.PlayerId, evt.Addresses);
            await walletKeys.SaveAsync(evt.PlayerId, evt.EncryptedKeys);
        });

        _logger.LogInformation("RabbitMQ consumer connected and listening");

        using var reg = stoppingToken.Register(() =>
        {
            channel.Close();
            connection.Close();
            disconnected.TrySetResult();
        });

        await disconnected.Task;

        channel.Dispose();
        connection.Dispose();
    }

    private void SubscribeQueue(IModel channel, string queue, Func<string, Task> handler)
    {
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, ea) =>
        {
            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            try
            {
                await handler(body);
                channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from {Queue}", queue);
                channel.BasicNack(ea.DeliveryTag, false, requeue: false); // goes to DLQ
            }
        };
        channel.BasicConsume(queue, autoAck: false, consumer: consumer);
    }
}
