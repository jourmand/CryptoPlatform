import * as bip39 from 'bip39';
import { HDNodeWallet, Mnemonic, getBytes, encodeBase58, Wallet as EthersWallet } from 'ethers';
import { Keypair } from '@solana/web3.js';
import { derivePath } from 'ed25519-hd-key';
import { createCipheriv, createDecipheriv, createHash, randomBytes } from 'crypto';

// ── Encryption helpers ─────────────────────────────────────────────
// Keys are AES-256-GCM encrypted before being sent back to .NET to store

const RAW_KEY = process.env.ENCRYPTION_KEY;
if (!RAW_KEY) throw new Error('ENCRYPTION_KEY environment variable is required — refusing to start with an unsafe fallback key');
const ENCRYPTION_KEY = Buffer.from(RAW_KEY, 'hex');

export function encrypt(plaintext: string): string {
  const iv         = randomBytes(12);
  const cipher     = createCipheriv('aes-256-gcm', ENCRYPTION_KEY, iv);
  const encrypted  = Buffer.concat([cipher.update(plaintext, 'utf8'), cipher.final()]);
  const authTag    = cipher.getAuthTag();
  return [iv.toString('hex'), authTag.toString('hex'), encrypted.toString('hex')].join(':');
}

export function decrypt(ciphertext: string): string {
  const [ivHex, tagHex, dataHex] = ciphertext.split(':');
  const decipher = createDecipheriv('aes-256-gcm', ENCRYPTION_KEY, Buffer.from(ivHex, 'hex'));
  decipher.setAuthTag(Buffer.from(tagHex, 'hex'));
  return decipher.update(Buffer.from(dataHex, 'hex')).toString('utf8') + decipher.final('utf8');
}

// ── EVM (BSC + POL) ────────────────────────────────────────────────

function deriveEVMWallet(phrase: string, index: number) {
  const wallet = HDNodeWallet.fromMnemonic(
    Mnemonic.fromPhrase(phrase),
    `m/44'/60'/0'/0/${index}`
  );
  return { address: wallet.address, privateKey: wallet.privateKey };
}

// ── Tron ───────────────────────────────────────────────────────────

function tronAddressFromPrivateKey(privateKeyHex: string): string {
  // Tron address = Base58Check( 0x41 || last-20-bytes-of-keccak256(pubkey) )
  const wallet  = new EthersWallet(`0x${privateKeyHex}`);
  const addrBytes = getBytes(wallet.address);             // 20 bytes (EIP-55)
  const payload   = Buffer.concat([Buffer.from([0x41]), Buffer.from(addrBytes)]);
  const sha256    = (b: Buffer) => createHash('sha256').update(b).digest();
  const checksum  = sha256(sha256(payload)).slice(0, 4);
  return encodeBase58(Buffer.concat([payload, checksum]));
}

function deriveTronWallet(phrase: string, index: number) {
  const node       = HDNodeWallet.fromMnemonic(Mnemonic.fromPhrase(phrase), `m/44'/195'/0'/0/${index}`);
  const privateKey = node.privateKey.replace(/^0x/, '');
  const address    = tronAddressFromPrivateKey(privateKey);
  return { address, privateKey };
}

// ── Solana ─────────────────────────────────────────────────────────

function deriveSolanaWallet(seed: Buffer, index: number) {
  const path    = `m/44'/501'/${index}'/0'`;
  const { key } = derivePath(path, seed.toString('hex'));
  const keypair = Keypair.fromSeed(key);
  return {
    address:    keypair.publicKey.toBase58(),
    privateKey: Buffer.from(keypair.secretKey).toString('hex'),
  };
}

// ── Main factory ───────────────────────────────────────────────────

export interface WalletSet {
  playerId:    string;
  addresses: {
    usdtTron:   string;
    usdtSolana: string;
    usdtBsc:    string;
    usdcSolana: string;
    usdcBsc:    string;
    trxTron:    string;
    polPol:     string;
  };
  // These go to vault storage via .NET — encrypted
  encryptedKeys: {
    evm:    string;
    tron:   string;
    solana: string;
  };
}

export async function generateWallets(playerId: string, playerIndex: number): Promise<WalletSet> {
  const phrase = bip39.generateMnemonic(256);
  const seed   = await bip39.mnemonicToSeed(phrase);

  const evm    = deriveEVMWallet(phrase, playerIndex);
  const tron   = deriveTronWallet(phrase, playerIndex);
  const solana = deriveSolanaWallet(seed, playerIndex);

  return {
    playerId,
    addresses: {
      usdtTron:   tron.address,
      usdtSolana: solana.address,
      usdtBsc:    evm.address,
      usdcSolana: solana.address,
      usdcBsc:    evm.address,
      trxTron:    tron.address,
      polPol:     evm.address,
    },
    encryptedKeys: {
      evm:    encrypt(evm.privateKey),
      tron:   encrypt(tron.privateKey),
      solana: encrypt(solana.privateKey),
    },
  };
}
