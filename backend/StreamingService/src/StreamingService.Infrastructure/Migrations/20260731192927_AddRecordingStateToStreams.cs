using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingStateToStreams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecordingState",
                table: "Streams",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecordingState",
                table: "Streams");
        }
    }
}
