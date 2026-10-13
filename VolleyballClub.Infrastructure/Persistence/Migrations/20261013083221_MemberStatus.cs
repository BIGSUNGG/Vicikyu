using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemberStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsActive",
                table: "AspNetUsers",
                newName: "Status");

            // 레거시 IsActive(bool) → Status(enum) 값 변환: false(비활성)=0 → Withdrawn=1, true(활성)=1 → Active=0
            migrationBuilder.Sql("UPDATE AspNetUsers SET Status = CASE WHEN Status = 0 THEN 1 ELSE 0 END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 역변환: Withdrawn/Expelled(비활동) → IsActive=true는 표현 불가 — Active만 true로 되돌린다
            migrationBuilder.Sql("UPDATE AspNetUsers SET Status = CASE WHEN Status = 0 THEN 1 ELSE 0 END;");
            migrationBuilder.RenameColumn(
                name: "Status",
                table: "AspNetUsers",
                newName: "IsActive");
        }
    }
}
