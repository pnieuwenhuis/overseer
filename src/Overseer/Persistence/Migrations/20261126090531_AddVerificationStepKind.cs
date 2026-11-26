using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationStepKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "VerificationSteps",
                type: "text",
                nullable: false,
                defaultValue: "Repository");

            // Until now only the check of a Designer Task's screenshots verified no repository.
            migrationBuilder.Sql("""UPDATE "VerificationSteps" SET "Kind" = 'Screenshots' WHERE "Repository" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "VerificationSteps");
        }
    }
}
