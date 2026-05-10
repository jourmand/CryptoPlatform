using CryptoPlatform.Application.UseCases;
using CryptoPlatform.Domain.Entities;
using CryptoPlatform.Domain.Messages;
using Moq;
using Xunit;

namespace CryptoPlatform.Tests.Unit.Handlers;

public class RequestWithdrawalHandlerTests
{
    private readonly Mock<IBalanceRepository> _balances = new();
    private readonly Mock<IWithdrawalRepository> _withdrawals = new();
    private readonly Mock<IMessagePublisher> _publisher = new();

    private RequestWithdrawalHandler Sut() => new(_balances.Object, _withdrawals.Object, _publisher.Object);

    private static readonly string ValidBscAddress = "0x" + new string('A', 40);

    [Fact]
    public async Task Handle_ThrowsOnInvalidCoinChainCombo()
    {
        // POL coin only valid on POL chain, not BSC
        var cmd = new RequestWithdrawalCommand(Guid.NewGuid(), "POL", "BSC", ValidBscAddress, 1m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().Handle(cmd, default));
    }

    [Fact]
    public async Task Handle_ThrowsOnInvalidAddressFormat()
    {
        // BSC address must start with 0x and be 42 chars
        var cmd = new RequestWithdrawalCommand(Guid.NewGuid(), "USDT", "BSC", "not-a-bsc-address", 1m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().Handle(cmd, default));
    }

    [Fact]
    public async Task Handle_ThrowsWhenBalanceInsufficient()
    {
        var playerId = Guid.NewGuid();
        _balances.Setup(r => r.GetBalanceAsync(playerId, "USDT", "BSC", default)).ReturnsAsync(50m);

        var cmd = new RequestWithdrawalCommand(playerId, "USDT", "BSC", ValidBscAddress, 100m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().Handle(cmd, default));
    }

    [Fact]
    public async Task Handle_DebitsBalanceWhenSufficientFunds()
    {
        var playerId = Guid.NewGuid();
        _balances.Setup(r => r.GetBalanceAsync(playerId, "USDT", "BSC", default)).ReturnsAsync(200m);
        _balances.Setup(r => r.DebitAsync(playerId, "USDT", "BSC", 50m, It.IsAny<Guid>(), default)).ReturnsAsync(150m);
        _withdrawals.Setup(r => r.CreateAsync(It.IsAny<Withdrawal>(), default))
            .ReturnsAsync((Withdrawal w, CancellationToken _) => w);

        await Sut().Handle(new RequestWithdrawalCommand(playerId, "USDT", "BSC", ValidBscAddress, 50m), default);

        _balances.Verify(r => r.DebitAsync(playerId, "USDT", "BSC", 50m, It.IsAny<Guid>(), default), Times.Once);
    }

    [Fact]
    public async Task Handle_CreatesWithdrawalRecordWithCorrectFields()
    {
        var playerId = Guid.NewGuid();
        _balances.Setup(r => r.GetBalanceAsync(playerId, "USDT", "BSC", default)).ReturnsAsync(200m);
        _balances.Setup(r => r.DebitAsync(playerId, "USDT", "BSC", 75m, It.IsAny<Guid>(), default)).ReturnsAsync(125m);

        Withdrawal? captured = null;
        _withdrawals.Setup(r => r.CreateAsync(It.IsAny<Withdrawal>(), default))
            .Callback<Withdrawal, CancellationToken>((w, _) => captured = w)
            .ReturnsAsync((Withdrawal w, CancellationToken _) => w);

        await Sut().Handle(new RequestWithdrawalCommand(playerId, "USDT", "BSC", ValidBscAddress, 75m), default);

        Assert.NotNull(captured);
        Assert.Equal(playerId, captured!.PlayerId);
        Assert.Equal("USDT", captured.Coin);
        Assert.Equal("BSC", captured.Chain);
        Assert.Equal(ValidBscAddress, captured.ToAddress);
        Assert.Equal(75m, captured.Amount);
        Assert.Equal(WithdrawalStatus.Pending, captured.Status);
    }

    [Fact]
    public async Task Handle_PublishesWithdrawalExecuteCommandWithCorrectIds()
    {
        var playerId = Guid.NewGuid();
        _balances.Setup(r => r.GetBalanceAsync(playerId, "USDT", "BSC", default)).ReturnsAsync(200m);
        _balances.Setup(r => r.DebitAsync(playerId, "USDT", "BSC", 50m, It.IsAny<Guid>(), default)).ReturnsAsync(150m);

        Withdrawal? stored = null;
        _withdrawals.Setup(r => r.CreateAsync(It.IsAny<Withdrawal>(), default))
            .Callback<Withdrawal, CancellationToken>((w, _) => stored = w)
            .ReturnsAsync((Withdrawal w, CancellationToken _) => w);

        await Sut().Handle(new RequestWithdrawalCommand(playerId, "USDT", "BSC", ValidBscAddress, 50m), default);

        _publisher.Verify(p => p.PublishAsync("withdrawal.execute",
            It.Is<ExecuteWithdrawalCommand>(c => c.WithdrawalId == stored!.Id && c.PlayerId == playerId),
            default), Times.Once);
    }
}
