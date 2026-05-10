using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence<int>(
                name: "player_index_seq",
                startValue: 0L);

            migrationBuilder.CreateTable(
                name: "deposits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coin = table.Column<string>(type: "text", nullable: false),
                    chain = table.Column<string>(type: "text", nullable: false),
                    tx_hash = table.Column<string>(type: "text", nullable: false),
                    from_address = table.Column<string>(type: "text", nullable: true),
                    to_address = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    confirmations = table.Column<int>(type: "integer", nullable: false),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    credited_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    coin = table.Column<string>(type: "text", nullable: false),
                    chain = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    balance_before = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    reference_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "players",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_players", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "withdrawals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coin = table.Column<string>(type: "text", nullable: false),
                    chain = table.Column<string>(type: "text", nullable: false),
                    to_address = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    fee = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    tx_hash = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coin = table.Column<string>(type: "text", nullable: false),
                    chain = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_balances", x => x.id);
                    table.ForeignKey(
                        name: "fk_balances_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "player_wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coin = table.Column<string>(type: "text", nullable: false),
                    chain = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_wallets", x => x.id);
                    table.ForeignKey(
                        name: "fk_player_wallets_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wallet_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chain_group = table.Column<string>(type: "text", nullable: false),
                    encrypted_private_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_wallet_keys_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_balances_player_id_coin_chain",
                table: "balances",
                columns: new[] { "player_id", "coin", "chain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deposits_tx_hash",
                table: "deposits",
                column: "tx_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_player_wallets_address",
                table: "player_wallets",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_player_wallets_player_id_coin_chain",
                table: "player_wallets",
                columns: new[] { "player_id", "coin", "chain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_players_email",
                table: "players",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_players_username",
                table: "players",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallet_keys_player_id_chain_group",
                table: "wallet_keys",
                columns: new[] { "player_id", "chain_group" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "balances");

            migrationBuilder.DropTable(
                name: "deposits");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "player_wallets");

            migrationBuilder.DropTable(
                name: "wallet_keys");

            migrationBuilder.DropTable(
                name: "withdrawals");

            migrationBuilder.DropTable(
                name: "players");

            migrationBuilder.DropSequence(
                name: "player_index_seq");
        }
    }
}
