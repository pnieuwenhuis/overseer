using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpecVersions",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    WrittenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Approach = table.Column<string>(type: "text", nullable: false),
                    AcceptanceCriteria = table.Column<string>(type: "text", nullable: false),
                    Dependencies = table.Column<string>(type: "text", nullable: false),
                    ApiEndpointChanges = table.Column<string>(type: "text", nullable: true),
                    DataModelChanges = table.Column<string>(type: "text", nullable: true),
                    UiChanges = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecVersions", x => new { x.IntentId, x.Number });
                    table.ForeignKey(
                        name: "FK_SpecVersions_Intents_IntentId",
                        column: x => x.IntentId,
                        principalTable: "Intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpecDecisions",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpecVersion = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    IsAnalystsChoice = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecDecisions", x => new { x.IntentId, x.SpecVersion, x.Number });
                    table.ForeignKey(
                        name: "FK_SpecDecisions_SpecVersions_IntentId_SpecVersion",
                        columns: x => new { x.IntentId, x.SpecVersion },
                        principalTable: "SpecVersions",
                        principalColumns: new[] { "IntentId", "Number" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpecTasks",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpecVersion = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Repositories = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecTasks", x => new { x.IntentId, x.SpecVersion, x.Number });
                    table.ForeignKey(
                        name: "FK_SpecTasks_SpecVersions_IntentId_SpecVersion",
                        columns: x => new { x.IntentId, x.SpecVersion },
                        principalTable: "SpecVersions",
                        principalColumns: new[] { "IntentId", "Number" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpecDecisions");

            migrationBuilder.DropTable(
                name: "SpecTasks");

            migrationBuilder.DropTable(
                name: "SpecVersions");
        }
    }
}
