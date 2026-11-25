using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImplementations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Implementations",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    Repository = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Implementations", x => x.IntentId);
                    table.ForeignKey(
                        name: "FK_Implementations_Intents_IntentId",
                        column: x => x.IntentId,
                        principalTable: "Intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Implementations");
        }
    }
}
