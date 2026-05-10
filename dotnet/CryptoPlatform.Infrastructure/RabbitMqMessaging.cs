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

namespace CryptoPlatform.Infrastructure.Messaging;

public class RabbitMqOptions
{
    public string Host        { get; set; } = "localhost";
    public string VirtualHost { get; set; } = "crypto";
    public string Username    { get; set; } = "rabbit";
    public string Password    { get; set; } = "rabbitpass";
}

// ── Publisher (.NET → RabbitMQ → Node) ────────────────────────────

public class RabbitMqPublisher : IMessagePublisher, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;

    public RabbitMqPublisher(IOptions<RabbitMqOptions> opts)
    {
        var o = opts.Value;
        var factory = new ConnectionFactory
        {
            HostName    = o.Host,
            VirtualHost = o.VirtualHost,
            UserName    = o.Username,
            Password    = o.Password,
        };
        _connection = factory.CreateConnection();
        _channel    = _connection.CreateModel();
        _channel.ConfirmSelect(); // publisher confirms
    }

    public Task PublishAsync<T>(string routingKey, T message, CancellationToken ct = default) where T : class
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = _channel.CreateBasicProperties();
        props.Persistent   = true;
        props.ContentType  = "application/json";
        props.Type         = typeof(T).Name;
        props.MessageId    = Guid.NewGuid().ToString();
        props.Timestamp    = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _channel.BasicPublish("platform.commands", routingKey, props, body);
        _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    public void Dispose() { _channel.Dispose(); _connection.Dispose(); }
}

// ── Consumer (Node → RabbitMQ → .NET) ─────────────────────────────

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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = _opts.Value;
        var factory = new ConnectionFactory
        {
            HostName    = o.Host,
            VirtualHost = o.VirtualHost,
            UserName    = o.Username,
            Password    = o.Password,
            DispatchConsumersAsync = true,
        };
        var connection = factory.CreateConnection();
        var channel    = connection.CreateModel();
        channel.BasicQos(0, 10, false); // prefetch 10

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

        SubscribeQueue(channel, "wallet.created", async body =>
        {
            var evt = JsonSerializer.Deserialize<WalletsCreatedEvent>(body, _jsonOpts)!;
            using var scope = _scope.CreateScope();
            var wallets = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            await wallets.SaveWalletsAsync(evt.PlayerId, evt.Addresses);
        });

        stoppingToken.Register(() => { channel.Close(); connection.Close(); });
        return Task.CompletedTask;
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
