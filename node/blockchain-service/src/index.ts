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

// In-memory map: wallet address → playerId (loaded from DB via .NET on startup)
// In production, back this with Redis for multi-instance support
const watchedAddresses = new Map<string, string>();

async function main() {
  logger.info('Blockchain service starting...');

  await connectRabbitMQ();

  // ── Listen for commands from .NET ──────────────────────────────

  // 1. Create wallets when a new player registers
  await subscribe('wallet.create', async (msg: any) => {
    logger.info('Creating wallets', { playerId: msg.PlayerId });
    const wallets = await generateWallets(msg.PlayerId, msg.PlayerIndex);

    // Register all addresses for listening.
    // Tron addresses (Base58, start with 'T') must keep original case; EVM/Solana are lowercased.
    const addrs = wallets.addresses;
    const tronAddrs = [addrs.usdtTron, addrs.trxTron];
    const otherAddrs = [addrs.usdtSolana, addrs.usdtBsc, addrs.usdcSolana, addrs.usdcBsc, addrs.polPol];
    tronAddrs.forEach(addr => watchedAddresses.set(addr, msg.PlayerId));
    otherAddrs.forEach(addr => watchedAddresses.set(addr.toLowerCase(), msg.PlayerId));

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

  // 2. Execute sweep when .NET confirms deposit
  await subscribe('sweep.execute', async (msg: any) => {
    logger.info('Sweeping funds', { playerId: msg.playerId, coin: msg.coin });
    try {
      const txHash = await sweepFunds({
        playerId:            msg.playerId,
        coin:                msg.coin,
        chain:               msg.chain,
        fromAddress:         msg.fromAddress,
        encryptedPrivateKey: msg.encryptedPrivateKey,
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

  if (process.env.TRON_API_URL || process.env.TRON_API_KEY) {
    await startTronListener(watchedAddresses);
  } else {
    logger.info('TRON_API_URL/KEY not set — Tron listener skipped');
  }

  if (process.env.SOLANA_RPC_URL) {
    await startSolanaListener(watchedAddresses);
  } else {
    logger.info('SOLANA_RPC_URL not set — Solana listener skipped');
  }

  logger.info('Blockchain service ready — listening for deposits and commands');
}

main().catch(err => {
  const message = err instanceof Error ? err.message : String(err);
  const stack   = err instanceof Error ? err.stack : undefined;
  logger.error('Fatal startup error', { message, stack });
  process.exit(1);
});
