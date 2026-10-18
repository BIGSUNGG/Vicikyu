using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PendingSheetWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SheetSyncError",
                table: "Activities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SheetSyncPendingAt",
                table: "Activities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SheetSyncRequiredUserIds",
                table: "Activities",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "SheetSyncRevision",
                table: "Activities",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Older live check-ins had no durable delivery marker. Preserve them conservatively
            // before the first sheet-authoritative refresh after this upgrade.
            migrationBuilder.Sql("""
                UPDATE Activities
                SET SheetSyncPendingAt = CURRENT_TIMESTAMP,
                    SheetSyncRevision = 1,
                    SheetSyncError = '업그레이드 이전 진행 활동: 시트 반영 확인 대기',
                    SheetSyncRequiredUserIds = (
                        SELECT COALESCE(json_group_array(UserId), '[]') FROM (
                            SELECT UserId FROM Attendances WHERE Attendances.ActivityId = Activities.Id
                            UNION
                            SELECT m.UserId FROM SheetMemberMappings m
                            JOIN ActivitySources s ON s.SheetDefinitionId = m.SheetDefinitionId AND s.TabName = m.TabName
                            WHERE s.ActivityId = Activities.Id
                        )
                    )
                WHERE Status IN (1, 2, 3) AND CreatedByUserId <> 'sheet-import';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SheetSyncError",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SheetSyncPendingAt",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SheetSyncRequiredUserIds",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SheetSyncRevision",
                table: "Activities");
        }
    }
}
