using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubtitleTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Subtitle_Download",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SubtitleName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subtitle_Download", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subtitle_Download_Media_Item_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "Media_Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subtitle_Setting",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    AssrtTokenEncrypted = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AssrtBaseUrl = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                    UseProxy = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subtitle_Setting", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Subtitle_Setting",
                columns: new[] { "Id", "AssrtBaseUrl", "AssrtTokenEncrypted", "CreatedAt", "TimeoutSeconds", "UpdatedAt" },
                values: new object[] { 1L, "https://api.assrt.net", null, 1309016457216000000L, 30, 1309016457216000000L });

            migrationBuilder.CreateIndex(
                name: "IX_Subtitle_Download_MediaItemId_CreatedAt",
                table: "Subtitle_Download",
                columns: new[] { "MediaItemId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Subtitle_Download");

            migrationBuilder.DropTable(
                name: "Subtitle_Setting");
        }
    }
}
