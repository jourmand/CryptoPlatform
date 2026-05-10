using CryptoPlatform.Application.UseCases;
using CryptoPlatform.Domain.Entities;
using CryptoPlatform.Domain.Messages;
using CryptoPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CryptoPlatform.Infrastructure.Repositories;

public class PlayerRepository : IPlayerRepository
{
    private readonly AppDbContext _db;
    public PlayerRepository(AppDbContext db) => _db = db;

    public Task<Player?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Players.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Player> CreateAsync(Player player, CancellationToken ct = default)
    {
        _db.Players.Add(player);
        await _db.SaveChangesAsync(ct);
        return player;
    }

    public async Task<int> GetNextPlayerIndexAsync(CancellationToken ct = default)
    {
        // nextval is atomic — two concurrent callers always receive different values,
        // preventing two players from being assigned the same HD wallet derivation path.
        var conn   = _db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT nextval('player_index_seq')";
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }
}

public class WalletRepository : IWalletRepository
{
    private readonly AppDbContext _db;
    public WalletRepository(AppDbContext db) => _db = db;

    public async Task SaveWalletsAsync(Guid playerId, WalletAddresses addresses, CancellationToken ct = default)
    {
        var wallets = new[]
        {
            new PlayerWallet { Id = Guid.NewGuid(), PlayerId = playerId, Coin = "USDT", Chain = "Tron",   Address = addresses.UsdtTron,   CreatedAt = DateTime.UtcNow },
            new PlayerWallet { Id = Guid.NewGuid(), PlayerId = playerId, Coin = "USDT", Chain = "Solana", Address = addresses.UsdtSolana, CreatedAt = DateTime.UtcNow },
            new PlayerWallet { Id = Guid.NewGuid(), PlayerId = playerId, Coin = "USDT", Chain = "BSC",    Address = addresses.UsdtBsc,    CreatedAt = DateTime.UtcNow },
            new PlayerWallet { Id = Guid.NewGuid(), PlayerId = playerId, Coin = "USDC", Chain = "Solana", Address = addresses.UsdcSolana, CreatedAt = DateTime.UtcNow },
            new PlayerWallet { Id = Guid.NewGuid(), PlayerId = playerId, Coin = "USDC", Chain = "BSC",    Address = addresses.UsdcBsc,    CreatedAt = DateTime.UtcNow },
            new PlayerWallet { Id = Guid.NewGuid(), PlayerId = playerId, Coin = "TRX",  Chain = "Tron",   Address = addresses.TrxTron,    CreatedAt = DateTime.UtcNow },
            new PlayerWallet { Id = Guid.NewGuid(), PlayerId = playerId, Coin = "POL",  Chain = "POL",    Address = addresses.PolPol,     CreatedAt = DateTime.UtcNow },
        };
        // Skip wallets that already exist (idempotent — guard against message redelivery)
        var existingAddresses = (await _db.PlayerWallets
            .Where(w => w.PlayerId == playerId)
            .Select(w => w.Address)
            .ToListAsync(ct)).ToHashSet();
        var newWallets = wallets.Where(w => !existingAddresses.Contains(w.Address)).ToArray();
        if (newWallets.Length == 0) return;
        _db.PlayerWallets.AddRange(newWallets);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            // Duplicate key — another concurrent handler already saved these wallets; ignore.
        }
    }

    public Task<PlayerWallet?> GetByAddressAsync(string address, CancellationToken ct = default)
        => _db.PlayerWallets.FirstOrDefaultAsync(w => w.Address == address, ct);
}

public class BalanceRepository : IBalanceRepository
{
    private readonly AppDbContext _db;
    public BalanceRepository(AppDbContext db) => _db = db;

    public async Task<decimal> GetBalanceAsync(Guid playerId, string coin, string chain, CancellationToken ct = default)
    {
        var balance = await _db.Balances
            .FirstOrDefaultAsync(b => b.PlayerId == playerId && b.Coin == coin && b.Chain == chain, ct);
        return balance?.Amount ?? 0m;
    }

