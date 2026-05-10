import { ethers } from 'ethers';
import TronWeb from 'tronweb';
import { decrypt } from '../wallet/generator';
import { logger } from '../index';

const ERC20_ABI = [
  'function transfer(address to, uint256 amount) returns (bool)',
  'function balanceOf(address) view returns (uint256)',
];

const CENTRAL_WALLETS = {
  EVM:    process.env.CENTRAL_WALLET_EVM!,    // 0x...
  Tron:   process.env.CENTRAL_WALLET_TRON!,   // T...
  Solana: process.env.CENTRAL_WALLET_SOLANA!, // base58
};

const TOKEN_CONTRACTS: Record<string, string> = {
  'USDT.BSC':  '0x55d398326f99059fF775485246999027B3197955',
  'USDC.BSC':  '0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d',
  'USDT.Tron': 'TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t',
};

export interface SweepRequest {
  playerId:    string;
  coin:        string;
  chain:       string;
  fromAddress: string;
  encryptedPrivateKey: string;
}

export async function sweepFunds(req: SweepRequest): Promise<string> {
  const { coin, chain } = req;

  if (chain === 'BSC' || chain === 'POL') {
    return sweepEVM(req);
  } else if (chain === 'Tron') {
    return sweepTron(req);
  }
  throw new Error(`Unsupported chain for sweep: ${chain}`);
}

async function sweepEVM(req: SweepRequest): Promise<string> {
  const privateKey = decrypt(req.encryptedPrivateKey);
  const provider   = new ethers.JsonRpcProvider(process.env.BSC_RPC_URL!);
  const signer     = new ethers.Wallet(privateKey, provider);

  const contractAddr = TOKEN_CONTRACTS[`${req.coin}.${req.chain}`];
  const contract     = new ethers.Contract(contractAddr, ERC20_ABI, signer);

  const balance = await contract.balanceOf(req.fromAddress);
  if (balance === 0n) throw new Error('Zero balance — nothing to sweep');

  // Estimate gas and ensure enough BNB/MATIC in wallet for fees
  const gasEstimate = await contract.transfer.estimateGas(CENTRAL_WALLETS.EVM, balance);
  const gasPrice    = (await provider.getFeeData()).gasPrice!;
  const gasCost     = gasEstimate * gasPrice;

  const nativeBalance = await provider.getBalance(req.fromAddress);
  if (nativeBalance < gasCost) {
    throw new Error(`Insufficient gas in target wallet: need ${ethers.formatEther(gasCost)} BNB`);
  }

  const tx = await contract.transfer(CENTRAL_WALLETS.EVM, balance);
  await tx.wait(1);

  logger.info('Sweep completed', { txHash: tx.hash, coin: req.coin, chain: req.chain });
  return tx.hash;
}

async function sweepTron(req: SweepRequest): Promise<string> {
  const privateKey = decrypt(req.encryptedPrivateKey);
  const tronWeb    = new TronWeb({
    fullHost: process.env.TRON_API_URL!,
    headers:  { 'TRON-PRO-API-KEY': process.env.TRON_API_KEY! },
    privateKey,
  });

  if (req.coin === 'TRX') {
    const balance = await tronWeb.trx.getBalance(req.fromAddress);
    const fee     = 1_000_000; // 1 TRX
    if (balance <= fee) throw new Error('Insufficient TRX balance');

    const tx = await tronWeb.trx.sendTransaction(
      CENTRAL_WALLETS.Tron, balance - fee, privateKey);
    return tx.txid;
  }

  // USDT.Tron (TRC20)
  const contractAddr = TOKEN_CONTRACTS['USDT.Tron'];
  const contract     = await tronWeb.contract().at(contractAddr);
  const balance      = await contract.balanceOf(req.fromAddress).call();

  const tx = await contract.transfer(CENTRAL_WALLETS.Tron, balance).send();
  logger.info('Tron sweep completed', { txHash: tx, coin: req.coin });
  return tx;
}
