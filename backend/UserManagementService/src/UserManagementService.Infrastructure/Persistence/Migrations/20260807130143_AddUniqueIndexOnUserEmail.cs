using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UserManagementService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexOnUserEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Canonicalise BEFORE constraining. `User.Email` now normalises on write, but rows
            // written before that do not, and a mixed-case row is worse after this migration than
            // before it: every lookup normalises what it is given, so `Jafar@x.com` sitting in the
            // table would match nothing anyone types and the account would be unreachable.
            //
            // EF does not generate this. A migration that only adds the index leaves those rows
            // stranded and still reports success.
            migrationBuilder.Sql(@"UPDATE ""Users"" SET ""Email"" = lower(btrim(""Email""));");

            // If two rows canonicalise to the same address, this FAILS and the deployment stops.
            // That is the intended behaviour: the duplicates are two accounts a real person may
            // have used, possibly with different passwords, roles or approval states, and a
            // migration is not entitled to decide which one survives. Resolve them by hand, then
            // re-run.
            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the constraint only. The original capitalisation is not restored because it is
            // gone — `lower()` is not reversible, and nothing in the system depended on it.
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");
        }
    }
}
