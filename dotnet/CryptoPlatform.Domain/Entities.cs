namespace CryptoPlatform.Domain.Entities;

public class Player
{
    public Guid Id { get; set; }
    public string Username { get; set; } = default!;
    public string Email { get; set; } = default!;
    public DateTime CreatedAt { get; set; }

    public ICollection<PlayerWallet> Wallets { get; set; } = new List<PlayerWallet>();
    public ICollection<Balance> Balances { get; set; } = new List<Balance>();
}

public class PlayerWallet
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Coin { get; set; } = default!;   // USDT, USDC, TRX, POL
    public string Chain { get; set; } = default!;  // Tron, Solana, BSC, POL
    public string Address { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public Player Player { get; set; } = default!;
}

public class Balance
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Coin { get; set; } = default!;
    public string Chain { get; set; } = default!;
    public decimal Amount { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Player Player { get; set; } = default!;
}

public class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public LedgerEntryType Type { get; set; }
    public string Coin { get; set; } = default!;
    public string Chain { get; set; } = default!;
    public decimal Amount { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? ReferenceId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Deposit
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Coin { get; set; } = default!;
    public string Chain { get; set; } = default!;
    public string TxHash { get; set; } = default!;
    public string? FromAddress { get; set; }
    public string ToAddress { get; set; } = default!;
    public decimal Amount { get; set; }
    public DepositStatus Status { get; set; }
    public int Confirmations { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CreditedAt { get; set; }
}

public class Withdrawal
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Coin { get; set; } = default!;
    public string Chain { get; set; } = default!;
    public string ToAddress { get; set; } = default!;
    public decimal Amount { get; set; }
    public decimal? Fee { get; set; }
    public string? TxHash { get; set; }
    public WithdrawalStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum LedgerEntryType { Deposit, Withdrawal, GameWin, GameLoss }
public enum DepositStatus   { Detected, Confirmed, Swept, Credited }
public enum WithdrawalStatus { Pending, Broadcasting, Completed, Failed }

public static class CoinChainValidator
{
    private static readonly Dictionary<string, HashSet<string>> ValidCombos = new()
    {
        ["USDT"] = new() { "Tron", "Solana", "BSC" },
        ["USDC"] = new() { "Solana", "BSC" },
        ["TRX"]  = new() { "Tron" },
        ["POL"]  = new() { "POL" },
    };

    public static void Validate(string coin, string chain, string toAddress)
    {
        if (!ValidCombos.TryGetValue(coin, out var validChains))
            throw new InvalidOperationException($"Unsupported coin: {coin}");

        if (!validChains.Contains(chain))
            throw new InvalidOperationException($"{coin} is not supported on {chain}");

        ValidateAddress(chain, toAddress);
    }

    private static void ValidateAddress(string chain, string address)
    {
        switch (chain)
        {
            case "Tron":
                if (address.Length != 34 || !address.StartsWith('T'))
                    throw new InvalidOperationException("Invalid Tron address: must start with 'T' and be 34 characters");
                break;
            case "Solana":
                if (address.Length < 32 || address.Length > 44)
                    throw new InvalidOperationException("Invalid Solana address: must be 32–44 characters");
                break;
            case "BSC":
            case "POL":
                if (address.Length != 42 || !address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Invalid {chain} address: must be a 42-character hex string starting with '0x'");
                break;
        }
    }
}
