using CryptoPlatform.Application.UseCases;
using CryptoPlatform.Domain.Entities;
using CryptoPlatform.Domain.Messages;
using Moq;
using Xunit;

namespace CryptoPlatform.Tests.Unit.Handlers;

public class CreditDepositHandlerTests
{
    private readonly Mock<IDepositRepository> _deposits = new();
    private readonly Mock<IBalanceRepository> _balances = new();
    private readonly Mock<IWalletRepository> _wallets = new();
    private readonly Mock<IWalletKeyRepository> _walletKeys = new();
    private readonly Mock<IMessagePublisher> _publisher = new();

    private CreditDepositHandler Sut() => new(
        _deposits.Object, _balances.Object, _wallets.Object, _walletKeys.Object, _publisher.Object);

    private static DepositConfirmedEvent MakeEvent(Guid? playerId = null) => new(
        PlayerId: playerId ?? Guid.NewGuid(),
        Coin: "USDT",
        Chain: "BSC",
        TxHash: "0xabc123",
        Amount: 100m);

    [Fact]
    public async Task Handle_SkipsEverythingWhenDepositAlreadyCredited()
    {
        var evt = MakeEvent();
        _deposits.Setup(r => r.GetByTxHashAsync(evt.TxHash, default))
            .ReturnsAsync(new Deposit { Status = DepositStatus.Credited });

        await Sut().Handle(new CreditDepositCommand(evt), default);

        _balances.Verify(r => r.CreditAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<decimal>(), It.IsAny<string>(), default), Times.Never);
        _publisher.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<SweepCommand>(), default), Times.Never);
    }

    [Fact]
    public async Task Handle_CreditsBalanceForFreshDeposit()
    {
        var evt = MakeEvent();
        _deposits.Setup(r => r.GetByTxHashAsync(evt.TxHash, default)).ReturnsAsync((Deposit?)null);
        _deposits.Setup(r => r.CreateAsync(It.IsAny<Deposit>(), default))
            .ReturnsAsync((Deposit d, CancellationToken _) => d);
        _balances.Setup(r => r.CreditAsync(evt.PlayerId, "USDT", "BSC", 100m, evt.TxHash, default)).ReturnsAsync(100m);

        await Sut().Handle(new CreditDepositCommand(evt), default);

        _balances.Verify(r => r.CreditAsync(evt.PlayerId, "USDT", "BSC", 100m, evt.TxHash, default), Times.Once);
    }

    [Fact]
    public async Task Handle_CreatesDepositRecordAsCreditedWhenNoneExists()
    {
        var evt = MakeEvent();
        _deposits.Setup(r => r.GetByTxHashAsync(evt.TxHash, default)).ReturnsAsync((Deposit?)null);
        _balances.Setup(r => r.CreditAsync(evt.PlayerId, "USDT", "BSC", 100m, evt.TxHash, default)).ReturnsAsync(100m);

        Deposit? created = null;
        _deposits.Setup(r => r.CreateAsync(It.IsAny<Deposit>(), default))
            .Callback<Deposit, CancellationToken>((d, _) => created = d)
            .ReturnsAsync((Deposit d, CancellationToken _) => d);

        await Sut().Handle(new CreditDepositCommand(evt), default);

        Assert.NotNull(created);
        Assert.Equal(DepositStatus.Credited, created!.Status);
        Assert.Equal(evt.TxHash, created.TxHash);
        Assert.Equal(evt.PlayerId, created.PlayerId);
    }

    [Fact]
    public async Task Handle_UpdatesExistingDepositStatusToCredited()
    {
        var evt = MakeEvent();
        var existing = new Deposit { Status = DepositStatus.Confirmed, TxHash = evt.TxHash };
        _deposits.Setup(r => r.GetByTxHashAsync(evt.TxHash, default)).ReturnsAsync(existing);
        _balances.Setup(r => r.CreditAsync(evt.PlayerId, "USDT", "BSC", 100m, evt.TxHash, default)).ReturnsAsync(100m);

        await Sut().Handle(new CreditDepositCommand(evt), default);

        _deposits.Verify(r => r.UpdateStatusAsync(evt.TxHash, DepositStatus.Credited, default), Times.Once);
        _deposits.Verify(r => r.CreateAsync(It.IsAny<Deposit>(), default), Times.Never);
    }

    [Fact]
    public async Task Handle_PublishesSweepCommandWhenWalletAndKeyAvailable()
    {
        var evt = MakeEvent();
        _deposits.Setup(r => r.GetByTxHashAsync(evt.TxHash, default)).ReturnsAsync((Deposit?)null);
        _deposits.Setup(r => r.CreateAsync(It.IsAny<Deposit>(), default))
            .ReturnsAsync((Deposit d, CancellationToken _) => d);
        _balances.Setup(r => r.CreditAsync(evt.PlayerId, "USDT", "BSC", 100m, evt.TxHash, default)).ReturnsAsync(100m);

        var wallet = new PlayerWallet { PlayerId = evt.PlayerId, Coin = "USDT", Chain = "BSC", Address = "0xDepositWallet" };
        _wallets.Setup(r => r.GetByPlayerCoinChainAsync(evt.PlayerId, "USDT", "BSC", default)).ReturnsAsync(wallet);

        var key = new WalletKey { PlayerId = evt.PlayerId, ChainGroup = "EVM", EncryptedPrivateKey = "enc-key-xyz" };
        _walletKeys.Setup(r => r.GetByPlayerAndChainGroupAsync(evt.PlayerId, "EVM", default)).ReturnsAsync(key);

        await Sut().Handle(new CreditDepositCommand(evt), default);

        _publisher.Verify(p => p.PublishAsync("sweep.execute",
            It.Is<SweepCommand>(c =>
                c.PlayerId == evt.PlayerId &&
                c.FromAddress == wallet.Address &&
                c.EncryptedPrivateKey == key.EncryptedPrivateKey),
            default), Times.Once);
    }

    [Fact]
    public async Task Handle_SkipsSweepWhenNoWalletFound()
    {
        var evt = MakeEvent();
        _deposits.Setup(r => r.GetByTxHashAsync(evt.TxHash, default)).ReturnsAsync((Deposit?)null);
        _deposits.Setup(r => r.CreateAsync(It.IsAny<Deposit>(), default))
            .ReturnsAsync((Deposit d, CancellationToken _) => d);
        _balances.Setup(r => r.CreditAsync(evt.PlayerId, "USDT", "BSC", 100m, evt.TxHash, default)).ReturnsAsync(100m);
        _wallets.Setup(r => r.GetByPlayerCoinChainAsync(evt.PlayerId, "USDT", "BSC", default))
            .ReturnsAsync((PlayerWallet?)null);

        await Sut().Handle(new CreditDepositCommand(evt), default);

        _publisher.Verify(p => p.PublishAsync("sweep.execute", It.IsAny<SweepCommand>(), default), Times.Never);
    }
}
