using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaItemAiInvolved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiInvolved",
                table: "Media_Item",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // 历史数据回填（幂等，重复执行结果一致）：
            //   ParseSource='Ai'（TEXT 存枚举名）= 结果确定采纳自 AI；
            //   Audit_AiCall 有 MediaItemId 关联 = 有过真实 AI 调用记录（含失败/混合场景）。
            migrationBuilder.Sql("""
                UPDATE Media_Item SET AiInvolved = 1
                WHERE ParseSource = 'Ai'
                   OR Id IN (SELECT DISTINCT MediaItemId FROM Audit_AiCall WHERE MediaItemId IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiInvolved",
                table: "Media_Item");
        }
    }
}
