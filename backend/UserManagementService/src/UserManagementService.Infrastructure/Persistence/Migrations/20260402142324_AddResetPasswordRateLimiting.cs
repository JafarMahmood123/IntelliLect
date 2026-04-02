using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UserManagementService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResetPasswordRateLimiting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastRequestedAtUtc",
                table: "ResetPasswordTokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestCount",
                table: "ResetPasswordTokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRequestedAtUtc",
                table: "ResetPasswordTokens");

            migrationBuilder.DropColumn(
                name: "RequestCount",
                table: "ResetPasswordTokens");
        }
    }
}
