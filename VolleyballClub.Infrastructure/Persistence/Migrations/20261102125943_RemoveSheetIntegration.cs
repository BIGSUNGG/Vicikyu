using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSheetIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 시트 통합 제거 — 원본 테이블·칼럼 삭제. DayMarks는 기존 예정/없음 표시를 보존하기 위해
            // SheetDayMarks 테이블을 이름만 바꿔 재사용한다(데이터 유지).
            migrationBuilder.DropTable(
                name: "ActivitySources");

            migrationBuilder.DropTable(
                name: "SheetEditJournals");

            migrationBuilder.DropTable(
                name: "SheetMemberMappings");

            migrationBuilder.DropTable(
                name: "SheetDefinitions");

            migrationBuilder.DropColumn(
                name: "SheetExportedAt",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SheetSyncError",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SheetSyncRequiredUserIds",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SheetSyncRevision",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SheetSyncPendingAt",
                table: "Activities");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Activities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.RenameTable(
                name: "SheetDayMarks",
                newName: "DayMarks");

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "DayMarks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UnmappedMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    UnmappedMemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: false),
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
                name: "IX_UnmappedAttendances_ActivityId",
                table: "UnmappedAttendances",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_UnmappedAttendances_UnmappedMemberId_ActivityId",
                table: "UnmappedAttendances",
                columns: new[] { "UnmappedMemberId", "ActivityId" },
                unique: true);

            // 구글 서비스 계정 키 설정 제거
            migrationBuilder.Sql("DELETE FROM \"Settings\" WHERE \"Key\" = 'Google:ServiceAccountJson';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnmappedAttendances");

            migrationBuilder.DropTable(
                name: "UnmappedMembers");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "DayMarks");

            migrationBuilder.RenameTable(
                name: "DayMarks",
                newName: "SheetDayMarks");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Activities");

            migrationBuilder.AddColumn<DateTime>(
                name: "SheetSyncPendingAt",
                table: "Activities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SheetExportedAt",
                table: "Activities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SheetSyncError",
                table: "Activities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SheetSyncRequiredUserIds",
                table: "Activities",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SheetSyncRevision",
                table: "Activities",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "SheetDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SpreadsheetId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetEditJournals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: false),
                    MutationJson = table.Column<string>(type: "TEXT", nullable: false),
                    SpreadsheetId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetEditJournals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActivitySources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    SheetDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    TabName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivitySources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivitySources_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ActivitySources_SheetDefinitions_SheetDefinitionId",
                        column: x => x.SheetDefinitionId,
                        principalTable: "SheetDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SheetMemberMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SheetDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    RowLabel = table.Column<string>(type: "TEXT", nullable: false),
                    TabName = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetMemberMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SheetMemberMappings_SheetDefinitions_SheetDefinitionId",
                        column: x => x.SheetDefinitionId,
                        principalTable: "SheetDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySources_ActivityId",
                table: "ActivitySources",
                column: "ActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySources_SheetDefinitionId",
                table: "ActivitySources",
                column: "SheetDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetDefinitions_Name",
                table: "SheetDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SheetMemberMappings_SheetDefinitionId_TabName_RowLabel",
                table: "SheetMemberMappings",
                columns: new[] { "SheetDefinitionId", "TabName", "RowLabel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SheetMemberMappings_SheetDefinitionId_TabName_UserId",
                table: "SheetMemberMappings",
                columns: new[] { "SheetDefinitionId", "TabName", "UserId" },
                unique: true);
        }
    }
}
