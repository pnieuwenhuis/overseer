using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinalVerificationAndPullRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFixUp",
                table: "IntentTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "AwaitingPullRequests",
                table: "Implementations",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PullRequests",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Repository = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequests", x => new { x.IntentId, x.Repository });
                    table.ForeignKey(
                        name: "FK_PullRequests_Intents_IntentId",
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
                name: "PullRequests");

            migrationBuilder.DropColumn(
                name: "IsFixUp",
                table: "IntentTasks");

            migrationBuilder.DropColumn(
                name: "AwaitingPullRequests",
                table: "Implementations");
        }
    }
}
