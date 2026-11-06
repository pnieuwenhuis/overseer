using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntentTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntentTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<string>(type: "text", nullable: true),
                    To = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntentTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntentTransitions_Intents_IntentId",
                        column: x => x.IntentId,
                        principalTable: "Intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntentTransitions_IntentId",
                table: "IntentTransitions",
                column: "IntentId");

            // The Intents captured before transitions were recorded get their capture as a Draft. One that has left Draft could only
            // have been started, at a time that isn't known: IntentLifecycle.RecordStartsBeforeTransitionsAsync records that at the
            // first start after this upgrade, by Overseer's clock.
            migrationBuilder.Sql("""
                INSERT INTO "IntentTransitions" ("IntentId", "From", "To", "At")
                SELECT "Id", NULL, 'Draft', "CreatedAt" FROM "Intents";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntentTransitions");
        }
    }
}
