using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexOnStreamParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replaced by the composite below, whose leftmost column is StreamId — so every query
            // the old index served is still served, and keeping both would be a second copy of the
            // same thing.
            migrationBuilder.DropIndex(
                name: "IX_Participants_StreamId",
                table: "Participants");

            // FAILS if a stream already holds two rows for one person, which is exactly what this
            // prevents from happening again. Those duplicates are a reconnect or a second tab, not
            // two different people, so removing the later row is safe — but it is a decision for
            // whoever runs the upgrade, not for a migration.
            migrationBuilder.CreateIndex(
                name: "IX_Participants_StreamId_UserId",
                table: "Participants",
                columns: new[] { "StreamId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Participants_StreamId_UserId",
                table: "Participants");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_StreamId",
                table: "Participants",
                column: "StreamId");
        }
    }
}
