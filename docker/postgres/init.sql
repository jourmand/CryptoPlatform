-- Players
CREATE TABLE players (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username VARCHAR(100) NOT NULL UNIQUE,
    email VARCHAR(255) NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Target wallets (one per player per coin/chain)
CREATE TABLE player_wallets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id),
    coin VARCHAR(10) NOT NULL,        -- USDT, USDC, TRX, POL
    chain VARCHAR(20) NOT NULL,       -- Tron, Solana, BSC, POL
    address VARCHAR(255) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(address, coin),
    UNIQUE(player_id, coin, chain)
);

-- Encrypted private keys (stored separately from wallet addresses)
CREATE TABLE wallet_keys (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id),
    chain_group VARCHAR(20) NOT NULL,  -- EVM, Tron, Solana
    encrypted_private_key TEXT NOT NULL,
    encrypted_mnemonic TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(player_id, chain_group)
);

-- Internal player balances (double-entry ledger)
CREATE TABLE balances (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id),
    coin VARCHAR(10) NOT NULL,
    chain VARCHAR(20) NOT NULL,
    amount NUMERIC(28, 8) NOT NULL DEFAULT 0 CHECK (amount >= 0),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(player_id, coin, chain)
);

-- Ledger transactions (full audit trail)
CREATE TABLE ledger_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id),
    type VARCHAR(20) NOT NULL,         -- DEPOSIT, WITHDRAWAL, GAME_WIN, GAME_LOSS
    coin VARCHAR(10) NOT NULL,
    chain VARCHAR(20) NOT NULL,
    amount NUMERIC(28, 8) NOT NULL,
    balance_before NUMERIC(28, 8) NOT NULL,
    balance_after NUMERIC(28, 8) NOT NULL,
    reference_id VARCHAR(255),         -- tx hash or game round ID
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- On-chain deposit tracking
CREATE TABLE deposits (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id),
    coin VARCHAR(10) NOT NULL,
    chain VARCHAR(20) NOT NULL,
    tx_hash VARCHAR(255) NOT NULL UNIQUE,
    from_address VARCHAR(255),
    to_address VARCHAR(255) NOT NULL,
    amount NUMERIC(28, 8) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'DETECTED',  -- DETECTED, CONFIRMED, SWEPT, CREDITED
    confirmations INT NOT NULL DEFAULT 0,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    confirmed_at TIMESTAMPTZ,
    credited_at TIMESTAMPTZ
);

-- On-chain withdrawal tracking
CREATE TABLE withdrawals (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id),
    coin VARCHAR(10) NOT NULL,
    chain VARCHAR(20) NOT NULL,
    to_address VARCHAR(255) NOT NULL,
    amount NUMERIC(28, 8) NOT NULL,
    fee NUMERIC(28, 8),
    tx_hash VARCHAR(255),
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING',   -- PENDING, BROADCASTING, COMPLETED, FAILED
    requested_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ
);

-- Indexes
CREATE INDEX idx_player_wallets_player ON player_wallets(player_id);
CREATE INDEX idx_player_wallets_address ON player_wallets(address);
CREATE INDEX idx_deposits_tx_hash ON deposits(tx_hash);
CREATE INDEX idx_deposits_status ON deposits(status);
CREATE INDEX idx_withdrawals_player ON withdrawals(player_id);
CREATE INDEX idx_withdrawals_status ON withdrawals(status);
CREATE INDEX idx_ledger_player ON ledger_entries(player_id);
