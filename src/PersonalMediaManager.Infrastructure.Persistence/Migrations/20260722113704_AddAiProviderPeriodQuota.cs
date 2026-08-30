using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProviderPeriodQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuotaPeriod",
                table: "Parse_AiProvider",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "QuotaPeriodCallLimit",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaPeriodResetAt",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuotaPeriodTimeZone",
                table: "Parse_AiProvider",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaPeriodTokenLimit",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotaPeriodUsedCalls",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "QuotaPeriodUsedTokens",
                table: "Parse_AiProvider",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaPeriod",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaPeriodCallLimit",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaPeriodResetAt",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaPeriodTimeZone",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaPeriodTokenLimit",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaPeriodUsedCalls",
                table: "Parse_AiProvider");

            migrationBuilder.DropColumn(
                name: "QuotaPeriodUsedTokens",
                table: "Parse_AiProvider");
        }
    }
}
