# CryptoPlatform — Hybrid Architecture

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                        Docker Network                        │
│                                                             │
│  ┌──────────────┐    ┌─────────────┐    ┌───────────────┐  │
│  │  .NET API    │    │  RabbitMQ   │    │ Node.js       │  │
│  │  (C# / EF)  │◄──►│  (broker)   │◄──►│ Blockchain    │  │
│  │             │    │             │    │ Service       │  │
│  └──────┬───┘    └─────────────┘    └───────────────┘  │
│         │                                                    │
│  ┌──────▼──────┐    ┌─────────────┐                         │
│  │  PostgreSQL │    │    Redis     │                         │
│  │  (ledger)   │    │  (cache)    │                         │
│  └─────────────┘    └─────────────┘                         │
└─────────────────────────────────────────────────────────────┘
```

## Message Flow

### Deposit (happy path)
```
Player sends crypto
  → Node listener detects tx
  → Node publishes deposit.detected → RabbitMQ
  → Node polls confirmations
  → Node publishes deposit.confirmed → RabbitMQ
  → .NET consumer credits player balance in PostgreSQL
  → Node receives sweep.execute command
  → Node moves funds to central wallet
```

### Withdrawal
```
Player requests withdraw via .NET API
  → .NET debits balance, creates withdrawal record
  → .NET publishes withdrawal.execute → RabbitMQ
  → Node broadcasts tx from central wallet
  → Node publishes withdrawal.completed → RabbitMQ
  → .NET marks withdrawal as complete
```

## Quickstart

```bash
# 1. Copy env file and fill in your RPC URLs and API keys
cp .env.example .env

# 2. Start everything
docker compose up -d

# 3. Check services
docker compose ps

# 4. View logs
docker compose logs -f blockchain-service
docker compose logs -f api

# 5. RabbitMQ management UI
open http://localhost:15672  # rabbit / rabbitpass

# 6. API (Swagger)
open http://localhost:5000/swagger
```

## Projects

| Project | Tech | Responsibility |
|---|---|---|
| `dotnet/CryptoPlatform.API` | ASP.NET 8 | REST API, controllers |
| `dotnet/CryptoPlatform.Application` | C# / MediatR | Business logic, use cases |
| `dotnet/CryptoPlatform.Domain` | C# | Entities, message contracts |
| `dotnet/CryptoPlatform.Infrastructure` | EF Core / RabbitMQ | DB, messaging |
| `node/blockchain-service` | Node.js / TypeScript | Wallets, listeners, sweep, withdrawals |

## Queues (RabbitMQ)

| Queue | Direction | Purpose |
|---|---|---|
| `wallet.create` | .NET → Node | New player registered, create wallets |
| `sweep.execute` | .NET → Node | Deposit confirmed, sweep to central |
| `withdrawal.execute` | .NET → Node | Player withdrawal approved |
| `deposit.detected` | Node → .NET | Incoming tx seen on-chain |
| `deposit.confirmed` | Node → .NET | Tx has enough confirmations |
| `withdrawal.completed` | Node → .NET | On-chain tx broadcast |

## Environment Variables

See `.env.example` for all required variables. Critical ones:

- `ENCRYPTION_KEY` — 32-byte hex key for AES-256-GCM encryption of private keys
- `BSC_RPC_URL` — Use a paid provider (QuickNode/Alchemy) for production
- `TRON_API_KEY` — TronGrid API key
- `CENTRAL_KEY_EVM` / `CENTRAL_KEY_TRON` — Central hot wallet private keys (use HSM in production)
