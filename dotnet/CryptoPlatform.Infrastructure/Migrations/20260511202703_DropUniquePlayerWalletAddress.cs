using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropUniquePlayerWalletAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_player_wallets_address",
                table: "player_wallets");

            migrationBuilder.CreateIndex(
                name: "ix_player_wallets_address",
                table: "player_wallets",
                column: "address");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_player_wallets_address",
                table: "player_wallets");

            migrationBuilder.CreateIndex(
                name: "ix_player_wallets_address",
                table: "player_wallets",
                column: "address",
                unique: true);
        }
    }
}
