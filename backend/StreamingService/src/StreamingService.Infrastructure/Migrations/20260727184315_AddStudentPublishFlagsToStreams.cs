using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentPublishFlagsToStreams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StudentsCanPublishAudio",
                table: "Streams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "StudentsCanPublishVideo",
                table: "Streams",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StudentsCanPublishAudio",
                table: "Streams");

            migrationBuilder.DropColumn(
                name: "StudentsCanPublishVideo",
                table: "Streams");
        }
    }
}
