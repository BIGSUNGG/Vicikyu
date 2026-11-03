using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConvertToSemesterMembers : Migration
    {
        /// <inheritdoc />
        /// <remarks>기존 Attendance(라이브 OTP·가져오기 이력)을 (부원×학기) SemesterMember 행으로 자동 이관·보존한다.
        /// UnmappedMember/UnmappedAttendance 임시 데이터는 폐기한다(사용자 결정).</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SemesterMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SemesterId = table.Column<int>(type: "INTEGER", nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MappedUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemesterMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SemesterMembers_SemesterPeriods_SemesterId",
                        column: x => x.SemesterId,
                        principalTable: "SemesterPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SemesterAttendances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SemesterMemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: false),
                    AttendanceType = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemesterAttendances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SemesterAttendances_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SemesterAttendances_SemesterMembers_SemesterMemberId",
                        column: x => x.SemesterMemberId,
                        principalTable: "SemesterMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SemesterAttendances_ActivityId",
                table: "SemesterAttendances",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_SemesterAttendances_SemesterMemberId_ActivityId",
                table: "SemesterAttendances",
                columns: new[] { "SemesterMemberId", "ActivityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SemesterMembers_MappedUserId",
                table: "SemesterMembers",
                column: "MappedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SemesterMembers_MappedUserId_SemesterId",
                table: "SemesterMembers",
                columns: new[] { "MappedUserId", "SemesterId" },
                unique: true,
                filter: "\"MappedUserId\" IS NOT NULL AND \"SemesterId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SemesterMembers_SemesterId",
                table: "SemesterMembers",
                column: "SemesterId");

            // 1) 학기별 부원 행 생성 — 각 (부원, 활동 날짜 소속 학기) 조합마다 한 행. 학기 밖 날짜는 미분류(null).
            //    라벨은 계정 파생 형식('이름 학번 학과')으로 합성하고 즉시 매핑한다.
            migrationBuilder.Sql("""
                WITH pairs AS (
                  SELECT a."UserId" AS uid,
                         (SELECT MIN(sp."Id") FROM "SemesterPeriods" sp
                           WHERE act."ActivityDate" >= sp."StartDate" AND act."ActivityDate" <= sp."EndDate") AS sem,
                         MIN(a."CreatedAt") AS first_seen
                  FROM "Attendances" a
                  JOIN "Activities" act ON act."Id" = a."ActivityId"
                  GROUP BY a."UserId", sem
                )
                INSERT INTO "SemesterMembers" ("SemesterId", "Label", "Name", "MappedUserId", "CreatedAt")
                SELECT p.sem,
                  TRIM(COALESCE(u."Name", '') || ' ' || COALESCE(u."StudentNumber", '') || ' ' || COALESCE(u."Department", '')),
                  COALESCE(u."Name", ''),
                  p.uid,
                  p.first_seen
                FROM pairs p
                JOIN "AspNetUsers" u ON u."Id" = p.uid
                WHERE NOT EXISTS (
                  SELECT 1 FROM "SemesterMembers" m
                  WHERE m."MappedUserId" = p.uid AND m."SemesterId" IS p.sem)
                """);

            // 2) 출석 이관 — 유형·체크 시각 보존. 기존 (ActivityId, UserId) unique 덕에 원본 중복은 없다.
            migrationBuilder.Sql("""
                INSERT INTO "SemesterAttendances" ("SemesterMemberId", "ActivityId", "AttendanceType", "CheckedAt", "CreatedAt")
                SELECT m."Id", a."ActivityId", a."AttendanceType", a."CheckedAt", a."CreatedAt"
                FROM "Attendances" a
                JOIN "Activities" act ON act."Id" = a."ActivityId"
                JOIN "AspNetUsers" u ON u."Id" = a."UserId"
                JOIN "SemesterMembers" m ON m."MappedUserId" = a."UserId"
                  AND m."SemesterId" IS (SELECT MIN(sp."Id") FROM "SemesterPeriods" sp
                     WHERE act."ActivityDate" >= sp."StartDate" AND act."ActivityDate" <= sp."EndDate")
                WHERE NOT EXISTS (
                  SELECT 1 FROM "SemesterAttendances" sa
                  WHERE sa."SemesterMemberId" = m."Id" AND sa."ActivityId" = a."ActivityId")
                """);

            // 3) 이관 검증 — 한 건이라도 누락되면 FK 위반으로 마이그레이션이 실패한다(트랜잭션 롤백).
            migrationBuilder.Sql("""
                INSERT INTO "SemesterAttendances" ("SemesterMemberId", "ActivityId", "AttendanceType", "CheckedAt", "CreatedAt")
                SELECT -1, -1, 0, '1970-01-01 00:00:00', '1970-01-01 00:00:00'
                WHERE (SELECT COUNT(*) FROM "Attendances") <> (SELECT COUNT(*) FROM "SemesterAttendances")
                """);

            migrationBuilder.DropTable(
                name: "Attendances");

            migrationBuilder.DropTable(
                name: "UnmappedAttendances");

            migrationBuilder.DropTable(
                name: "UnmappedMembers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attendances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: false),
                    AttendanceType = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attendances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attendances_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UnmappedMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnmappedMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UnmappedAttendances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: false),
                    UnmappedMemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnmappedAttendances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnmappedAttendances_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UnmappedAttendances_UnmappedMembers_UnmappedMemberId",
                        column: x => x.UnmappedMemberId,
                        principalTable: "UnmappedMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attendances_ActivityId_UserId",
                table: "Attendances",
                columns: new[] { "ActivityId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendances_UserId",
                table: "Attendances",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UnmappedAttendances_ActivityId",
                table: "UnmappedAttendances",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_UnmappedAttendances_UnmappedMemberId_ActivityId",
                table: "UnmappedAttendances",
                columns: new[] { "UnmappedMemberId", "ActivityId" },
                unique: true);

            // 되돌리기 — 매핑된 행의 출석만 Attendances로 복원한다(미매핑 행 데이터는 이관 시 폐기됨).
            migrationBuilder.Sql("""
                INSERT INTO "Attendances" ("ActivityId", "UserId", "AttendanceType", "CheckedAt", "CreatedAt")
                SELECT sa."ActivityId", m."MappedUserId", sa."AttendanceType", sa."CheckedAt", sa."CreatedAt"
                FROM "SemesterAttendances" sa
                JOIN "SemesterMembers" m ON m."Id" = sa."SemesterMemberId"
                WHERE m."MappedUserId" IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM "Attendances" a
                    WHERE a."ActivityId" = sa."ActivityId" AND a."UserId" = m."MappedUserId")
                """);

            migrationBuilder.DropTable(
                name: "SemesterAttendances");

            migrationBuilder.DropTable(
                name: "SemesterMembers");
        }
    }
}
