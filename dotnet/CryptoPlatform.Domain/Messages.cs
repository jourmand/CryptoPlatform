namespace CryptoPlatform.Domain.Messages;

// ── Commands (.NET → Node) ─────────────────────────────────────────

/// Sent when a new player registers — Node creates all wallets
public record CreateWalletsCommand(
    Guid PlayerId,
    int  PlayerIndex   // deterministic index for HD derivation
);

/// Sent when .NET approves a withdrawal — Node broadcasts the tx
public record ExecuteWithdrawalCommand(
    Guid   WithdrawalId,
    Guid   PlayerId,
    string Coin,
    string Chain,
    string ToAddress,
    decimal Amount
);

// ── Events (Node → .NET) ───────────────────────────────────────────

/// Node finished creating wallets — .NET stores addresses and encrypted keys in DB
public record WalletsCreatedEvent(
    Guid   PlayerId,
    WalletAddresses Addresses,
    WalletEncryptedKeys EncryptedKeys
);

public record WalletEncryptedKeys(
    string Evm,
    string Tron,
    string Solana
);

public record WalletAddresses(
    string UsdtTron,
    string UsdtSolana,
    string UsdtBsc,
    string UsdcSolana,
    string UsdcBsc,
    string TrxTron,
    string PolPol
);

/// Node detected incoming funds on a target wallet
public record DepositDetectedEvent(
    Guid    PlayerId,
    string  Coin,
    string  Chain,
    string  TxHash,
    string  FromAddress,
    string  ToAddress,
    decimal Amount,
    int     Confirmations
);

/// Node confirmed deposit has enough block confirmations
public record DepositConfirmedEvent(
    Guid    PlayerId,
    string  Coin,
    string  Chain,
    string  TxHash,
    decimal Amount
);

/// Node completed an on-chain withdrawal
public record WithdrawalCompletedEvent(
    Guid   WithdrawalId,
    string TxHash,
    decimal Fee
);

/// Node failed to broadcast a withdrawal
public record WithdrawalFailedEvent(
    Guid   WithdrawalId,
    string Reason
);

/// .NET tells Node to sweep deposited funds from player wallet to central wallet
public record SweepCommand(
    Guid   PlayerId,
    string Coin,
    string Chain,
    string FromAddress,
    string EncryptedPrivateKey
);
