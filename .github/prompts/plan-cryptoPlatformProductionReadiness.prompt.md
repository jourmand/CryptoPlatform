# CryptoPlatform Production Readiness Plan

## Phase 1 — Critical Security (Blockers)

1. **Add Authentication & Authorization**
   The API has zero auth. Any caller can register a player or submit a withdrawal for any `PlayerId`. Add JWT bearer auth to `Program.cs` and protect both controllers. The `WithdrawalsController` must verify `PlayerId` matches the authenticated user's identity.

2. **Remove the encryption key zero-fallback**
   In `node/blockchain-service/src/wallet/generator.ts`: `|| '0'.repeat(64)` silently uses 32 zero-bytes as the AES key — any deployment that forgets to set `ENCRYPTION_KEY` will generate wallets with cryptographically broken key storage. Change to throw a hard error at startup.

3. **Fix balance debit race condition**
   In `BalanceRepository.DebitAsync` (`dotnet/CryptoPlatform.Infrastructure/Repositories.cs`): "read balance → debit" is two separate DB ops with no lock. Two concurrent withdrawal requests for the same player can both pass the `GetBalanceAsync` check and both debit, going negative. Fix with a PostgreSQL `SELECT ... FOR UPDATE` or optimistic concurrency on the `Balance` row.

4. **Fix PlayerIndex race condition**
   `GetNextPlayerIndexAsync` uses `COUNT(Players)` — two simultaneous registrations get the same index → same HD wallet derivation path → same blockchain addresses for two different players. Fix with a PostgreSQL `SEQUENCE` or a DB-level advisory lock.

