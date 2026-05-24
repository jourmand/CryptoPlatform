import { ethers } from 'ethers';
import { AccountInfo, Connection, PublicKey } from '@solana/web3.js';
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
      const toAddr   = to.toLowerCase();
      const playerId = watchedAddresses.get(toAddr);
      if (!playerId) return;

      const amount  = parseFloat(ethers.formatUnits(value, 18));
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

      // Poll for confirmations; .NET will send the sweep.execute command after crediting
      pollConfirmations(provider, event.transactionHash, CONFIRMATIONS_REQUIRED[chain],
        async () => {
          await publish('blockchain.events', 'deposit.confirmed', {
            playerId, coin: tokenSymbol, chain,
            txHash: event.transactionHash, amount,
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

  setInterval(async () => {
    for (const [address, playerId] of watchedAddresses) {
      if (!address.startsWith('T') || address.length < 30) continue;
      try {
        const txs = await tronWeb.trx.getTransactionsRelated(address, 'to', 10, 0);
        for (const tx of txs?.data ?? []) {
          const txHash = tx.txID;
          if (tx.raw_data?.contract?.[0]?.type === 'TransferContract') {
            const value  = tx.raw_data.contract[0].parameter.value;
            const amount = value.amount / 1_000_000;
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
  }, 3000);

  logger.info('Tron listener started');
}

// ── Solana SPL token listener ──────────────────────────────────────
// Monitors Associated Token Accounts (ATAs) for USDT and USDC.
// The wallet's native SOL account does NOT receive SPL transfers —
// each SPL mint has a separate ATA that must be subscribed individually.

const TOKEN_PROGRAM_ID          = new PublicKey('TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA');
const ASSOCIATED_TOKEN_PROGRAM_ID = new PublicKey('ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJe8bv');

const SPL_MINTS: { coin: string; mint: string; decimals: number }[] = [
  { coin: 'USDT', mint: 'Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB', decimals: 6 },
  { coin: 'USDC', mint: 'EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v', decimals: 6 },
];

function deriveAta(wallet: PublicKey, mint: PublicKey): PublicKey {
  const [ata] = PublicKey.findProgramAddressSync(
    [wallet.toBuffer(), TOKEN_PROGRAM_ID.toBuffer(), mint.toBuffer()],
    ASSOCIATED_TOKEN_PROGRAM_ID
  );
  return ata;
}

function readTokenBalance(data: Buffer): bigint {
  // SPL Token Account layout: mint(32) + owner(32) + amount(8 LE u64) + ...
  if (data.length < 72) return 0n;
  return data.readBigUInt64LE(64);
}

function subscribeWalletAtas(
  connection: Connection,
  walletAddress: string,
  playerId: string
): void {
  const walletPubkey = new PublicKey(walletAddress);

  for (const { coin, mint, decimals } of SPL_MINTS) {
    const mintPubkey   = new PublicKey(mint);
    const ata          = deriveAta(walletPubkey, mintPubkey);
    const ataAddress   = ata.toBase58();
    let   prevBalance  = 0n;

    connection.onAccountChange(ata, async (accountInfo: AccountInfo<Buffer>, context) => {
      const currentBalance = readTokenBalance(accountInfo.data as Buffer);

      if (currentBalance <= prevBalance) {
        prevBalance = currentBalance;
        return;
      }

      const rawDelta = currentBalance - prevBalance;
      prevBalance    = currentBalance;
      const amount   = Number(rawDelta) / Math.pow(10, decimals);

      // Use latest transaction signature on the ATA as a stable txHash
      let txHash = `sol-${context.slot}-${ataAddress}`;
      try {
        const sigs = await connection.getSignaturesForAddress(ata, { limit: 1 });
        if (sigs[0]) txHash = sigs[0].signature;
      } catch {
        // fallback to slot-based id if RPC call fails
      }

      logger.info('Solana SPL deposit detected', { coin, walletAddress, amount, txHash });

      await publish('blockchain.events', 'deposit.detected', {
        playerId, coin, chain: 'Solana',
        txHash, fromAddress: '', toAddress: walletAddress,
        amount, confirmations: 1,
      });

      await publish('blockchain.events', 'deposit.confirmed', {
        playerId, coin, chain: 'Solana', txHash, amount,
      });
    }, 'confirmed');
  }
}

export async function startSolanaListener(
  watchedAddresses: Map<string, string>
): Promise<(address: string, playerId: string) => void> {
  const connection = new Connection(process.env.SOLANA_RPC_URL!, 'confirmed');

  for (const [address, playerId] of watchedAddresses) {
    subscribeWalletAtas(connection, address, playerId);
  }

  logger.info('Solana listener started');

  // Return a function to subscribe new wallets added after startup
  return (address: string, playerId: string) => {
    subscribeWalletAtas(connection, address, playerId);
  };
}
