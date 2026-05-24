import './tracing'; // must be first — patches modules before they are loaded
import Redis from 'ioredis';
import winston from 'winston';
import { connectRabbitMQ, subscribe, publish } from './messaging/rabbitmq';
import { generateWallets } from './wallet/generator';
import { startEvmListener, startTronListener, startSolanaListener } from './listener/chainListeners';
import { sweepFunds } from './sweep/sweepService';
import { executeWithdrawal } from './withdrawal/withdrawalService';

export const logger = winston.createLogger({
  level: 'info',
  format: winston.format.combine(
    winston.format.timestamp(),
    winston.format.json()
  ),
  transports: [new winston.transports.Console()],
});

// In-memory map: wallet address → playerId
// Backed by Redis so addresses survive service restarts.
const watchedAddresses = new Map<string, string>();

const REDIS_ADDRESS_KEY = 'watched_addresses';
let redis: Redis;

async function connectRedis(): Promise<void> {
  redis = new Redis(process.env.REDIS_URL!);
  redis.on('error', (err) => logger.error('Redis error', { err }));
  await redis.ping();
  logger.info('Redis connected');
}

async function loadWatchedAddresses(): Promise<void> {
  const data = await redis.hgetall(REDIS_ADDRESS_KEY);
  for (const [address, playerId] of Object.entries(data)) {
    watchedAddresses.set(address, playerId);
  }
  logger.info(`Restored ${watchedAddresses.size} watched addresses from Redis`);
}

async function persistAddress(address: string, playerId: string): Promise<void> {
  watchedAddresses.set(address, playerId);
  await redis.hset(REDIS_ADDRESS_KEY, address, playerId);
}

function validateRequiredEnvVars(): void {
  const required = [
    'RABBITMQ_URL',
    'REDIS_URL',
    'ENCRYPTION_KEY',
    'CENTRAL_KEY_EVM',
    'CENTRAL_KEY_TRON',
    'CENTRAL_WALLET_EVM',
    'CENTRAL_WALLET_TRON',
    'CENTRAL_WALLET_SOLANA',
  ];
  const missing = required.filter(key => !process.env[key]);
  if (missing.length > 0) {
    throw new Error(`Missing required environment variables: ${missing.join(', ')}`);
  }
}

async function main() {
  validateRequiredEnvVars();
  logger.info('Blockchain service starting...');

  await connectRedis();
  await connectRabbitMQ();

  // Restore watched addresses before starting listeners so existing wallets are monitored
  await loadWatchedAddresses();

  // ── Listen for commands from .NET ──────────────────────────────

  // 1. Create wallets when a new player registers
  await subscribe('wallet.create', async (msg: any) => {
    logger.info('Creating wallets', { playerId: msg.PlayerId });
    const wallets = await generateWallets(msg.PlayerId, msg.PlayerIndex);

    // Persist all addresses to Redis and in-memory map
    const addrs     = wallets.addresses;
    const tronAddrs = [addrs.usdtTron, addrs.trxTron];
    const otherAddrs = [addrs.usdtSolana, addrs.usdtBsc, addrs.usdcSolana, addrs.usdcBsc, addrs.polPol];

    for (const addr of tronAddrs)  await persistAddress(addr, msg.PlayerId);
    for (const addr of otherAddrs) await persistAddress(addr.toLowerCase(), msg.PlayerId);

    // Subscribe new Solana ATAs immediately so we don't miss deposits
    if (addSolanaWatch) {
      addSolanaWatch(addrs.usdtSolana, msg.PlayerId);
    }

    // Send addresses + encrypted keys back to .NET
    await publish('blockchain.events', 'wallet.create', {
      PlayerId:  msg.PlayerId,
      Addresses: {
        UsdtTron:   wallets.addresses.usdtTron,
        UsdtSolana: wallets.addresses.usdtSolana,
        UsdtBsc:    wallets.addresses.usdtBsc,
        UsdcSolana: wallets.addresses.usdcSolana,
        UsdcBsc:    wallets.addresses.usdcBsc,
        TrxTron:    wallets.addresses.trxTron,
        PolPol:     wallets.addresses.polPol,
      },
      EncryptedKeys: wallets.encryptedKeys,
    });
  });

  // 2. Execute sweep when .NET sends the command after crediting a deposit
  await subscribe('sweep.execute', async (msg: any) => {
    logger.info('Sweeping funds', { playerId: msg.PlayerId, coin: msg.Coin });
    try {
      const txHash = await sweepFunds({
        playerId:            msg.PlayerId,
        coin:                msg.Coin,
        chain:               msg.Chain,
        fromAddress:         msg.FromAddress,
        encryptedPrivateKey: msg.EncryptedPrivateKey,
      });
      logger.info('Sweep successful', { txHash });
    } catch (err) {
      logger.error('Sweep failed', { err, msg });
    }
  });

  // 3. Execute withdrawal when .NET approves
  await subscribe('withdrawal.execute', async (msg: any) => {
    logger.info('Executing withdrawal', { withdrawalId: msg.WithdrawalId });
    try {
      const result = await executeWithdrawal({
        withdrawalId: msg.WithdrawalId,
        coin:         msg.Coin,
        chain:        msg.Chain,
        toAddress:    msg.ToAddress,
        amount:       msg.Amount,
      });
      await publish('blockchain.events', 'withdrawal.completed', {
        WithdrawalId: msg.WithdrawalId,
        TxHash:       result.txHash,
        Fee:          result.fee,
      });
    } catch (err: any) {
      await publish('blockchain.events', 'withdrawal.completed', {
        WithdrawalId: msg.WithdrawalId,
        Reason:       err.message,
      });
    }
  });

  // ── Start chain listeners (skipped if RPC URL not configured) ──
  if (process.env.BSC_RPC_URL) {
    const bscWss = process.env.BSC_RPC_URL.replace(/^https/, 'wss').replace(/^http/, 'ws');
    await startEvmListener('BSC', bscWss, watchedAddresses);
  } else {
    logger.info('BSC_RPC_URL not set — BSC listener skipped');
  }

  if (process.env.POL_RPC_URL) {
    const polWss = process.env.POL_RPC_URL.replace(/^https/, 'wss').replace(/^http/, 'ws');
    await startEvmListener('POL', polWss, watchedAddresses);
  } else {
    logger.info('POL_RPC_URL not set — POL listener skipped');
  }

  if (process.env.TRON_API_URL || process.env.TRON_API_KEY) {
    await startTronListener(watchedAddresses);
  } else {
    logger.info('TRON_API_URL/KEY not set — Tron listener skipped');
  }

  if (process.env.SOLANA_RPC_URL) {
    addSolanaWatch = await startSolanaListener(watchedAddresses);
  } else {
    logger.info('SOLANA_RPC_URL not set — Solana listener skipped');
  }

  logger.info('Blockchain service ready — listening for deposits and commands');
}

// Populated by startSolanaListener; used by wallet.create handler to subscribe new wallets
let addSolanaWatch: ((address: string, playerId: string) => void) | null = null;

main().catch(err => {
  const message = err instanceof Error ? err.message : String(err);
  const stack   = err instanceof Error ? err.stack : undefined;
  logger.error('Fatal startup error', { message, stack });
  process.exit(1);
});
