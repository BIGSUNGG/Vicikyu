using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminActivitySources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivitySources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SheetDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    TabName = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    IsMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: true)
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
                name: "SheetEditJournals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SpreadsheetId = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: false),
                    MutationJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetEditJournals", x => x.Id);
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivitySources");

            migrationBuilder.DropTable(
                name: "SheetEditJournals");
        }
    }
}
