using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchFolderAutoScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoScan",
                table: "Watch_Folder",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoScan",
                table: "Watch_Folder");
        }
    }
}
