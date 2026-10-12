using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleSessionsPerDay : Migration
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
                column: "ActivityDate");
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
                column: "ActivityDate",
                unique: true);
        }
    }
}