    public async Task<decimal> CreditAsync(Guid playerId, string coin, string chain, decimal amount, string txHash, CancellationToken ct = default)
    {
        var balance = await _db.Balances
            .FirstOrDefaultAsync(b => b.PlayerId == playerId && b.Coin == coin && b.Chain == chain, ct);

        if (balance is null)
        {
            balance = new Balance { Id = Guid.NewGuid(), PlayerId = playerId, Coin = coin, Chain = chain, Amount = 0m };
            _db.Balances.Add(balance);
        }

        balance.Amount    += amount;
        balance.UpdatedAt  = DateTime.UtcNow;

        _db.LedgerEntries.Add(new LedgerEntry
        {
            Id            = Guid.NewGuid(),
            PlayerId      = playerId,
            Type          = LedgerEntryType.Deposit,
            Coin          = coin,
            Chain         = chain,
            Amount        = amount,
            BalanceBefore = balance.Amount - amount,
            BalanceAfter  = balance.Amount,
            ReferenceId   = txHash,
            CreatedAt     = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return balance.Amount;
    }

    public async Task<decimal> DebitAsync(Guid playerId, string coin, string chain, decimal amount, Guid withdrawalId, CancellationToken ct = default)
    {
        // SELECT FOR UPDATE locks the balance row for the duration of the transaction,
        // preventing concurrent withdrawals from both passing the balance check and going negative.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var balance = await _db.Balances
            .FromSqlRaw(
                "SELECT * FROM balances WHERE player_id = {0} AND coin = {1} AND chain = {2} FOR UPDATE",
                playerId, coin, chain)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Balance not found");

        if (balance.Amount < amount)
            throw new InvalidOperationException("Insufficient balance");

        balance.Amount    -= amount;
        balance.UpdatedAt  = DateTime.UtcNow;

        _db.LedgerEntries.Add(new LedgerEntry
        {
            Id            = Guid.NewGuid(),
            PlayerId      = playerId,
            Type          = LedgerEntryType.Withdrawal,
            Coin          = coin,
            Chain         = chain,
            Amount        = amount,
            BalanceBefore = balance.Amount + amount,
            BalanceAfter  = balance.Amount,
            ReferenceId   = withdrawalId.ToString(),
            CreatedAt     = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return balance.Amount;
    }
}

public class DepositRepository : IDepositRepository
{
    private readonly AppDbContext _db;
    public DepositRepository(AppDbContext db) => _db = db;

    public Task<bool> ExistsAsync(string txHash, CancellationToken ct = default)
        => _db.Deposits.AnyAsync(d => d.TxHash == txHash, ct);

    public async Task<Deposit> CreateAsync(Deposit deposit, CancellationToken ct = default)
    {
        _db.Deposits.Add(deposit);
        await _db.SaveChangesAsync(ct);
        return deposit;
    }

    public async Task UpdateStatusAsync(string txHash, DepositStatus status, CancellationToken ct = default)
    {
        var deposit = await _db.Deposits.FirstOrDefaultAsync(d => d.TxHash == txHash, ct);
        if (deposit is null) return;
        deposit.Status = status;
        if (status == DepositStatus.Confirmed) deposit.ConfirmedAt = DateTime.UtcNow;
        if (status == DepositStatus.Credited)  deposit.CreditedAt  = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

public class WithdrawalRepository : IWithdrawalRepository
{
    private readonly AppDbContext _db;
    public WithdrawalRepository(AppDbContext db) => _db = db;

    public async Task<Withdrawal> CreateAsync(Withdrawal withdrawal, CancellationToken ct = default)
    {
        _db.Withdrawals.Add(withdrawal);
        await _db.SaveChangesAsync(ct);
        return withdrawal;
    }

    public async Task UpdateAsync(Guid id, WithdrawalStatus status, string? txHash = null, decimal? fee = null, CancellationToken ct = default)
    {
        var w = await _db.Withdrawals.FindAsync([id], ct);
        if (w is null) return;
        w.Status      = status;
        w.TxHash      = txHash ?? w.TxHash;
        w.Fee         = fee    ?? w.Fee;
        w.CompletedAt = status == WithdrawalStatus.Completed ? DateTime.UtcNow : w.CompletedAt;
        await _db.SaveChangesAsync(ct);
    }
}
