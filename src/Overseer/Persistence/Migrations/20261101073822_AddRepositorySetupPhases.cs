using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositorySetupPhases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "RepositorySetups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "RepositorySetups",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Repository Setups from before the agent part: no agent wrote their starter files, which Retry has one do.
            migrationBuilder.Sql("""
                UPDATE "RepositorySetups"
                SET "Phase" = 'Failed',
                    "Error" = 'This Repository Setup is from before Overseer had an agent write the starter files. Retry to have it write them.'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Error",
                table: "RepositorySetups");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "RepositorySetups");
        }
    }
}
