using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioProbeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioCodecs",
                table: "Media_Item",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasIncompatibleAudio",
                table: "Media_Item",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.InsertData(
                table: "System_Setting",
                columns: new[] { "Key", "Category", "CreatedAt", "Description", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { "Audio_AutoRemux", "Audio", 1309028843520000000L, "自动重混丢不兼容音轨（开且存在其它兼容音轨时归档用 ffmpeg 流复制去掉 av3a 轨；关=仅标记）", 1309028843520000000L, "false" },
                    { "Audio_FfmpegPath", "Audio", 1309028843520000000L, "ffmpeg / ffprobe 所在目录或 ffmpeg 可执行文件路径（自备，留空=不探测不重混）", 1309028843520000000L, null },
                    { "Audio_IncompatibleCheckEnabled", "Audio", 1309028843520000000L, "音频不兼容轨检查总开关（开则归档前用 ffprobe 探测 av3a 等不兼容音轨并打标）", 1309028843520000000L, "false" },
                    { "Audio_IncompatibleCodecs", "Audio", 1309028843520000000L, "视为不兼容的音频编解码器清单（逗号分隔，小写；默认 av3a 菁彩声；留空=不检查）", 1309028843520000000L, "av3a" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Audio_AutoRemux");

            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Audio_FfmpegPath");

            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Audio_IncompatibleCheckEnabled");

            migrationBuilder.DeleteData(
                table: "System_Setting",
                keyColumn: "Key",
                keyValue: "Audio_IncompatibleCodecs");

            migrationBuilder.DropColumn(
                name: "AudioCodecs",
                table: "Media_Item");

            migrationBuilder.DropColumn(
                name: "HasIncompatibleAudio",
                table: "Media_Item");
        }
    }
}
