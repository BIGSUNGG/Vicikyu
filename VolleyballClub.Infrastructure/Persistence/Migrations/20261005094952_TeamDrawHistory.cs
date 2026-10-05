using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamDrawHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            // 구모델에서 TeamCreated 이상은 이미 확정된 것 — 기존 팀을 1회차로 확정 처리
            // (ActivityClosed 포함 — 종료된 과거 활동의 기록도 계속 팀 배정을 보여줘야 한다)
            migrationBuilder.Sql("UPDATE \"Activities\" SET \"ConfirmedDrawNumber\" = 1 WHERE \"Status\" >= 3;");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_ActivityId_DrawNumber",
                table: "Teams",
                columns: new[] { "ActivityId", "DrawNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
    }
}
