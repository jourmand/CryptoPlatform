using CryptoPlatform.Application.UseCases;
using CryptoPlatform.Domain.Entities;
using CryptoPlatform.Domain.Messages;
using CryptoPlatform.Infrastructure.Data;
using CryptoPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;
using Xunit;

namespace CryptoPlatform.Tests.Integration;

public class DepositFlowTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static readonly string ValidBscAddress = "0x" + new string('A', 40);

    // Creates a uniquely-named player to avoid unique-constraint conflicts between tests
    private static RegisterPlayerCommand UniquePlayer(string prefix) =>
        new($"{prefix}_{Guid.NewGuid():N}", $"{prefix}_{Guid.NewGuid():N}@test.com");

    [Fact]
    public async Task DepositCredit_ThenWithdrawal_LeavesCorrectBalance()
    {
        var pub = new Mock<IMessagePublisher>();
        var playerRepo    = new PlayerRepository(_db);
        var balanceRepo   = new BalanceRepository(_db);
        var depositRepo   = new DepositRepository(_db);
        var walletRepo    = new WalletRepository(_db);
        var walletKeyRepo = new WalletKeyRepository(_db);
        var withdrawalRepo = new WithdrawalRepository(_db);

        var player = await new RegisterPlayerHandler(playerRepo, pub.Object)
            .Handle(UniquePlayer("alice"), default);

        // Credit 200 USDT on BSC
        var depositEvt = new DepositConfirmedEvent(player.Id, "USDT", "BSC", $"0xtx_{Guid.NewGuid():N}", 200m);
        await new CreditDepositHandler(depositRepo, balanceRepo, walletRepo, walletKeyRepo, pub.Object)
            .Handle(new CreditDepositCommand(depositEvt), default);

        Assert.Equal(200m, await balanceRepo.GetBalanceAsync(player.Id, "USDT", "BSC"));

        // Withdraw 80
        var withdrawal = await new RequestWithdrawalHandler(balanceRepo, withdrawalRepo, pub.Object)
            .Handle(new RequestWithdrawalCommand(player.Id, "USDT", "BSC", ValidBscAddress, 80m), default);

        Assert.Equal(WithdrawalStatus.Pending, withdrawal.Status);
        Assert.Equal(120m, await balanceRepo.GetBalanceAsync(player.Id, "USDT", "BSC"));
    }

    [Fact]
    public async Task CreditDeposit_IsIdempotent_WhenCalledTwiceWithSameTxHash()
    {
        var pub = new Mock<IMessagePublisher>();
        var playerRepo    = new PlayerRepository(_db);
        var balanceRepo   = new BalanceRepository(_db);
        var depositRepo   = new DepositRepository(_db);
        var walletRepo    = new WalletRepository(_db);
        var walletKeyRepo = new WalletKeyRepository(_db);

        var player = await new RegisterPlayerHandler(playerRepo, pub.Object)
            .Handle(UniquePlayer("bob"), default);

        var evt = new DepositConfirmedEvent(player.Id, "USDT", "BSC", $"0xtx_{Guid.NewGuid():N}", 50m);
        var handler = new CreditDepositHandler(depositRepo, balanceRepo, walletRepo, walletKeyRepo, pub.Object);

        // Simulate duplicate delivery
        await handler.Handle(new CreditDepositCommand(evt), default);
        await handler.Handle(new CreditDepositCommand(evt), default);

        // Balance must reflect exactly one credit
        Assert.Equal(50m, await balanceRepo.GetBalanceAsync(player.Id, "USDT", "BSC"));
    }

    [Fact]
    public async Task RequestWithdrawal_Throws_WhenBalanceInsufficient()
    {
        var pub = new Mock<IMessagePublisher>();
        var playerRepo    = new PlayerRepository(_db);
        var balanceRepo   = new BalanceRepository(_db);
        var depositRepo   = new DepositRepository(_db);
        var walletRepo    = new WalletRepository(_db);
        var walletKeyRepo = new WalletKeyRepository(_db);
        var withdrawalRepo = new WithdrawalRepository(_db);

        var player = await new RegisterPlayerHandler(playerRepo, pub.Object)
            .Handle(UniquePlayer("carol"), default);

        // Credit 30, try to withdraw 100
        var depositEvt = new DepositConfirmedEvent(player.Id, "USDT", "BSC", $"0xtx_{Guid.NewGuid():N}", 30m);
        await new CreditDepositHandler(depositRepo, balanceRepo, walletRepo, walletKeyRepo, pub.Object)
            .Handle(new CreditDepositCommand(depositEvt), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RequestWithdrawalHandler(balanceRepo, withdrawalRepo, pub.Object)
                .Handle(new RequestWithdrawalCommand(player.Id, "USDT", "BSC", ValidBscAddress, 100m), default));

        // Balance must be unchanged after failed withdrawal
        Assert.Equal(30m, await balanceRepo.GetBalanceAsync(player.Id, "USDT", "BSC"));
    }

    [Fact]
    public async Task Deposits_AccumulateCorrectly_AcrossMultipleCredits()
    {
        var pub = new Mock<IMessagePublisher>();
        var playerRepo    = new PlayerRepository(_db);
        var balanceRepo   = new BalanceRepository(_db);
        var depositRepo   = new DepositRepository(_db);
        var walletRepo    = new WalletRepository(_db);
        var walletKeyRepo = new WalletKeyRepository(_db);

        var player = await new RegisterPlayerHandler(playerRepo, pub.Object)
            .Handle(UniquePlayer("dave"), default);

        var handler = new CreditDepositHandler(depositRepo, balanceRepo, walletRepo, walletKeyRepo, pub.Object);

        for (int i = 0; i < 3; i++)
        {
            var evt = new DepositConfirmedEvent(player.Id, "USDT", "BSC", $"0xtx_{Guid.NewGuid():N}", 10m);
            await handler.Handle(new CreditDepositCommand(evt), default);
        }

        Assert.Equal(30m, await balanceRepo.GetBalanceAsync(player.Id, "USDT", "BSC"));
    }
}
