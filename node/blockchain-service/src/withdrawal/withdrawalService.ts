import { ethers } from 'ethers';
import TronWeb from 'tronweb';
import { logger } from '../index';

const ERC20_ABI = [
  'function transfer(address to, uint256 amount) returns (bool)',
];

const TOKEN_CONTRACTS: Record<string, string> = {
  'USDT.BSC':  '0x55d398326f99059fF775485246999027B3197955',
  'USDC.BSC':  '0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d',
  'USDT.Tron': 'TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t',
};

// Central wallet private keys — loaded from env / vault at startup
const CENTRAL_KEYS = {
  EVM:  process.env.CENTRAL_KEY_EVM!,
  Tron: process.env.CENTRAL_KEY_TRON!,
};

export interface WithdrawalRequest {
  withdrawalId: string;
  coin:         string;
  chain:        string;
  toAddress:    string;
  amount:       number;
}

export async function executeWithdrawal(req: WithdrawalRequest): Promise<{ txHash: string; fee: number }> {
  if (req.chain === 'BSC' || req.chain === 'POL') {
    return withdrawEVM(req);
  } else if (req.chain === 'Tron') {
    return withdrawTron(req);
  }
  throw new Error(`Unsupported chain: ${req.chain}`);
}

async function withdrawEVM(req: WithdrawalRequest): Promise<{ txHash: string; fee: number }> {
  const provider = new ethers.JsonRpcProvider(process.env.BSC_RPC_URL!);
  const signer   = new ethers.Wallet(CENTRAL_KEYS.EVM, provider);

  const contractAddr = TOKEN_CONTRACTS[`${req.coin}.${req.chain}`];
  const contract     = new ethers.Contract(contractAddr, ERC20_ABI, signer);

  const amount = ethers.parseUnits(req.amount.toString(), 18);
  const tx     = await contract.transfer(req.toAddress, amount);
  const receipt = await tx.wait(1);

  const fee = parseFloat(ethers.formatEther(
    receipt.gasUsed * receipt.gasPrice
  ));

  logger.info('EVM withdrawal sent', { withdrawalId: req.withdrawalId, txHash: tx.hash, fee });
  return { txHash: tx.hash, fee };
}

async function withdrawTron(req: WithdrawalRequest): Promise<{ txHash: string; fee: number }> {
  const tronWeb = new TronWeb({
    fullHost:   process.env.TRON_API_URL!,
    headers:    { 'TRON-PRO-API-KEY': process.env.TRON_API_KEY! },
    privateKey: CENTRAL_KEYS.Tron,
  });

  if (req.coin === 'TRX') {
    const sunAmount = Math.floor(req.amount * 1_000_000);
    const tx = await tronWeb.trx.sendTransaction(req.toAddress, sunAmount, CENTRAL_KEYS.Tron);
    return { txHash: tx.txid, fee: 1 }; // ~1 TRX fee
  }

  const contractAddr = TOKEN_CONTRACTS['USDT.Tron'];
  const contract     = await tronWeb.contract().at(contractAddr);
  const amount       = Math.floor(req.amount * 1_000_000); // USDT has 6 decimals on Tron
  const txHash       = await contract.transfer(req.toAddress, amount).send();

  logger.info('Tron withdrawal sent', { withdrawalId: req.withdrawalId, txHash });
  return { txHash, fee: 1 };
}
