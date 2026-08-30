using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CleanEmptyDirIgnoreExtsSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "System_Setting",
                columns: new[] { "Key", "Category", "CreatedAt", "Description", "UpdatedAt", "Value" },
                values: new object[] { "File.CleanEmptyDirIgnoreExts", "General", 1309018226688000000L, "清理空目录时视为「可忽略残留」的扩展名清单（逗号分隔；目录仅剩这些文件 + 子目录递归为空也算空，一并删除；留空=仅清真空目录）", 1309018226688000000L, ".torrent" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "File.CleanEmptyDirIgnoreExts");
        }
    }
}
