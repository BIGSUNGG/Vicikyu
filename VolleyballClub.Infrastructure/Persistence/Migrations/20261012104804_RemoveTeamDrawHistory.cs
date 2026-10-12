using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTeamDrawHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 데이터 정리: 마감 활동의 팀은 전부 삭제, 진행 중 활동은 최신 회차만 남긴 뒤 컬럼 제거
            migrationBuilder.Sql("""
                DELETE FROM "Teams" WHERE "ActivityId" IN (SELECT "Id" FROM "Activities" WHERE "Status" = 4);
                DELETE FROM "Teams" WHERE "DrawNumber" <> (
                    SELECT MAX(t2."DrawNumber") FROM "Teams" t2 WHERE t2."ActivityId" = "Teams"."ActivityId");
                """);

            migrationBuilder.DropIndex(
                name: "IX_Teams_ActivityId_DrawNumber",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "DrawNumber",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ConfirmedDrawNumber",
                table: "Activities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DrawNumber",
                table: "Teams",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ConfirmedDrawNumber",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_ActivityId_DrawNumber",
                table: "Teams",
                columns: new[] { "ActivityId", "DrawNumber" });
        }
    }
}
