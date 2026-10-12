using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneOpenSessionPerDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_ActivityDate",
                table: "Activities");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ActivityDate",
                table: "Activities",
                column: "ActivityDate",
                unique: true,
                filter: "\"Status\" <> 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_ActivityDate",
                table: "Activities");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ActivityDate",
                table: "Activities",
                column: "ActivityDate");
        }
    }
}
