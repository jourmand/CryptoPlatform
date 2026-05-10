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
    Task<PlayerWallet?> GetByPlayerCoinChainAsync(Guid playerId, string coin, string chain, CancellationToken ct = default);
    Task<IReadOnlyList<PlayerWallet>> GetByPlayerIdAsync(Guid playerId, CancellationToken ct = default);
}

public interface IWalletKeyRepository
{
    Task SaveAsync(Guid playerId, WalletEncryptedKeys keys, CancellationToken ct = default);
    Task<WalletKey?> GetByPlayerAndChainGroupAsync(Guid playerId, string chainGroup, CancellationToken ct = default);
}

public interface IBalanceRepository
{
    Task<decimal> GetBalanceAsync(Guid playerId, string coin, string chain, CancellationToken ct = default);
    Task<IReadOnlyList<Balance>> GetAllAsync(Guid playerId, CancellationToken ct = default);
    Task<decimal> CreditAsync(Guid playerId, string coin, string chain, decimal amount, string txHash, CancellationToken ct = default);
    Task<decimal> DebitAsync(Guid playerId, string coin, string chain, decimal amount, Guid withdrawalId, CancellationToken ct = default);
}

public interface IDepositRepository
{
    Task<bool> ExistsAsync(string txHash, CancellationToken ct = default);
    Task<Deposit?> GetByTxHashAsync(string txHash, CancellationToken ct = default);
    Task<Deposit> CreateAsync(Deposit deposit, CancellationToken ct = default);
    Task UpdateStatusAsync(string txHash, DepositStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<Deposit>> GetByPlayerIdAsync(Guid playerId, CancellationToken ct = default);
}

public interface IWithdrawalRepository
{
    Task<Withdrawal> CreateAsync(Withdrawal withdrawal, CancellationToken ct = default);
    Task UpdateAsync(Guid id, WithdrawalStatus status, string? txHash = null, decimal? fee = null, CancellationToken ct = default);
    Task<IReadOnlyList<Withdrawal>> GetByPlayerIdAsync(Guid playerId, CancellationToken ct = default);
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

// ── Handle Deposit Detected (from Node event) ──────────────────────

public record RecordDepositDetectedCommand(DepositDetectedEvent Event) : IRequest;

public class RecordDepositDetectedHandler : IRequestHandler<RecordDepositDetectedCommand>
{
    private readonly IDepositRepository _deposits;

    public RecordDepositDetectedHandler(IDepositRepository deposits) => _deposits = deposits;

    public async Task Handle(RecordDepositDetectedCommand request, CancellationToken ct)
    {
        var e = request.Event;

        if (await _deposits.ExistsAsync(e.TxHash, ct))
            return;

        await _deposits.CreateAsync(new Deposit
        {
            Id            = Guid.NewGuid(),
            PlayerId      = e.PlayerId,
            Coin          = e.Coin,
            Chain         = e.Chain,
            TxHash        = e.TxHash,
            FromAddress   = e.FromAddress,
            ToAddress     = e.ToAddress,
            Amount        = e.Amount,
            Status        = DepositStatus.Detected,
            Confirmations = e.Confirmations,
            DetectedAt    = DateTime.UtcNow,
        }, ct);
    }
}

// ── Handle Deposit Confirmed (from Node event) ─────────────────────

public record CreditDepositCommand(DepositConfirmedEvent Event) : IRequest;

public class CreditDepositHandler : IRequestHandler<CreditDepositCommand>
{
    private readonly IDepositRepository _deposits;
    private readonly IBalanceRepository _balances;
    private readonly IWalletRepository _wallets;
    private readonly IWalletKeyRepository _walletKeys;
    private readonly IMessagePublisher _publisher;

    public CreditDepositHandler(
        IDepositRepository deposits,
        IBalanceRepository balances,
        IWalletRepository wallets,
        IWalletKeyRepository walletKeys,
        IMessagePublisher publisher)
        => (_deposits, _balances, _wallets, _walletKeys, _publisher)
            = (deposits, balances, wallets, walletKeys, publisher);

