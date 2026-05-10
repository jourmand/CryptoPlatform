using CryptoPlatform.Application.UseCases;
using CryptoPlatform.Domain.Entities;
using CryptoPlatform.Domain.Messages;
using Moq;
using Xunit;

namespace CryptoPlatform.Tests.Unit.Handlers;

public class RegisterPlayerHandlerTests
{
    private readonly Mock<IPlayerRepository> _players = new();
    private readonly Mock<IMessagePublisher> _publisher = new();

    private RegisterPlayerHandler Sut() => new(_players.Object, _publisher.Object);

    [Fact]
    public async Task Handle_CreatesPlayerWithCorrectFields()
    {
        var created = new Player { Id = Guid.NewGuid(), Username = "alice", Email = "alice@x.com", CreatedAt = DateTime.UtcNow };
        _players.Setup(r => r.CreateAsync(It.IsAny<Player>(), default)).ReturnsAsync(created);
        _players.Setup(r => r.GetNextPlayerIndexAsync(default)).ReturnsAsync(0);

        var result = await Sut().Handle(new RegisterPlayerCommand("alice", "alice@x.com"), default);

        Assert.Equal(created.Id, result.Id);
        _players.Verify(r => r.CreateAsync(
            It.Is<Player>(p => p.Username == "alice" && p.Email == "alice@x.com" && p.Id != Guid.Empty),
            default), Times.Once);
    }

    [Fact]
    public async Task Handle_PublishesCreateWalletsCommandWithCorrectPlayerIdAndIndex()
    {
        var player = new Player { Id = Guid.NewGuid() };
        _players.Setup(r => r.CreateAsync(It.IsAny<Player>(), default)).ReturnsAsync(player);
        _players.Setup(r => r.GetNextPlayerIndexAsync(default)).ReturnsAsync(7);

        await Sut().Handle(new RegisterPlayerCommand("bob", "bob@x.com"), default);

        _publisher.Verify(p => p.PublishAsync("wallet.create",
            It.Is<CreateWalletsCommand>(c => c.PlayerId == player.Id && c.PlayerIndex == 7),
            default), Times.Once);
    }

    [Fact]
    public async Task Handle_ReturnsCreatedPlayer()
    {
        var player = new Player { Id = Guid.NewGuid(), Username = "carol" };
        _players.Setup(r => r.CreateAsync(It.IsAny<Player>(), default)).ReturnsAsync(player);
        _players.Setup(r => r.GetNextPlayerIndexAsync(default)).ReturnsAsync(0);

        var result = await Sut().Handle(new RegisterPlayerCommand("carol", "carol@x.com"), default);

        Assert.Same(player, result);
    }
}
