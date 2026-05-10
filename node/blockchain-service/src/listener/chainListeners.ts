import { ethers } from 'ethers';
import { Connection, PublicKey } from '@solana/web3.js';
import TronWeb from 'tronweb';
import { publish } from '../messaging/rabbitmq';
import { logger } from '../index';

// ── Confirmation thresholds ────────────────────────────────────────
const CONFIRMATIONS_REQUIRED = {
  BSC:    12,
  POL:    32,
  Tron:   20,
  Solana:  1,  // Solana finality is ~400ms, 1 confirmed block is safe
};

// ── EVM listener (BSC + POL) ───────────────────────────────────────

const ERC20_TRANSFER_ABI = [
  'event Transfer(address indexed from, address indexed to, uint256 value)'
];

// Token contract addresses
const TOKEN_CONTRACTS: Record<string, Record<string, string>> = {
  BSC: {
    USDT: '0x55d398326f99059fF775485246999027B3197955',
    USDC: '0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d',
  },
  POL: {
    POL: '0x0000000000000000000000000000000000001010', // native
  },
};

export async function startEvmListener(
  chain: 'BSC' | 'POL',
  rpcUrl: string,
  watchedAddresses: Map<string, string> // address → playerId
): Promise<void> {
  const provider = new ethers.WebSocketProvider(rpcUrl);

  for (const [tokenSymbol, contractAddress] of Object.entries(TOKEN_CONTRACTS[chain] ?? {})) {
    const contract = new ethers.Contract(contractAddress, ERC20_TRANSFER_ABI, provider);

    contract.on('Transfer', async (from: string, to: string, value: bigint, event: ethers.EventLog) => {
      const toAddr  = to.toLowerCase();
      const playerId = watchedAddresses.get(toAddr);
      if (!playerId) return;

      const amount = parseFloat(ethers.formatUnits(value, 18));
      const receipt = await provider.getTransactionReceipt(event.transactionHash);
      if (!receipt) return;

      logger.info('Deposit detected', { chain, tokenSymbol, from, to, amount });

      await publish('blockchain.events', 'deposit.detected', {
        playerId,
        coin:          tokenSymbol,
        chain,
        txHash:        event.transactionHash,
        fromAddress:   from,
        toAddress:     to,
        amount,
        confirmations: receipt.confirmations ?? 0,
      });

      // Poll for confirmations
      pollConfirmations(provider, event.transactionHash, CONFIRMATIONS_REQUIRED[chain],
        async (confirmations) => {
          await publish('blockchain.events', 'deposit.confirmed', {
            playerId, coin: tokenSymbol, chain,
            txHash: event.transactionHash, amount,
          });
          // Also trigger sweep
          await publish('platform.commands', 'sweep.execute', {
            playerId, coin: tokenSymbol, chain,
            fromAddress: to, txHash: event.transactionHash, amount,
          });
        });
    });
  }

  logger.info('EVM listener started', { chain });
}

async function pollConfirmations(
  provider: ethers.WebSocketProvider,
  txHash: string,
  required: number,
  onConfirmed: (conf: number) => Promise<void>
): Promise<void> {
  const interval = setInterval(async () => {
    try {
      const receipt = await provider.getTransactionReceipt(txHash);
      if (receipt && (await receipt.confirmations()) >= required) {
        clearInterval(interval);
        await onConfirmed(await receipt.confirmations());
      }
    } catch (err) {
      logger.error('Confirmation polling error', { err, txHash });
    }
  }, 5000);
}

// ── Tron listener ──────────────────────────────────────────────────

export async function startTronListener(
  watchedAddresses: Map<string, string>
): Promise<void> {
  const tronWeb = new TronWeb({
    fullHost: process.env.TRON_API_URL!,
    headers:  { 'TRON-PRO-API-KEY': process.env.TRON_API_KEY! },
  });

  // Tron doesn't have WebSocket events — we poll using TRC20 transfer events
  setInterval(async () => {
    for (const [address, playerId] of watchedAddresses) {
      // Only poll addresses that are valid Tron base58 addresses (start with uppercase 'T', ~34 chars)
      if (!address.startsWith('T') || address.length < 30) continue;
      try {
        const txs = await tronWeb.trx.getTransactionsRelated(address, 'to', 10, 0);
        for (const tx of txs?.data ?? []) {
          const txHash = tx.txID;
          // Process TRX native transfers
          if (tx.raw_data?.contract?.[0]?.type === 'TransferContract') {
            const value  = tx.raw_data.contract[0].parameter.value;
            const amount = value.amount / 1_000_000; // sun → TRX
            await publish('blockchain.events', 'deposit.detected', {
              playerId, coin: 'TRX', chain: 'Tron',
              txHash, fromAddress: tronWeb.address.fromHex(value.owner_address),
              toAddress: address, amount, confirmations: 0,
            });
          }
        }
      } catch (err) {
        logger.error('Tron polling error', { err, address });
      }
    }
  }, 3000); // poll every 3 seconds

  logger.info('Tron listener started');
}

// ── Solana listener ────────────────────────────────────────────────

export async function startSolanaListener(
  watchedAddresses: Map<string, string>
): Promise<void> {
  const connection = new Connection(process.env.SOLANA_RPC_URL!, 'confirmed');

  for (const [address, playerId] of watchedAddresses) {
    const pubkey = new PublicKey(address);

    connection.onAccountChange(pubkey, async (accountInfo, context) => {
      logger.info('Solana account changed', { address, playerId, slot: context.slot });
      // Note: For SPL token (USDT/USDC) transfers, subscribe to token accounts
      // For brevity, full SPL token handling would be added here
      await publish('blockchain.events', 'deposit.detected', {
        playerId, coin: 'USDC', chain: 'Solana',
        txHash: `sol-${context.slot}-${address}`,
        toAddress: address, amount: 0, confirmations: 1,
      });
    }, 'confirmed');
  }

  logger.info('Solana listener started');
}
