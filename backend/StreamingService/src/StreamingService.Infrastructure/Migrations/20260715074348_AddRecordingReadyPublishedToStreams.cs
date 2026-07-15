using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingReadyPublishedToStreams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EgressId",
                table: "Streams",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecordingReadyPublished",
                table: "Streams",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EgressId",
                table: "Streams");

            migrationBuilder.DropColumn(
                name: "RecordingReadyPublished",
                table: "Streams");
        }
    }
}