    public async Task Handle(CreditDepositCommand request, CancellationToken ct)
    {
        var e = request.Event;

        // Idempotency — skip only if already credited, not just if the deposit record exists
        var deposit = await _deposits.GetByTxHashAsync(e.TxHash, ct);
        if (deposit?.Status == DepositStatus.Credited)
            return;

        await _balances.CreditAsync(e.PlayerId, e.Coin, e.Chain, e.Amount, e.TxHash, ct);

        if (deposit is null)
        {
            await _deposits.CreateAsync(new Deposit
            {
                Id         = Guid.NewGuid(),
                PlayerId   = e.PlayerId,
                Coin       = e.Coin,
                Chain      = e.Chain,
                TxHash     = e.TxHash,
                ToAddress  = string.Empty,
                Amount     = e.Amount,
                Status     = DepositStatus.Credited,
                DetectedAt = DateTime.UtcNow,
                CreditedAt = DateTime.UtcNow,
            }, ct);
        }
        else
        {
            await _deposits.UpdateStatusAsync(e.TxHash, DepositStatus.Credited, ct);
        }

        // Trigger sweep: look up the player's deposit wallet address and encrypted key
        var wallet = await _wallets.GetByPlayerCoinChainAsync(e.PlayerId, e.Coin, e.Chain, ct);
        if (wallet is not null)
        {
            var chainGroup = ChainGroups.FromChain(e.Chain);
            var walletKey  = await _walletKeys.GetByPlayerAndChainGroupAsync(e.PlayerId, chainGroup, ct);
            if (walletKey is not null)
            {
                await _publisher.PublishAsync("sweep.execute",
                    new SweepCommand(e.PlayerId, e.Coin, e.Chain, wallet.Address, walletKey.EncryptedPrivateKey), ct);
            }
        }
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

// ── Read Queries ────────────────────────────────────────────────────

public record GetPlayerWalletsQuery(Guid PlayerId) : IRequest<IReadOnlyList<PlayerWallet>>;

public class GetPlayerWalletsHandler : IRequestHandler<GetPlayerWalletsQuery, IReadOnlyList<PlayerWallet>>
{
    private readonly IWalletRepository _wallets;
    public GetPlayerWalletsHandler(IWalletRepository wallets) => _wallets = wallets;
    public Task<IReadOnlyList<PlayerWallet>> Handle(GetPlayerWalletsQuery request, CancellationToken ct)
        => _wallets.GetByPlayerIdAsync(request.PlayerId, ct);
}

public record GetPlayerBalancesQuery(Guid PlayerId) : IRequest<IReadOnlyList<Balance>>;

public class GetPlayerBalancesHandler : IRequestHandler<GetPlayerBalancesQuery, IReadOnlyList<Balance>>
{
    private readonly IBalanceRepository _balances;
    public GetPlayerBalancesHandler(IBalanceRepository balances) => _balances = balances;
    public Task<IReadOnlyList<Balance>> Handle(GetPlayerBalancesQuery request, CancellationToken ct)
        => _balances.GetAllAsync(request.PlayerId, ct);
}

public record GetPlayerDepositsQuery(Guid PlayerId) : IRequest<IReadOnlyList<Deposit>>;

public class GetPlayerDepositsHandler : IRequestHandler<GetPlayerDepositsQuery, IReadOnlyList<Deposit>>
{
    private readonly IDepositRepository _deposits;
    public GetPlayerDepositsHandler(IDepositRepository deposits) => _deposits = deposits;
    public Task<IReadOnlyList<Deposit>> Handle(GetPlayerDepositsQuery request, CancellationToken ct)
        => _deposits.GetByPlayerIdAsync(request.PlayerId, ct);
}

public record GetPlayerWithdrawalsQuery(Guid PlayerId) : IRequest<IReadOnlyList<Withdrawal>>;

public class GetPlayerWithdrawalsHandler : IRequestHandler<GetPlayerWithdrawalsQuery, IReadOnlyList<Withdrawal>>
{
    private readonly IWithdrawalRepository _withdrawals;
    public GetPlayerWithdrawalsHandler(IWithdrawalRepository withdrawals) => _withdrawals = withdrawals;
    public Task<IReadOnlyList<Withdrawal>> Handle(GetPlayerWithdrawalsQuery request, CancellationToken ct)
        => _withdrawals.GetByPlayerIdAsync(request.PlayerId, ct);
}
