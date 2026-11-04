using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VolleyballClub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeleteReadyActivities : Migration
    {
        /// <inheritdoc />
        /// <remarks>활동 시작 전 상태(Ready=0) 폐지 — 기존 Ready 행은 전량 삭제한다.
        /// Ready 행은 시작 전이므로 출석/팀/게스트를 가질 수 없어 안전하다.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"Activities\" WHERE \"Status\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 삭제된 Ready 행은 복원하지 않는다(폐기된 상태값).
        }
    }
}
