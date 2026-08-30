using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProviderQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuotaCallLimit",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaExceededAt",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaExpiresAt",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaTokenLimit",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaUsedCalls",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "QuotaUsedTokens",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaCallLimit",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaExceededAt",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaExpiresAt",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaTokenLimit",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaUsedCalls",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaUsedTokens",
                table: "Parse_AiProvider");
        }
    }
}
