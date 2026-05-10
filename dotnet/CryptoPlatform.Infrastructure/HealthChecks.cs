using CryptoPlatform.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using StackExchange.Redis;

namespace CryptoPlatform.Infrastructure.HealthChecks;

public class RedisOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";
}

public class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqOptions _opts;
    public RabbitMqHealthCheck(IOptions<RabbitMqOptions> opts) => _opts = opts.Value;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName                   = _opts.Host,
                VirtualHost                = _opts.VirtualHost,
                UserName                   = _opts.Username,
                Password                   = _opts.Password,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            };
            using var conn = factory.CreateConnection();
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (BrokerUnreachableException ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(ex.Message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(ex.Message));
        }
    }
}

public class RedisHealthCheck : IHealthCheck
{
    private readonly string _connectionString;
    public RedisHealthCheck(IOptions<RedisOptions> opts) => _connectionString = opts.Value.ConnectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            await mux.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
