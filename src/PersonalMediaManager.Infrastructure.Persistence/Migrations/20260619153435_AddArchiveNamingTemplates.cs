using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveNamingTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "System_Setting",
                columns: new[] { "Key", "Category", "CreatedAt", "Description", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { "Archive_MovieNameTemplate", "Archive", 1309029728256000000L, "电影目录名 + 文件名主体模板（{tmdb-NNN} 后缀由系统自动追加）。占位符：{title} {originaltitle} {year} {tmdbid} {edition}", 1309029728256000000L, "{title} ({year})" },
                    { "Archive_SeasonFolderIncludeTitle", "Archive", 1309029728256000000L, "季目录是否追加季标题后缀（true=「Season 03 锻刀村篇」；false=纯「Season 03」）。季目录 Season NN 前缀本身锁定不可改", 1309029728256000000L, "true" },
                    { "Archive_TvEpisodeNameTemplate", "Archive", 1309029728256000000L, "单集文件名主体模板（必含 {episode}=SxxEyy）。占位符：{title} {originaltitle} {year} {seasonyear} {tmdbid} {episode}", 1309029728256000000L, "{title} ({year}) - {episode}" },
                    { "Archive_TvShowFolderTemplate", "Archive", 1309029728256000000L, "剧集根目录主体模板（{tmdb-NNN} 后缀由系统自动追加）。占位符：{title} {originaltitle} {year} {tmdbid}", 1309029728256000000L, "{title} ({year})" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Archive_MovieNameTemplate");

            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Archive_SeasonFolderIncludeTitle");

            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Archive_TvEpisodeNameTemplate");

            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Archive_TvShowFolderTemplate");
        }
    }
}
