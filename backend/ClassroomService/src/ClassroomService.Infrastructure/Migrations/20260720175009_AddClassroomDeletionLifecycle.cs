using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClassroomService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClassroomDeletionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Classrooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Classrooms",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ClassroomId",
                table: "Sessions",
                column: "ClassroomId");

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_Status",
                table: "Classrooms",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_ClassroomId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Classrooms_Status",
                table: "Classrooms");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Classrooms");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Classrooms");
        }
    }
}