5. **Validate Coin/Chain combos and address format on withdrawal**
   The `WithdrawRequest` accepts any `Coin`, `Chain`, `ToAddress` with no validation. Add domain validation (e.g., USDT is only valid on Tron/Solana/BSC; a Tron address starting with `T` can't be used on BSC).

6. **Re-enable DI validation**
   `dotnet/CryptoPlatform.API/Program.cs`: `ValidateScopes = false, ValidateOnBuild = false` suppresses DI misconfiguration errors. Remove those options so the app fails fast on misconfigured services.

---

## Phase 2 — Missing Core Features (Required for the System to Function)

7. **Store encrypted wallet keys in DB**
   The `wallet_keys` table exists in `docker/postgres/init.sql` but there is **no `WalletKey` entity** in `dotnet/CryptoPlatform.Domain/Entities.cs`, no EF configuration in `AppDbContext.cs`, and no repository. The `WalletsCreatedEvent` in `Messages.cs` is also missing the `EncryptedKeys` field. The `.NET` consumer in `RabbitMqMessaging.cs` only saves wallet addresses — encrypted keys are dropped. This breaks the sweep flow entirely.

8. **Fix the sweep flow (encrypted keys must reach the sweep command)**
   `chainListeners.ts` triggers `sweep.execute` with no `encryptedPrivateKey` field. The sweep service in `sweepService.ts` requires it. Once keys are stored in DB (item 7), `.NET` should send the sweep command including the encrypted key after crediting a deposit.

9. **Handle `deposit.detected` events in .NET**
   Node publishes `deposit.detected` to RabbitMQ but `.NET` has **no consumer** for this queue. A `Deposit` record should be created at detection time (for audit) and updated through `Confirmed → Swept → Credited` states.

10. **Fix Solana SPL token listener**
    The Solana listener in `chainListeners.ts` uses `onAccountChange` on the wallet's native SOL account — this will NOT detect USDT or USDC transfers, which are SPL token transfers on separate Associated Token Accounts (ATAs). Must be replaced with per-mint ATA monitoring.

11. **Fix POL chain RPC URL**
    `sweepService.ts` uses `BSC_RPC_URL` for both BSC and POL chains. Add a `POL_RPC_URL` env var and use it for POL. Update `docker-compose.yml` accordingly.

12. **Restore watched addresses on restart**
    `watchedAddresses` is an in-memory `Map` in `index.ts` — on service restart, all existing players' wallets stop being monitored. On startup, load all existing wallet addresses from the DB (via a `.NET` API call or direct Redis cache). Redis is already in `docker-compose.yml` and `ioredis` is in `package.json` but not used.

---

## Phase 3 — Infrastructure & Reliability

13. **Replace `EnsureCreatedAsync` with EF Migrations**
    `Program.cs` uses `EnsureCreatedAsync()` — this will silently lose data on schema changes in production. Generate EF Core migrations and call `MigrateAsync()` at startup.

14. **Fix `ASPNETCORE_ENVIRONMENT` in docker-compose**
    `docker-compose.yml` sets `ASPNETCORE_ENVIRONMENT: Development` — this exposes Swagger publicly and runs `EnsureCreatedAsync`. Change to `Production`.

15. **Add RabbitMQ reconnection resilience**
    `RabbitMqPublisher` creates the connection in its constructor (DI will hang if RabbitMQ is slow), and neither the publisher nor consumer reconnects if RabbitMQ drops. Add retry logic with exponential backoff (Polly or manual) for both.

16. **Fix thread-unsafe RabbitMQ publisher channel**
    `RabbitMqPublisher` shares one `IModel` across concurrent HTTP requests — `IModel` is not thread-safe. Add a `SemaphoreSlim` guard or create per-publish channels.

17. **Add central wallet key validation at startup**
    `CENTRAL_KEY_EVM` and `CENTRAL_KEY_TRON` are loaded from env with no null check. If missing, the first withdrawal silently fails. Validate all required env vars at startup and fail fast.

---

## Phase 4 — API Completeness

18. **Add missing read endpoints** — there is no way for a player to see their deposit addresses, current balances, or transaction history:
    - `GET /api/players/{id}/wallets` — deposit addresses to show the player
    - `GET /api/players/{id}/balances` — current balances
    - `GET /api/players/{id}/deposits` and `/withdrawals` — history

19. **Add health check endpoints**
    Expose `/health` and `/ready` using `Microsoft.Extensions.Diagnostics.HealthChecks` with checks for PostgreSQL, RabbitMQ, and Redis.

20. **Add rate limiting on the withdrawal endpoint**
    Use `Microsoft.AspNetCore.RateLimiting` to prevent withdrawal endpoint abuse.

---

## Phase 5 — Observability & Testing

21. **Add structured logging to .NET** — replace default console logging with Serilog (JSON format, correlation IDs).
22. **Add unit tests** — at minimum cover `RequestWithdrawalHandler` (balance check, debit, publish), `CreditDepositHandler` (idempotency), and `RegisterPlayerHandler`.
23. **Add integration tests** — full deposit flow with Testcontainers (PostgreSQL + RabbitMQ).
24. **Add OpenTelemetry tracing** across .NET and Node.js services.

---

## Relevant Files

| File | Changes Needed |
|---|---|
| `dotnet/CryptoPlatform.API/Program.cs` | Auth, DI validation, migrations, health checks |
| `dotnet/CryptoPlatform.API/Controllers.cs` | Auth, input validation, GET endpoints |
| `dotnet/CryptoPlatform.Domain/Entities.cs` | Add `WalletKey` entity |
| `dotnet/CryptoPlatform.Domain/Messages.cs` | Add `EncryptedKeys` to `WalletsCreatedEvent`, add `SweepCommand` |
| `dotnet/CryptoPlatform.Application/UseCases.cs` | New handlers, `IWalletKeyRepository` interface |
| `dotnet/CryptoPlatform.Infrastructure/AppDbContext.cs` | Configure `WalletKey` entity |
| `dotnet/CryptoPlatform.Infrastructure/Repositories.cs` | `WalletKeyRepository`, fix `DebitAsync` locking |
| `dotnet/CryptoPlatform.Infrastructure/RabbitMqMessaging.cs` | `deposit.detected` consumer, thread safety, reconnection |
| `node/blockchain-service/src/wallet/generator.ts` | Remove zero-key fallback, startup env validation |
| `node/blockchain-service/src/listener/chainListeners.ts` | Fix Solana SPL, fix sweep command payload |
| `node/blockchain-service/src/sweep/sweepService.ts` | Fix POL RPC URL |
| `node/blockchain-service/src/index.ts` | Redis backing, startup wallet load, env validation |
| `docker-compose.yml` | Fix env, add `POL_RPC_URL`, `CENTRAL_WALLET_SOLANA` |

---

## Verification Checklist

- [ ] Register a player → 7 wallet addresses stored; encrypted keys stored in `wallet_keys` table
- [ ] Send testnet funds → `deposit.detected` saved → confirmed → balance credited → sweep tx visible on chain
- [ ] Two concurrent withdrawals exceeding balance → one 400, one 200 (race condition fixed)
- [ ] Two concurrent registrations → different HD addresses (index collision fixed)
- [ ] Restart blockchain service → existing wallets still monitored
- [ ] Missing `ENCRYPTION_KEY` env var → service refuses to start with a clear error
- [ ] `GET /health` returns 200 with all dependency checks green
