using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations;

/// <summary>
/// 출석 마감 상태 폐지 — 레거시 AttendanceClosed(2) 행을 팀 유무에 따라
/// TeamCreated(3) 또는 AttendanceOpen(1, 활동 중)으로 변환한다.
/// </summary>
public partial class RemoveAttendanceCloseState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "Activities" SET "Status" = 3
            WHERE "Status" = 2 AND EXISTS (SELECT 1 FROM "Teams" t WHERE t."ActivityId" = "Activities"."Id");
            UPDATE "Activities" SET "Status" = 1 WHERE "Status" = 2;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 근사 복원: 출석 마감 시각이 찍힌 행만 마감 상태로 되돌린다(완전 복원은 불가)
        migrationBuilder.Sql(
            """
            UPDATE "Activities" SET "Status" = 2
            WHERE "Status" IN (1, 3) AND "AttendanceClosedAt" IS NOT NULL;
            """);
    }
}
