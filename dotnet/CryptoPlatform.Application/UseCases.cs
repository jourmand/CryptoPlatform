using CryptoPlatform.Domain.Entities;
using CryptoPlatform.Domain.Messages;
using MediatR;

namespace CryptoPlatform.Application.UseCases;

// ── Interfaces (implemented in Infrastructure) ─────────────────────

public interface IPlayerRepository
{
    Task<Player?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Player> CreateAsync(Player player, CancellationToken ct = default);
    Task<int> GetNextPlayerIndexAsync(CancellationToken ct = default);
}

public interface IWalletRepository
{
    Task SaveWalletsAsync(Guid playerId, WalletAddresses addresses, CancellationToken ct = default);
    Task<PlayerWallet?> GetByAddressAsync(string address, CancellationToken ct = default);
}

public interface IBalanceRepository
{
    Task<decimal> GetBalanceAsync(Guid playerId, string coin, string chain, CancellationToken ct = default);
    Task<decimal> CreditAsync(Guid playerId, string coin, string chain, decimal amount, string txHash, CancellationToken ct = default);
    Task<decimal> DebitAsync(Guid playerId, string coin, string chain, decimal amount, Guid withdrawalId, CancellationToken ct = default);
}

public interface IDepositRepository
{
    Task<bool> ExistsAsync(string txHash, CancellationToken ct = default);
    Task<Deposit> CreateAsync(Deposit deposit, CancellationToken ct = default);
    Task UpdateStatusAsync(string txHash, DepositStatus status, CancellationToken ct = default);
}

public interface IWithdrawalRepository
{
    Task<Withdrawal> CreateAsync(Withdrawal withdrawal, CancellationToken ct = default);
    Task UpdateAsync(Guid id, WithdrawalStatus status, string? txHash = null, decimal? fee = null, CancellationToken ct = default);
}

public interface IMessagePublisher
{
    Task PublishAsync<T>(string routingKey, T message, CancellationToken ct = default) where T : class;
}

// ── Register Player ────────────────────────────────────────────────

public record RegisterPlayerCommand(string Username, string Email) : IRequest<Player>;

public class RegisterPlayerHandler : IRequestHandler<RegisterPlayerCommand, Player>
{
    private readonly IPlayerRepository _players;
    private readonly IMessagePublisher _publisher;

    public RegisterPlayerHandler(IPlayerRepository players, IMessagePublisher publisher)
        => (_players, _publisher) = (players, publisher);

    public async Task<Player> Handle(RegisterPlayerCommand request, CancellationToken ct)
    {
        var player = await _players.CreateAsync(new Player
        {
            Id        = Guid.NewGuid(),
            Username  = request.Username,
            Email     = request.Email,
            CreatedAt = DateTime.UtcNow,
        }, ct);

        var playerIndex = await _players.GetNextPlayerIndexAsync(ct);

        // Tell Node.js blockchain service to create wallets
        await _publisher.PublishAsync("wallet.create", new CreateWalletsCommand(player.Id, playerIndex), ct);

        return player;
    }
}

// ── Handle Deposit Confirmed (from Node event) ─────────────────────

public record CreditDepositCommand(DepositConfirmedEvent Event) : IRequest;

public class CreditDepositHandler : IRequestHandler<CreditDepositCommand>
{
    private readonly IDepositRepository _deposits;
    private readonly IBalanceRepository _balances;

    public CreditDepositHandler(IDepositRepository deposits, IBalanceRepository balances)
        => (_deposits, _balances) = (deposits, balances);

    public async Task Handle(CreditDepositCommand request, CancellationToken ct)
    {
        var e = request.Event;

        // Idempotency check — may receive duplicate events
        if (await _deposits.ExistsAsync(e.TxHash, ct))
            return;

        await _balances.CreditAsync(e.PlayerId, e.Coin, e.Chain, e.Amount, e.TxHash, ct);
        await _deposits.UpdateStatusAsync(e.TxHash, DepositStatus.Credited, ct);
    }
}

// ── Request Withdrawal ─────────────────────────────────────────────

public record RequestWithdrawalCommand(
    Guid    PlayerId,
    string  Coin,
    string  Chain,
    string  ToAddress,
    decimal Amount
) : IRequest<Withdrawal>;

public class RequestWithdrawalHandler : IRequestHandler<RequestWithdrawalCommand, Withdrawal>
{
    private readonly IBalanceRepository _balances;
    private readonly IWithdrawalRepository _withdrawals;
    private readonly IMessagePublisher _publisher;

    public RequestWithdrawalHandler(
        IBalanceRepository balances,
        IWithdrawalRepository withdrawals,
        IMessagePublisher publisher)
        => (_balances, _withdrawals, _publisher) = (balances, withdrawals, publisher);

    public async Task<Withdrawal> Handle(RequestWithdrawalCommand request, CancellationToken ct)
    {
        CoinChainValidator.Validate(request.Coin, request.Chain, request.ToAddress);

        var balance = await _balances.GetBalanceAsync(
            request.PlayerId, request.Coin, request.Chain, ct);

        if (balance < request.Amount)
            throw new InvalidOperationException("Insufficient balance");

        // Debit immediately — funds reserved
        await _balances.DebitAsync(
            request.PlayerId, request.Coin, request.Chain, request.Amount,
            Guid.NewGuid(), ct);

        var withdrawal = await _withdrawals.CreateAsync(new Withdrawal
        {
            Id          = Guid.NewGuid(),
            PlayerId    = request.PlayerId,
            Coin        = request.Coin,
            Chain       = request.Chain,
            ToAddress   = request.ToAddress,
            Amount      = request.Amount,
            Status      = WithdrawalStatus.Pending,
            RequestedAt = DateTime.UtcNow,
        }, ct);

        // Tell Node.js to broadcast the transaction
        await _publisher.PublishAsync("withdrawal.execute",
            new ExecuteWithdrawalCommand(
                withdrawal.Id, request.PlayerId, request.Coin,
                request.Chain, request.ToAddress, request.Amount), ct);

        return withdrawal;
    }
}

// ── Handle Withdrawal Completed (from Node event) ──────────────────

public record FinalizeWithdrawalCommand(WithdrawalCompletedEvent Event) : IRequest;

public class FinalizeWithdrawalHandler : IRequestHandler<FinalizeWithdrawalCommand>
{
    private readonly IWithdrawalRepository _withdrawals;

    public FinalizeWithdrawalHandler(IWithdrawalRepository withdrawals)
        => _withdrawals = withdrawals;

    public async Task Handle(FinalizeWithdrawalCommand request, CancellationToken ct)
    {
        var e = request.Event;
        await _withdrawals.UpdateAsync(
            e.WithdrawalId, WithdrawalStatus.Completed, e.TxHash, e.Fee, ct);
    }
}
