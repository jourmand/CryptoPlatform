using CryptoPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPlatform.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Player>       Players       => Set<Player>();
    public DbSet<PlayerWallet> PlayerWallets => Set<PlayerWallet>();
    public DbSet<WalletKey>    WalletKeys    => Set<WalletKey>();
    public DbSet<Balance>      Balances      => Set<Balance>();
    public DbSet<LedgerEntry>  LedgerEntries => Set<LedgerEntry>();
    public DbSet<Deposit>      Deposits      => Set<Deposit>();
    public DbSet<Withdrawal>   Withdrawals   => Set<Withdrawal>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasSequence<int>("player_index_seq").StartsAt(1).IncrementsBy(1);

        b.Entity<Player>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<PlayerWallet>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Address);
            e.HasIndex(x => new { x.PlayerId, x.Coin, x.Chain }).IsUnique();
            e.HasOne(x => x.Player).WithMany(x => x.Wallets).HasForeignKey(x => x.PlayerId);
        });

        b.Entity<WalletKey>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PlayerId, x.ChainGroup }).IsUnique();
            e.HasOne(x => x.Player).WithMany().HasForeignKey(x => x.PlayerId);
        });

        b.Entity<Balance>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PlayerId, x.Coin, x.Chain }).IsUnique();
            e.Property(x => x.Amount).HasPrecision(28, 8);
        });

        b.Entity<LedgerEntry>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(28, 8);
            e.Property(x => x.BalanceBefore).HasPrecision(28, 8);
            e.Property(x => x.BalanceAfter).HasPrecision(28, 8);
        });

        b.Entity<Deposit>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TxHash).IsUnique();
            e.Property(x => x.Amount).HasPrecision(28, 8);
            e.Property(x => x.Status).HasConversion<string>();
        });

        b.Entity<Withdrawal>(e => {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(28, 8);
            e.Property(x => x.Fee).HasPrecision(28, 8);
            e.Property(x => x.Status).HasConversion<string>();
        });
    }
}
