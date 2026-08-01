using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClassroomService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuizExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuizExtensions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuizId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClosesAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizExtensions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizExtensions_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuizExtensions_QuizId_StudentId",
                table: "QuizExtensions",
                columns: new[] { "QuizId", "StudentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuizExtensions");
        }
    }
}
