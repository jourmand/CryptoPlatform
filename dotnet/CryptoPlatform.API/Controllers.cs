using System.Security.Claims;
using CryptoPlatform.Application.UseCases;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoPlatform.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PlayersController : ControllerBase
{
    private readonly IMediator _mediator;
    public PlayersController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        var player = await _mediator.Send(
            new RegisterPlayerCommand(req.Username, req.Email), ct);
        return CreatedAtAction(nameof(Register), new { id = player.Id }, player);
    }

    public record RegisterRequest(string Username, string Email);
}

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class WithdrawalsController : ControllerBase
{
    private readonly IMediator _mediator;
    public WithdrawalsController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> RequestWithdrawal(
        [FromBody] WithdrawRequest req, CancellationToken ct)
    {
        // Enforce that the caller can only withdraw from their own account
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sub is null || !Guid.TryParse(sub, out var jwtPlayerId) || jwtPlayerId != req.PlayerId)
            return Forbid();

        try
        {
            var withdrawal = await _mediator.Send(
                new RequestWithdrawalCommand(
                    req.PlayerId, req.Coin, req.Chain,
                    req.ToAddress, req.Amount), ct);
            return Ok(withdrawal);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public record WithdrawRequest(
        Guid    PlayerId,
        string  Coin,
        string  Chain,
        string  ToAddress,
        decimal Amount
    );
}
